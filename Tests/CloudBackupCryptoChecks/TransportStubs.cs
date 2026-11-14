// No live OAuth or provider account is used by the transport regression fixture.
namespace FMSC_SMS.Services.Backup;
internal enum CloudProvider { Dropbox }
internal static class CloudBackupOAuth
{
    internal static Task<string> GetAccessTokenAsync(CloudProvider provider, CancellationToken token)
        => throw new InvalidOperationException("A test attempted live OAuth.");
}
