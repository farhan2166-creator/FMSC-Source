using FMSC_SMS.Services.Backup;

internal static class RecoverySourceChecks
{
    internal static async Task RunAsync()
    {
        foreach (Exception failure in new Exception[] { new TimeoutException(), new TaskCanceledException(),
                     new IOException(), new InvalidDataException(), new HttpRequestException() })
        {
            var visited = new List<CloudProvider>();
            var found = await CloudBackupOperations.BrowseAsync(_ => true, (provider, _) => {
                visited.Add(provider);
                throw failure;
            }, (_, _) => { }, CancellationToken.None);
            if (visited.Count != 1 || visited[0] != CloudProvider.Dropbox ||
                found.Copies.Count != 0 || found.Warnings.Count != 1)
                throw new Exception("Unavailable Dropbox backup inventory was incorrectly reported healthy.");
            if (!CloudRecoverySourceFailure.CanTryAnotherCopy(failure, CancellationToken.None))
                throw new Exception("A failed pre-SQL source must allow an identical alternate copy.");
        }
        var healthy = await CloudBackupOperations.BrowseAsync(_ => true, (provider, _) =>
            Task.FromResult<IReadOnlyList<CloudRemoteBackup>>(new[] {
                new CloudRemoteBackup(provider, "valid", "backup", new string('a',64),
                    DateTime.UtcNow, DateTime.UtcNow, 10),
                new CloudRemoteBackup(provider, "unfinished", "backup", new string('b',64),
                    DateTime.UtcNow, DateTime.UtcNow, 0)
            }), (_, _) => throw new Exception("Healthy account reported an error."), CancellationToken.None);
        if (healthy.Copies.Count != 1 || healthy.Warnings.Count != 0)
            throw new Exception("Empty Dropbox upload placeholders must not become recoverable backups.");
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
