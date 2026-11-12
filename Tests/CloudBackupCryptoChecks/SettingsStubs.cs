namespace FMSC_SMS.Core;

// Settings parsing checks never enroll credentials or write real user settings.
internal static class CurrentUser
{
    internal static bool HasPermission(string permission) => false;
}
