namespace FMSC_SMS.Core;

internal static class CommunicationAccess
{
    internal static bool CanCompose(Func<string, bool> has) => has("Communication.Send")
        && (has("Students.View") || has("Staff.View"));
    internal static bool CanOpen(Func<string, bool> has) => CanCompose(has)
        || has("Communication.View") || has("Communication.Configure");
    internal static bool CanMessageStudent(Func<string, bool> has, bool fees = false) =>
        has("Communication.Send") && has("Students.View") && (!fees || has("Fees.View"));
}
