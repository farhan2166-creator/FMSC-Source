namespace FMSC_SMS.Core;

/// <summary>Recoverable message/recipient guidance, safe to show to the operator.</summary>
internal sealed class CommunicationValidationException(string message, bool isTemporary = false) : InvalidOperationException(message)
{
    internal bool IsTemporary { get; } = isTemporary;
}
