using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace FMSC_SMS.Services.Backup;

/// <summary>
/// Minimal HTTP callback on 127.0.0.1, implemented with TcpListener instead
/// of Windows HTTP.sys. No administrator privileges or URLACL reservations
/// are required just to connect a school cloud account. Only a random PKCE
/// authorization code paired with the exact anti-CSRF state is accepted.
/// </summary>
internal sealed class CloudOAuthLoopback : IDisposable
{
    private readonly TcpListener listener;
    internal int Port { get; }
    internal string CallbackUrl => $"http://127.0.0.1:{Port}/fmsc-backup/";

    internal CloudOAuthLoopback(int port = 0)
    {
        listener = new TcpListener(IPAddress.Loopback, port);
        try { listener.Start(); }
        catch (SocketException e)
        {
            throw new IOException(
                "The cloud authorization callback port is unavailable. Close other sign-in sessions and retry.", e);
        }
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    internal async Task<string> ReceiveCodeAsync(string expectedState, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedState))
            throw new ArgumentException("OAuth state cannot be empty.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        int attempts = 0;
        while (attempts++ < 20)
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var input = client.GetStream();
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                using var reader = new StreamReader(input, Encoding.ASCII,
                    detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                string line = await reader.ReadLineAsync(requestTimeout.Token) ?? "";
                if (line.Length is < 12 or > 4096)
                    throw new InvalidDataException("Invalid cloud authorization request.");
                int headerLength = line.Length;
                while (true)
                {
                    string header = await reader.ReadLineAsync(requestTimeout.Token) ?? "";
                    headerLength += header.Length;
                    if (headerLength > 16_384)
                        throw new InvalidDataException("Cloud authorization request is too large.");
                    if (header.Length == 0) break;
                }
                string[] parts = line.Split(' ');
                bool validRequest = parts.Length == 3 &&
                    parts[0] == "GET" && parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal);
                if (!validRequest || !Uri.TryCreate("http://127.0.0.1:" + Port + parts[1],
                    UriKind.Absolute, out var url) ||
                    !url.Host.Equals("127.0.0.1", StringComparison.Ordinal) ||
                    url.Port != Port || url.AbsolutePath != "/fmsc-backup/")
                {
                    await RespondAsync(input, false, requestTimeout.Token);
                    continue;
                }
                var parameters = ParseQuery(url.Query);
                parameters.TryGetValue("state", out var suppliedState);
                parameters.TryGetValue("code", out var code);
                suppliedState ??= "";
                code ??= "";
                bool stateMatches = CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(Encoding.UTF8.GetBytes(suppliedState)),
                    SHA256.HashData(Encoding.UTF8.GetBytes(expectedState)));
                bool accepted = stateMatches && code.Length > 8 && code.Length < 4096;
                await RespondAsync(input, accepted, requestTimeout.Token);
                if (accepted) return code;
                if (stateMatches && parameters.TryGetValue("error", out var error) && error.Length > 0)
                    throw new InvalidOperationException("Cloud sign-in was declined. You can try again.");
                // An unrelated loopback request must not steal or cancel the
                // real browser authorization attempt.
            }
            catch (OperationCanceledException) when (!timeout.IsCancellationRequested)
            {
                // A stalled local connection cannot block the OAuth listener.
            }
            catch (IOException) { /* Ignore unrelated malformed local probes. */ }
            catch (InvalidDataException) { /* Continue waiting for the real callback. */ }
        }
        throw new InvalidOperationException("Too many invalid cloud sign-in callbacks were received.");
    }

    private static Dictionary<string,string> ParseQuery(string query)
    {
        var result = new Dictionary<string,string>(StringComparer.Ordinal);
        foreach (string part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int index = part.IndexOf('=');
            if (index < 1) continue;
            string name = WebUtility.UrlDecode(part[..index]);
            string value = WebUtility.UrlDecode(part[(index + 1)..]);
            if (!result.TryAdd(name, value))
                throw new InvalidDataException("Duplicate OAuth query field.");
        }
        return result;
    }

    private static async Task RespondAsync(Stream output, bool approved, CancellationToken token)
    {
        byte[] body = Encoding.UTF8.GetBytes(approved
            ? "<html><body><h2>FMSC authorization received</h2><p>Return to FMSC to finish connecting your account.</p></body></html>"
            : "<html><body><h2>FMSC authorization rejected</h2><p>Return to FMSC and retry.</p></body></html>");
        string header = "HTTP/1.1 " + (approved ? "200 OK" : "400 Bad Request") +
            "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " +
            body.Length + "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        byte[] prefix = Encoding.ASCII.GetBytes(header);
        await output.WriteAsync(prefix, token);
        await output.WriteAsync(body, token);
        await output.FlushAsync(token);
    }

    public void Dispose() => listener.Stop();
}
