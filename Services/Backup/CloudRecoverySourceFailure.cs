using System.Security.Cryptography;

namespace FMSC_SMS.Services.Backup;

internal static class CloudRecoverySourceFailure
{
    // A provider timeout is recoverable before SQL activation. Operator cancellation
    // must stop immediately, even if the provider reports it as an I/O failure.
    internal static bool CanTryAnotherCopy(Exception error, CancellationToken token) =>
        !token.IsCancellationRequested && error is IOException or InvalidDataException or UnauthorizedAccessException
            or CryptographicException or HttpRequestException or TimeoutException or OperationCanceledException;
}
