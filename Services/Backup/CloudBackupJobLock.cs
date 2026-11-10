using System.IO;

namespace FMSC_SMS.Services.Backup;

/// <summary>
/// An exclusive OS file handle prevents overlapping cloud jobs for the same
/// Windows profile without tying ownership to an async continuation thread.
/// The OS releases the handle if FMSC crashes or the process exits.
/// </summary>
internal static class CloudBackupJobLock
{
    internal static FileStream? TryAcquire(string path)
    {
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException ex) when ((uint)ex.HResult is 0x80070020 or 0x80070021)
        {
            // Sharing/lock violation: another process already owns the job.
            // Permission errors and disk failures must not be treated as busy.
            return null;
        }
    }
}
