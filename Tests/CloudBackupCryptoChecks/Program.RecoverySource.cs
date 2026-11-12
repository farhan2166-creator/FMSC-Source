using FMSC_SMS.Services.Backup;

internal static class RecoverySourceChecks
{
    internal static async Task RunAsync()
    {
        const string id = "fictional-desktop.apps.googleusercontent.com";
        var registration = GoogleDesktopRegistration.Parse(
            """{"installed":{"client_id":"fictional-desktop.apps.googleusercontent.com","client_secret":"fictional-native-metadata"}}""", id);
        foreach (string grant in new[] { "authorization_code", "refresh_token" })
        {
            var fields = GoogleDesktopRegistration.TokenFields(id, registration.ClientSecret, grant);
            if (fields["client_id"] != id || fields["client_secret"] != "fictional-native-metadata" || fields["grant_type"] != grant)
                throw new Exception("Google Desktop token requests must carry the matching native client metadata.");
            if (GoogleDesktopRegistration.TokenFields("dropbox-key", null, grant).ContainsKey("client_secret"))
                throw new Exception("Dropbox public PKCE requests must not inherit Google client metadata.");
        }
        foreach (string invalid in new[] {
            """{"web":{"client_id":"fictional-desktop.apps.googleusercontent.com","client_secret":"forbidden-web-secret"}}""",
            """{"installed":{"client_id":"different-client","client_secret":"fictional-native-metadata"}}""",
            """{"installed":{"client_id":"fictional-desktop.apps.googleusercontent.com"}}""", "null", "{}", "{" })
        {
            try { GoogleDesktopRegistration.Parse(invalid,id); throw new Exception("Invalid Google registration accepted."); }
            catch (InvalidDataException error) {
                if (error.ToString().Contains("forbidden-web-secret")) throw new Exception("Registration leaked client metadata.");
            }
        }
        foreach (Exception failure in new Exception[] { new TimeoutException(), new TaskCanceledException(),
                     new IOException(), new InvalidDataException(), new HttpRequestException() })
        {
            var visited = new List<CloudProvider>();
            var found = await CloudBackupOperations.BrowseAsync(_ => true, (provider, _) => {
                visited.Add(provider);
                if (provider == CloudProvider.GoogleDrive) throw failure;
                return Task.FromResult<IReadOnlyList<CloudRemoteBackup>>(new[] {
                    new CloudRemoteBackup(provider, "valid", "backup", new string('a',64),
                        DateTime.UtcNow, DateTime.UtcNow, 10),
                    new CloudRemoteBackup(provider, "unfinished", "backup", new string('b',64),
                        DateTime.UtcNow, DateTime.UtcNow, 0) });
            }, (_, _) => { }, CancellationToken.None);
            if (visited.Count != 2 || found.Copies.Count != 1 || found.Warnings.Count != 1)
                throw new Exception("One failed provider must not hide the other provider's usable backups.");
            if (!CloudRecoverySourceFailure.CanTryAnotherCopy(failure, CancellationToken.None))
                throw new Exception("A failed pre-SQL source must allow an identical alternate copy.");
        }
        using var cancel = new CancellationTokenSource();
        int calls = 0;
        try
        {
            await CloudBackupOperations.BrowseAsync(_ => true, (_, _) => {
                calls++; cancel.Cancel(); throw new OperationCanceledException(cancel.Token);
            }, (_, _) => throw new Exception("Operator cancellation was reported as provider failure."), cancel.Token);
            throw new Exception("Operator cancellation was swallowed.");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        if (calls != 1 || CloudRecoverySourceFailure.CanTryAnotherCopy(new TimeoutException(), cancel.Token))
            throw new Exception("Operator cancellation must prevent further recovery-source attempts.");
        if (CloudRecoverySourceFailure.CanTryAnotherCopy(new ArgumentException(), CancellationToken.None))
            throw new Exception("Programming errors must not be hidden as provider failure.");
        Console.WriteLine("PASS: cloud timeout isolation, empty upload exclusion and cancellation propagation.");
    }
}
