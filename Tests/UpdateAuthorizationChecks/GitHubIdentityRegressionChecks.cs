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
        using var publisherHttp = Fake(account, updates, publisher: true);
        Check(GitHubRepositoryIdentity.ResolveUpdatesHttpsRemoteAsync("synthetic-token", publisherHttp).GetAwaiter().GetResult() ==
            "https://github.com/future-renamed-owner/FMSC-Updates.git",
            "Private publisher remote must use verified numeric repository identity.");
        using var rateLimitedHttp = Fake(account, updates, publisher: true, rateLimitResponses: 1);
        Check(GitHubRepositoryIdentity.ResolveUpdatesHttpsRemoteAsync("synthetic-token", rateLimitedHttp)
            .GetAwaiter().GetResult() ==
            "https://github.com/future-renamed-owner/FMSC-Updates.git",
            "GitHub API rate-limit 403 must retry without losing publisher authentication.");
        var denied = new FakeHandler(account, updates, publisher: true, accessDenied: true);
        using var deniedHttp = new HttpClient(denied);
        try
        {
            GitHubRepositoryIdentity.ResolveUpdatesHttpsRemoteAsync("synthetic-token", deniedHttp)
                .GetAwaiter().GetResult();
            throw new Exception("Authorization refusal was incorrectly accepted.");
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Forbidden) { }
        Check(denied.Requests == 1, "Ordinary 403 authorization refusal must not be retried.");

        Reject(() => GitHubRepositoryIdentity.ResolveOwnerLoginAsync(
            Fake("{\"id\":1,\"type\":\"User\",\"login\":\"future-renamed-owner\"}", updates))
            .GetAwaiter().GetResult(), "Different owner account ID");
        Reject(() => GitHubRepositoryIdentity.ResolveOwnerLoginAsync(
            Fake("{\"id\":316891609,\"type\":\"User\",\"login\":\"wrong/name\"}", updates))
            .GetAwaiter().GetResult(), "Invalid login");
        Reject(() => GitHubRepositoryIdentity.ResolveUpdatesHttpsRemoteAsync("synthetic-token",
            Fake(account, updates.Replace("1445675367", "1", StringComparison.Ordinal), publisher: true))
            .GetAwaiter().GetResult(), "Replacement repository with same name");
        Reject(() => GitHubRepositoryIdentity.ResolveUpdatesHttpsRemoteAsync("synthetic-token",
            Fake(account, updates.Replace("\"id\":316891609", "\"id\":123", StringComparison.Ordinal), publisher: true))
            .GetAwaiter().GetResult(), "Ownership transfer");
        Reject(() => GitHubRepositoryIdentity.ResolveUpdatesHttpsRemoteAsync("synthetic-token",
            Fake(account, updates.Replace("\"private\":true", "\"private\":false", StringComparison.Ordinal), publisher: true))
            .GetAwaiter().GetResult(), "Private repository made public");
        Reject(() => GitHubRepositoryIdentity.ResolveUpdatesHttpsRemoteAsync("",
            Fake(account, updates, publisher: true)).GetAwaiter().GetResult(), "Missing publisher metadata token");
        Console.WriteLine("PASS: immutable-ID owner rename, authenticated private publication, rate-limit recovery, forbidden refusal, Git SSH compatibility, repository ID, ownership and visibility.");
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

    private static HttpClient Fake(string account, string updates,
        bool publisher = false, int rateLimitResponses = 0) =>
        new(new FakeHandler(account, updates, publisher, rateLimitResponses));

    private sealed class FakeHandler(string account, string updates,
        bool publisher = false, int rateLimitResponses = 0,
        bool accessDenied = false) : HttpMessageHandler
    {
        private int remainingRateLimitResponses = rateLimitResponses;
        internal int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            string path = request.RequestUri?.AbsoluteUri ?? "";
            string value;
            if (path == "https://api.github.com/user/316891609")
            {
                if (publisher)
                    Check(request.Headers.Authorization?.Scheme == "Bearer" &&
                          request.Headers.Authorization.Parameter == "synthetic-token",
                        "Publisher owner-ID lookup must use the scoped token, not anonymous runner quota.");
                else
                    Check(request.Headers.Authorization is null,
                        "Read-only SSH owner lookup must not transmit a publisher token.");
                value = account;
            }
            else if (path == "https://api.github.com/repos/future-renamed-owner/FMSC-Updates")
            {
                Check(request.Headers.Authorization?.Scheme == "Bearer" &&
                      request.Headers.Authorization.Parameter == "synthetic-token",
                    "Private publisher repository lookup must use the configured scoped token.");
                value = updates;
            }
            else throw new Exception("Unexpected or legacy username-dependent URL: " + path);
            if (accessDenied)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    RequestMessage = request,
                    Content = new StringContent("{\"message\":\"Resource not accessible by integration\"}",
                        Encoding.UTF8, "application/json")
                });
            if (remainingRateLimitResponses-- > 0)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    RequestMessage = request,
                    Content = new StringContent("{\"message\":\"API rate limit exceeded\"}",
                        Encoding.UTF8, "application/json")
                };
                limited.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
                return Task.FromResult(limited);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(value, Encoding.UTF8, "application/json")
            });
        }
    }
}
