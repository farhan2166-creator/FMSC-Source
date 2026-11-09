using System.Net;
using System.Text;
using FMSC_SMS.Core.Updates;

internal static class GitHubIdentityRegressionChecks
{
    internal static void Run()
    {
        const string owner = "future-renamed-owner";
        const string account = "{\"id\":316891609,\"type\":\"User\",\"login\":\"future-renamed-owner\"}";
        const string updates = "{\"id\":1445675367,\"name\":\"FMSC-Updates\",\"full_name\":\"future-renamed-owner/FMSC-Updates\",\"private\":true,\"owner\":{\"id\":316891609}}";

        using var http = Fake(account, updates);
        string login = GitHubRepositoryIdentity.ResolveOwnerLoginAsync(http).GetAwaiter().GetResult();
        Check(login == owner, "Account rename was not resolved from permanent account ID.");
        Check(GitHubRepositoryIdentity.ResolveUpdatesSshRemoteAsync(http).GetAwaiter().GetResult() ==
            "git@github.com:future-renamed-owner/FMSC-Updates.git",
            "Existing read-only SSH deploy-key remote must follow current login.");
        Check(GitHubRepositoryIdentity.ResolveUpdatesHttpsRemoteAsync("synthetic-token", http).GetAwaiter().GetResult() ==
            "https://github.com/future-renamed-owner/FMSC-Updates.git",
            "Private publisher remote must use verified numeric repository identity.");

        Reject(() => GitHubRepositoryIdentity.ResolveOwnerLoginAsync(
            Fake("{\"id\":1,\"type\":\"User\",\"login\":\"future-renamed-owner\"}", updates))
            .GetAwaiter().GetResult(), "Different owner account ID");
        Reject(() => GitHubRepositoryIdentity.ResolveOwnerLoginAsync(
            Fake("{\"id\":316891609,\"type\":\"User\",\"login\":\"wrong/name\"}", updates))
            .GetAwaiter().GetResult(), "Invalid login");
        Reject(() => GitHubRepositoryIdentity.ResolveUpdatesHttpsRemoteAsync("synthetic-token",
            Fake(account, updates.Replace("1445675367", "1", StringComparison.Ordinal)))
            .GetAwaiter().GetResult(), "Replacement repository with same name");
        Reject(() => GitHubRepositoryIdentity.ResolveUpdatesHttpsRemoteAsync("synthetic-token",
            Fake(account, updates.Replace("\"id\":316891609", "\"id\":123", StringComparison.Ordinal)))
            .GetAwaiter().GetResult(), "Ownership transfer");
        Reject(() => GitHubRepositoryIdentity.ResolveUpdatesHttpsRemoteAsync("synthetic-token",
            Fake(account, updates.Replace("\"private\":true", "\"private\":false", StringComparison.Ordinal)))
            .GetAwaiter().GetResult(), "Private repository made public");
        Reject(() => GitHubRepositoryIdentity.ResolveUpdatesHttpsRemoteAsync("",
            Fake(account, updates)).GetAwaiter().GetResult(), "Missing publisher metadata token");
        Console.WriteLine("PASS: immutable-ID owner rename, Git SSH compatibility, private repository ID, ownership and visibility refusal.");
    }

    private static void Check(bool passes, string error)
    {
        if (!passes) throw new Exception(error);
    }

    private static void Reject(Action action, string scenario)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        catch (InvalidOperationException) { return; }
        throw new Exception("Fail-closed GitHub identity regression did not refuse: " + scenario);
    }

    private static HttpClient Fake(string account, string updates) => new(new FakeHandler(account, updates));

    private sealed class FakeHandler(string account, string updates) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsoluteUri ?? "";
            string value;
            if (path == "https://api.github.com/user/316891609")
            {
                Check(request.Headers.Authorization is null, "Public account lookup must not transmit any private token.");
                value = account;
            }
            else if (path == "https://api.github.com/repos/future-renamed-owner/FMSC-Updates")
            {
                Check(request.Headers.Authorization?.Scheme == "Bearer" &&
                      request.Headers.Authorization.Parameter == "synthetic-token",
                    "Private publisher lookup must use the configured scoped token.");
                value = updates;
            }
            else throw new Exception("Unexpected or legacy username-dependent URL: " + path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(value, Encoding.UTF8, "application/json")
            });
        }
    }
}
