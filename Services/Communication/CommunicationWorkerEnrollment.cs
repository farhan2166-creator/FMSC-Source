using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FMSC_SMS.Core;
using FMSC_SMS.Core.Updates;
using FMSC_SMS.Pages.Security;

namespace FMSC_SMS.Services.Communication;

/// <summary>
/// Explicit administrator enrollment under an authorized Windows identity.
/// The 256-bit bearer is sealed with DPAPI CurrentUser; SQL stores its hash
/// and associates it with the Windows SID, enrolled device and current FMSC
/// credential generation. No passwords or tokens are passed on command lines.
/// </summary>
internal static class CommunicationWorkerEnrollment
{
    private static readonly byte[] Entropy = SHA256.HashData(
        Encoding.UTF8.GetBytes("FMSC/communication/background-worker/v1"));
    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FMSC", "Communication");
    private static string CredentialPath => Path.Combine(Root, "worker.v1.dpapi");
    internal static bool HasLocalCredential => File.Exists(CredentialPath);

    internal sealed record Credential(Guid WorkerId, byte[] Token, byte[] WindowsProof, byte[] DeviceProof);

    private static byte[] CurrentWindowsProof()
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new UnauthorizedAccessException("The current Windows account could not be identified.");
        return SHA256.HashData(Encoding.UTF8.GetBytes(sid));
    }

    private static byte[] CurrentDeviceProof()
    {
        if (!UpdateDeviceKey.IsEnrolled)
            throw new UnauthorizedAccessException("Authorize this computer for FMSC updates first.");
        return SHA256.HashData(Encoding.UTF8.GetBytes(
            Environment.MachineName + "|" + UpdateDeviceKey.GetPublicKey()));
    }

    internal static Credential Load()
    {
        byte[] encrypted = File.ReadAllBytes(CredentialPath);
        if (encrypted.Length is < 60 or > 16384)
            throw new InvalidDataException("The communication worker credential is invalid.");
        byte[] plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            using var json = JsonDocument.Parse(plain);
            var root = json.RootElement;
            if (root.GetProperty("Version").GetInt32() != 1)
                throw new InvalidDataException("Unsupported worker credential version.");
            Guid id = root.GetProperty("WorkerId").GetGuid();
            byte[] token = Convert.FromBase64String(root.GetProperty("Token").GetString() ?? "");
            byte[] windows = Convert.FromBase64String(root.GetProperty("WindowsProof").GetString() ?? "");
            byte[] device = Convert.FromBase64String(root.GetProperty("DeviceProof").GetString() ?? "");
            if (id == Guid.Empty || token.Length != 32 || windows.Length != 32 || device.Length != 32
                || !windows.SequenceEqual(CurrentWindowsProof())
                || !device.SequenceEqual(CurrentDeviceProof()))
            {
                CryptographicOperations.ZeroMemory(token);
                throw new UnauthorizedAccessException("The worker belongs to another Windows user or computer.");
            }
            return new(id, token, windows, device);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static Credential Create()
    {
        Directory.CreateDirectory(Root);
        UpdateDeviceKey.RestrictDirectory(Root);
        if (File.Exists(CredentialPath))
            return Load();
        var credential = new Credential(Guid.NewGuid(), RandomNumberGenerator.GetBytes(32),
            CurrentWindowsProof(), CurrentDeviceProof());
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(new {
            Version = 1, credential.WorkerId, Token = Convert.ToBase64String(credential.Token),
            WindowsProof = Convert.ToBase64String(credential.WindowsProof),
            DeviceProof = Convert.ToBase64String(credential.DeviceProof)
        });
        byte[] encrypted = Array.Empty<byte>();
        string temporary = CredentialPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(encrypted);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, CredentialPath, overwrite: false);
            return credential;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            CryptographicOperations.ZeroMemory(encrypted);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static async Task EnableAsync(CancellationToken token = default)
    {
        if (!CurrentUser.IsAdministrator || !CurrentUser.HasPermission("Communication.Configure")
            || !CurrentUser.HasPermission("Communication.Send"))
            throw new UnauthorizedAccessException("Only an FMSC administrator may enroll background sending.");
        var authorization = await UpdateGitReader.CheckAsync(token);
        if (!authorization.Authorized)
            throw new UnauthorizedAccessException(
                "Authorize this computer for FMSC updates before enabling background sending.");
        int actor = CurrentUser.UserId;
        Credential credential = Create();
        bool taskInstalled = false;
        try
        {
            await DatabaseHelper.ExecuteUserAsync("msg.RegisterWorker",
                DatabaseHelper.P("@WorkerId", credential.WorkerId),
                DatabaseHelper.P("@Token", credential.Token),
                DatabaseHelper.P("@WindowsProof", credential.WindowsProof),
                DatabaseHelper.P("@DeviceProof", credential.DeviceProof));
            if (actor != CurrentUser.UserId)
                throw new UnauthorizedAccessException("The FMSC administrator changed during enrollment.");
            await CommunicationWorkerTask.ConfigureAsync(true, token);
            taskInstalled = true;
            await DatabaseHelper.ExecuteUserAsync("msg.ActivateWorker",
                DatabaseHelper.P("@WorkerId", credential.WorkerId));
        }
        catch
        {
            try
            {
                await DatabaseHelper.ExecuteUserAsync("msg.RevokeWorker",
                    DatabaseHelper.P("@WorkerId", credential.WorkerId));
            }
            catch (Exception error)
            {
                AppExceptionHandler.LogNonFatal(error, "Worker enrollment rollback requires attention");
            }
            if (taskInstalled)
                try { await CommunicationWorkerTask.ConfigureAsync(false); }
                catch (Exception error)
                {
                    AppExceptionHandler.LogNonFatal(error, "Communication worker task cleanup failed");
                }
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(credential.Token); }
    }

    internal static async Task DisableAsync()
    {
        if (!CurrentUser.IsAdministrator || !CurrentUser.HasPermission("Communication.Configure"))
            throw new UnauthorizedAccessException("An FMSC administrator is required.");
        // Revocation comes first; a scheduled process still running must fail
        // proof validation before any subsequent provider submission.
        await DatabaseHelper.ExecuteUserAsync("msg.DisableBackgroundSending");
        await CommunicationWorkerTask.ConfigureAsync(false);
    }
}
