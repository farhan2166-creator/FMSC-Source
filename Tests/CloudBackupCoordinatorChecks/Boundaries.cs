using Microsoft.Data.SqlClient;
namespace FMSC_SMS {
internal static class DatabaseHelper {
 internal static string DatabaseFile => FMSC_SMS.Services.Backup.FixtureRuntime.LocalFolder + "/missing-database.mdf";
 internal static bool TestConnection(out string why){why="";throw new Exception("SQL forbidden");}
 internal static SqlConnection GetMasterConnection()=>throw new Exception("SQL forbidden");
 internal static void OpenConnection(SqlConnection c)=>throw new Exception("SQL forbidden");
}}
namespace FMSC_SMS.Core {
internal static class CurrentUser {internal static bool HasPermission(string p)=>true;}
internal static class AppExceptionHandler {internal static void LogNonFatal(Exception e,string s){}}
}
namespace FMSC_SMS.Core.Updates {internal static class UpdateStartupGuard {internal static bool RequiresRecovery(byte[] b)=>throw new Exception("Update inspection forbidden");}}
namespace FMSC_SMS.Services.Backup {
internal enum CloudProvider {GoogleDrive,Dropbox}
internal sealed record CloudRemoteBackup(CloudProvider Provider,string RemoteId,string Name,string PackageSha256,DateTime SqlBackupCreatedUtc,DateTime UploadedUtc,long Size);
internal sealed class CloudBackupOptions {
 public bool LocalEnabled {get;set;}=true;public bool GoogleDriveEnabled {get;set;}=true;public bool DropboxEnabled {get;set;}
 public bool AutomaticEnabled {get;set;}=true;public int MaximumPendingQueueMegabytes=>8192;public int MaximumLocalBackups=>7;public int MaximumGoogleDriveBackups=>7;public int MaximumDropboxBackups=>7;
 internal void Validate(){} internal bool IsDue(DateTimeOffset now,DateTimeOffset? last)=>FixtureRuntime.Due;
}
internal static class CloudBackupSettings
{
    internal static CloudBackupOptions Load() => FixtureRuntime.Options;
    internal static CloudBackupOptions LoadForEditing(out string? warning) { warning = null; return Load(); }
    internal static bool HasLocalRecoveryPassphrase => true;
    internal static string OpenRecoveryPassphrase(string? queuedKeyPath = null) => "fictional-disposable-recovery-key";
    internal static void PreserveQueuedPassphrase(string path) => throw new NotSupportedException();
    internal static void SaveRecoveryPassphrase(string passphrase, bool useAdministratorPassword) => throw new NotSupportedException();
}
internal static class BackupSettings {internal static string GetBackupFolder()=>FixtureRuntime.LocalFolder;}
internal static class DatabaseBackupManager {
 internal sealed class BackupResult {public bool Success {get;init;}public string Message{get;init;}="";}
 internal sealed class BackupInfo {public bool IsForFmscDatabase=>true;public DateTime BackupStartDate=>FixtureRuntime.SqlCreated;}
 internal static string CreateUniqueBackupPath(string a,string b)=>Path.Combine(a,b);
 internal static BackupResult CreateBackup(string p){File.WriteAllBytes(p, new byte[1024]);return new(){Success=true};}
 internal static BackupInfo? GetBackupInfo(string p)=>new();
}
internal static class CloudBackupOAuth {internal static bool IsConnected(CloudProvider p)=>FixtureRuntime.Connected;}
internal static class CloudBackupTransport {
 internal static Task<IReadOnlyList<CloudRemoteBackup>> ListAsync(CloudProvider p,CancellationToken t)=>Task.FromResult<IReadOnlyList<CloudRemoteBackup>>(FixtureRuntime.Remote.Values.Select(x=>x.Remote).Where(x=>x.Provider==p).ToArray());
 internal static Task DownloadAsync(CloudProvider p,string id,string path,CancellationToken t)=>File.WriteAllBytesAsync(path,FixtureRuntime.Remote[id].Bytes,t);
 internal static Task DeleteAsync(CloudProvider p,string id,CancellationToken t)=>throw new Exception("Network forbidden");
 internal static Task<CloudRemoteBackup> UploadAsync(CloudProvider p,string path,DateTime d,CancellationToken t,string? id,Action<string> save)
 {
  byte[] bytes=File.ReadAllBytes(path);
  var remote=new CloudRemoteBackup(p,Guid.NewGuid().ToString("N"),"synthetic",Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),d,DateTime.UtcNow,bytes.Length);
  FixtureRuntime.Remote.Add(remote.RemoteId,(remote,bytes));return Task.FromResult(remote);
 }
}
internal static class CloudRestoreStaging {
 internal sealed record DownloadCandidate(Func<string,CancellationToken,Task> Download,string HistoryPath,string Label);
 internal static Task<DatabaseBackupManager.BackupResult> RestoreWithFallbackAsync(IReadOnlyList<DownloadCandidate> c,string p,CancellationToken t,IProgress<BackupProgressUpdate>? progress)=>throw new Exception("SQL forbidden");
 internal static async Task VerifyRemoteCopyAsync(Func<string,CancellationToken,Task> d,string p,DateTime when,CancellationToken t)
 {
  string package=Path.Combine(FixtureRuntime.LocalFolder,Guid.NewGuid()+".fmscbackup");
  string bak=package+".bak";
  try {await d(package,t);CloudBackupCrypto.Decrypt(package,bak,p);FixtureRuntime.Verifications++;}
  finally {File.Delete(package);File.Delete(bak);}
 }
}
internal static class BackupActivityJournal {internal static void TryRecord(string o,string d,string s,string detail=""){}}
}

namespace FMSC_SMS.Services.Backup {
// Only OS settings, SQL creation and external provider boundaries are replaced.
// Production encryption, digest validation, queue journals and orchestration run unchanged.
internal static class FixtureRuntime {
 internal static string LocalFolder="";
 internal static readonly CloudBackupOptions Options=new();
 internal static bool Due,Connected;
 internal static int Verifications;
 internal static readonly DateTime SqlCreated=DateTime.Now;
 internal static readonly Dictionary<string,(CloudRemoteBackup Remote,byte[] Bytes)> Remote=new();
}}
