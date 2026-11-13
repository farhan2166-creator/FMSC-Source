using System.Net;
using FMSC_SMS.Services.Backup;

internal static class AuditFollowupChecks
{
    internal static async Task RunAsync()
    {
        var copy = new CloudRemoteBackup(CloudProvider.Dropbox, "fixture", "fixture.fmscbackup",
            new string('a', 64), DateTime.UtcNow, DateTime.UtcNow, 3);
        Task<IReadOnlyList<CloudRemoteBackup>> Partial(CloudProvider provider, CancellationToken token)
            => provider == CloudProvider.GoogleDrive ? throw new HttpRequestException("fixture outage")
                : Task.FromResult<IReadOnlyList<CloudRemoteBackup>>(new[] { copy });
        int logged = 0;
        var partial = await CloudBackupOperations.BrowseAsync(_ => true, Partial, (_, _) => logged++, default);
        Require(partial.Copies.Count == 1 && partial.Warnings.Count == 1 && logged == 1,
            "Partial inventory must retain working copies and name the failed provider.");
        var failed = await CloudBackupOperations.BrowseAsync(_ => true,
            (_, _) => throw new HttpRequestException("fixture outage"), (_, _) => { }, default);
        Require(failed.Copies.Count == 0 && failed.Warnings.Count == 2, "Unavailable inventories must not appear empty and healthy.");
        var empty = await CloudBackupOperations.BrowseAsync(_ => true,
            (_, _) => Task.FromResult<IReadOnlyList<CloudRemoteBackup>>(Array.Empty<CloudRemoteBackup>()), (_, _) => { }, default);
        Require(empty.Copies.Count == 0 && empty.Warnings.Count == 0, "A valid empty account must remain distinguishable from listing failure.");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); bool observed = false;
            try { await CloudBackupOperations.BrowseAsync(_ => true, Partial, (_, _) => { }, canceled.Token); }
            catch (OperationCanceledException) { observed = true; }
            Require(observed, "Explicit inventory cancellation must propagate.");
        }
        foreach (var outcome in new[] {
            new CloudBackupOutcome(true, false, false, "queued", true),
            new CloudBackupOutcome(true, true, false, "partial", true),
            new CloudBackupOutcome(false, false, false, "retry still pending", true) })
            Require(CloudBackupOperations.ScheduledExitCode(outcome) == 4, "Pending delivery must produce a nonzero scheduled result, even on no-due retry runs.");
        Require(CloudBackupOperations.ScheduledExitCode(new(false, false, false, "no due backup")) == 0,
            "A healthy no-due run must remain successful.");
        Require(CloudBackupOperations.ScheduledExitCode(new(true, true, false, "single selected provider complete")) == 0,
            "Completed selected destinations must not require a disabled provider.");

        string root = Path.Combine(Path.GetTempPath(), "FMSC-AuditFollowup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string destination = Path.Combine(root, "download.fmscbackup");
            using var stalled = new HttpClient(new Reply(() => new StreamContent(new StalledBody())));
            using (var request = new HttpRequestMessage(HttpMethod.Get, "https://fixture.invalid/download"))
            {
                bool timedOut = false;
                try { await CloudBackupTransport.DownloadToFileAsync(request, destination, default, stalled, TimeSpan.FromMilliseconds(100)); }
                catch (TimeoutException) { timedOut = true; }
                Require(timedOut && Directory.GetFiles(root).Length == 0, "A stalled body must time out and remove every partial package.");
            }
            using (var canceled = new CancellationTokenSource(100))
            using (var request = new HttpRequestMessage(HttpMethod.Get, "https://fixture.invalid/download"))
            {
                bool observed = false;
                try { await CloudBackupTransport.DownloadToFileAsync(request, destination, canceled.Token, stalled, TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) { observed = true; }
                Require(observed && Directory.GetFiles(root).Length == 0, "Explicit download cancellation must clean partial data and retain its cancellation meaning.");
            }
            using var healthy = new HttpClient(new Reply(() => new ByteArrayContent(new byte[] { 1, 2, 3 })));
            using var success = new HttpRequestMessage(HttpMethod.Get, "https://fixture.invalid/download");
            var samples = new List<(long Bytes, long? Total)>();
            await CloudBackupTransport.DownloadToFileAsync(success, destination, default, healthy,
                reportTransfer: (bytes, total) => samples.Add((bytes, total)));
            Require(File.ReadAllBytes(destination).SequenceEqual(new byte[] { 1, 2, 3 }) && Directory.GetFiles(root).Length == 1,
                "A complete download must atomically retain only the final package.");
            Require(samples.SequenceEqual(new (long, long?)[] { (0, 3), (3, 3) }),
                "Download progress must report bytes written and the response length.");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine("PASS: continued-audit cloud failure, cancellation, streaming cleanup and scheduled-result checks.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    private sealed class Reply(Func<HttpContent> content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content() });
    }

    private sealed class StalledBody : MemoryStream
    {
        private bool first = true;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (first) { first = false; buffer.Span[0] = 1; return 1; }
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }
    }
}
