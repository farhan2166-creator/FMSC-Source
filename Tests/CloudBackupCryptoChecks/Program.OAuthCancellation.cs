using FMSC_SMS.Services.Backup;
using System.Net;

internal static class OAuthCancellationChecks
{
    internal static async Task RunAsync()
    {
        int releasedPort;
        using (var callback = new CloudOAuthLoopback())
        using (var cancellation = new CancellationTokenSource())
        {
            releasedPort = callback.Port;
            Task<string> pending = callback.ReceiveCodeAsync("fictional-expected-state", cancellation.Token);
            if (pending.IsCompleted) throw new Exception("Browser wait did not remain asynchronous.");
            cancellation.Cancel();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(3)); throw new Exception("Cancelled sign-in returned a code."); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        using (var retry = new CloudOAuthLoopback(releasedPort)) { }
        using (var callback = new CloudOAuthLoopback())
        using (var browser = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
        {
            const string state = "fictional-decline-state";
            Task<string> pending = callback.ReceiveCodeAsync(state, CancellationToken.None);
            using var unrelated = await browser.GetAsync(callback.CallbackUrl+"?state=wrong&error=access_denied");
            if (unrelated.StatusCode != HttpStatusCode.BadRequest || pending.IsCompleted)
                throw new Exception("Untrusted error callback cancelled the real sign-in.");
            using var declined = await browser.GetAsync(callback.CallbackUrl+"?state="+state+"&error=access_denied");
            try { await pending.WaitAsync(TimeSpan.FromSeconds(3)); throw new Exception("Declined sign-in returned a code."); }
            catch (InvalidOperationException error) when (error.Message.Contains("declined")) { }
        }
        Console.WriteLine("PASS: abandoned browser wait cancellation, callback-port release and authenticated decline handling.");
    }
}
