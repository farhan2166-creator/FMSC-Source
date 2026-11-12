namespace FMSC_SMS.Core;

/// <summary>Recoverable message/recipient guidance, safe to show to the operator.</summary>
internal sealed class CommunicationValidationException(string message) : InvalidOperationException(message);
