using System.Data;

namespace FMSC_SMS.Core;

/// <summary>
/// The admission record is the one authority for school SMS/WhatsApp consent.
/// No communication contact directory, secondary verification or automatic send.
/// </summary>
internal static class AdmissionConsentStore
{
    internal static async Task<bool?> ReadAsync(int admissionId)
    {
        if (admissionId <= 0) throw new ArgumentOutOfRangeException(nameof(admissionId));
        DataTable rows = await DatabaseHelper.QuerySqlAsync("""
            EXEC dbo.RequirePermission @UserId, N'Students.View';
            SELECT SchoolMessageConsent FROM dbo.Students WHERE AdmissionId=@AdmissionId;
            """,
            DatabaseHelper.Int("@UserId", CurrentUser.UserId),
            DatabaseHelper.Int("@AdmissionId", admissionId));
        if (rows.Rows.Count != 1)
            throw new InvalidOperationException("Admission consent could not be loaded.");
        object value = rows.Rows[0]["SchoolMessageConsent"];
        return value == DBNull.Value ? null : Convert.ToBoolean(value);
    }

    internal static Task RecordAsync(int admissionId, bool consent)
    {
        if (admissionId <= 0) throw new ArgumentOutOfRangeException(nameof(admissionId));
        // Both permissions are checked by SQL; the caller cannot bypass either one.
        string authority = CurrentUser.HasPermission("Students.Admit")
            ? "Students.Admit" : "Students.Administer";
        return DatabaseHelper.ExecuteUserAsync("msg.RecordAdmissionConsent",
            DatabaseHelper.Int("@AdmissionId", admissionId),
            DatabaseHelper.Bit("@Consent", consent),
            DatabaseHelper.P("@Authority", authority));
    }
}
