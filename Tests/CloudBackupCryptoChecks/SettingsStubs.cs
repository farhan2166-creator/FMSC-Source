namespace FMSC_SMS.Core;

// Settings parsing checks never enroll credentials or write real user settings.
internal static class CurrentUser
{
    internal static bool HasPermission(string permission) => false;
}

// The portable settings fixture does not include the Windows Forms logger.
internal static class AppExceptionHandler
{
    internal static void LogNonFatal(Exception exception, string context) { }
}
