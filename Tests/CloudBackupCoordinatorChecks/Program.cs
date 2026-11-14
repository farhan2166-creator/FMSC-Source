using System.Reflection;
using System.Text.Json;
using FMSC_SMS.Services.Backup;

var type = typeof(CloudBackupCoordinator);
const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
string root = (string)type.GetField("Root", flags)!.GetValue(null)!;
if (!Path.GetFileName(root).StartsWith("FMSC-BackupFixture-", StringComparison.Ordinal)
    || Directory.Exists(root)) throw new Exception("Only a newly owned disposable queue is allowed.");
string journal = Path.Combine(root, "journal"), queue = Path.Combine(root, "queue");
Directory.CreateDirectory(journal);
Directory.CreateDirectory(queue);
FixtureRuntime.LocalFolder = Path.Combine(root,"local");
Directory.CreateDirectory(FixtureRuntime.LocalFolder);
var settings = CloudBackupSettings.Load();
int checks = 0;
void Check(bool okay, string message) { if (!okay) throw new Exception(message); checks++; }
object? Call(string name, params object?[] arguments) => type.GetMethod(name, flags)!.Invoke(null, arguments);
string Write(string id, bool verified = false, bool superseded = false, bool payload = false,
    bool dropbox = true, int attempts = 0, DateTime? created = null)
{
    string file = Path.Combine(journal, id + ".json");
    File.WriteAllText(file, JsonSerializer.Serialize(new {
        Id = id, Sha256 = new string('a',64), PackageFile = Path.Combine(queue,id+".fmscbackup"),
        SqlCreatedUtc = created ?? DateTime.UtcNow, RequestedDropbox = dropbox,
        DropboxRecoveryVerified = verified, DropboxSuperseded = superseded, DropboxDeliveryAttempts = attempts,
        DropboxId = verified ? "verified-remote" : ""
    }));
    if (payload) File.WriteAllText(Path.Combine(queue,id+".fmscbackup"), "synthetic ciphertext");
    return file;
}
List<string> Issues() => (List<string>)Call("ReadDeliveryIssues", settings)!;
async Task<List<string>> Retry() => await (Task<List<string>>)Call("RetryPendingUploadsCoreAsync", settings, CancellationToken.None, null)!;
try
{
    string id = Guid.NewGuid().ToString("N");
    string path = Write(id);
    Check((await Retry()).Any(x => x.Contains("missing")), "Missing unfinished payload must report incomplete delivery.");
    Check(File.Exists(path), "Missing ciphertext must not delete its journal.");
    Write(id, verified: true);
    Check((await Retry()).Count == 0, "A verified delivery can release its local ciphertext without becoming pending.");
    Write(id, superseded: true);
    Check(Issues().Count == 0, "Safely superseded deliveries must not retain a stale pending status.");
    Write(id, dropbox: false);
    Check(Issues().Count == 0, "A destination never selected must not generate retry obligations.");
    Write(id, dropbox: true);
    Check(Issues().Count == 1 && Issues()[0].StartsWith("Dropbox"), "An unfinished requested Dropbox delivery must be reported.");
    File.Delete(path);
    File.WriteAllText(Path.Combine(journal, "unreadable.json"), "{");
    Check((await Retry()).Count > 0, "Unreadable journals must not produce healthy delivery status.");
    File.Delete(Path.Combine(journal,"unreadable.json"));
    if (OperatingSystem.IsWindows())
    {
        Write(id, payload: true, attempts: 4, created: DateTime.UtcNow.AddDays(-1));
        var initial = await Retry();
        Check(initial.Any(x => x.Contains("exhausted")), "Bounded delivery retries must remain enforced.");
        FixtureRuntime.Connected = true;
        FixtureRuntime.Due = true;
        var outcome = await CloudBackupCoordinator.CreateAndUploadAsync();
        Check(outcome.DropboxSucceeded && !outcome.UploadsPending && FixtureRuntime.Verifications == 1,
            "A newly recovery-verified delivery must clear older exhausted failures in the same create/upload run.");
        Check(Issues().Count == 0, "Final health must reconcile durable supersession rather than old retry errors.");
        Check(!File.Exists(Path.Combine(queue,id+".fmscbackup")), "Only superseded ciphertext can be released.");
        FixtureRuntime.Due = false;
        outcome = await CloudBackupCoordinator.CreateAndUploadAsync(scheduled: true);
        Check(!outcome.UploadsPending, "A successful supersession must produce a healthy no-due scheduled outcome.");
        Write(id);
        outcome = await CloudBackupCoordinator.CreateAndUploadAsync(scheduled: true);
        Check(outcome.UploadsPending && CloudBackupOperations.ScheduledExitCode(outcome) == 4,
            "Scheduled runs must expose missing queued payloads as incomplete.");

        string retainedPayload = Path.Combine(queue, id + ".fmscbackup");
        Write(id, payload: true, attempts: 4, created: DateTime.UtcNow.AddDays(-1));
        using (var payload = File.OpenWrite(retainedPayload)) payload.SetLength(1024 * 1024);
        settings.MaximumPendingQueueMegabytes = 1;
        settings.MaximumLocalBackups = 1;
        string previous = Directory.GetFiles(FixtureRuntime.LocalFolder, "FMSC_Managed_*.bak").Single();
        string separate = Path.Combine(FixtureRuntime.LocalFolder, "manual.bak");
        File.WriteAllText(separate, "synthetic manually retained backup");
        FixtureRuntime.FailBackup = true;
        try
        {
            await CloudBackupCoordinator.CreateAndUploadAsync();
            throw new Exception("Failed SQL backup was reported as successful.");
        }
        catch (IOException ex) when (ex.Message == "Synthetic SQL backup failure") { }
        Check(File.Exists(previous), "A failed new SQL backup must preserve the previous local backup.");
        FixtureRuntime.FailBackup = false;
        outcome = await CloudBackupCoordinator.CreateAndUploadAsync();
        Check(outcome.LocalSucceeded && !outcome.DropboxSucceeded && outcome.UploadsPending,
            "A full encrypted queue must report local success and incomplete cloud delivery.");
        Check(!File.Exists(previous) && Directory.GetFiles(FixtureRuntime.LocalFolder, "FMSC_Managed_*.bak").Length == 1,
            "A verified local replacement must enforce retention even when cloud staging is blocked.");
        using (var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "schedule-state.json"))))
            Check(state.RootElement.GetProperty("LastSuccessfulLocalBackup").GetDateTimeOffset() >=
                new DateTimeOffset(File.GetLastWriteTimeUtc(Directory.GetFiles(FixtureRuntime.LocalFolder, "FMSC_Managed_*.bak").Single())),
                "A verified local backup must advance the schedule even when the cloud queue is full.");
        settings.DropboxEnabled = false;
        string combined = Directory.GetFiles(FixtureRuntime.LocalFolder, "FMSC_Managed_*.bak").Single();
        outcome = await CloudBackupCoordinator.CreateAndUploadAsync();
        Check(outcome.LocalSucceeded && !File.Exists(combined) &&
            Directory.GetFiles(FixtureRuntime.LocalFolder, "FMSC_Managed_*.bak").Length == 1,
            "Local-only backups must enforce the same retention after verifying a replacement.");
        Check(File.Exists(separate) && Directory.GetFiles(queue, "*.fmscbackup").Single() == retainedPayload,
            "Local retention must preserve separately named backups and previously queued ciphertext.");

        settings.MaximumPendingQueueMegabytes = 8192;
        settings.MaximumDropboxBackups = 1;
        settings.DropboxEnabled = true;
        FixtureRuntime.Connected = false;
        outcome = await CloudBackupCoordinator.CreateAndUploadAsync();
        string firstOffline = Directory.GetFiles(queue, "*.fmscbackup").Single();
        Check(outcome.LocalSucceeded && outcome.UploadsPending && firstOffline != retainedPayload,
            "The newest Dropbox pending package must replace an older one at its configured limit.");
        outcome = await CloudBackupCoordinator.CreateAndUploadAsync();
        string newestOffline = Directory.GetFiles(queue, "*.fmscbackup").Single();
        Check(outcome.LocalSucceeded && outcome.UploadsPending && newestOffline != firstOffline,
            "Offline Dropbox queue must safely supersede older packages without unbounded growth.");
        Check(!File.Exists(firstOffline) && File.Exists(newestOffline),
            "Supersession must retain the newest encrypted backup package.");
        using (var entry = JsonDocument.Parse(File.ReadAllText(Path.Combine(journal,
            Path.GetFileNameWithoutExtension(firstOffline) + ".json"))))
            Check(entry.RootElement.GetProperty("DropboxSuperseded").GetBoolean(),
                "Dropbox pending rotation must be recorded in its immutable journal.");
    }
    else Console.WriteLine("SKIP: coordinator ACL, job lease and persisted supersession require Windows; exercised by desktop CI.");
    Console.WriteLine($"PASS: {checks} actual coordinator journal, retry and delivery-health checks.");
}
finally
{
    Directory.Delete(root, recursive: true);
}
