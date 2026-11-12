using System.Data;

namespace FMSC_SMS.Core;

/// <summary>
/// Audience selection uses school records, not a second address book.
/// A recorded Yes on Admission is the sole school-message consent authority.
/// This resolver is read-only: preview never queues or transmits messages.
/// </summary>
internal enum CommunicationAudience
{
    School, Campus, Class, OutstandingFees, Student, Family, Staff
}

internal sealed record CommunicationTarget(
    CommunicationAudience Audience, int CampusId, int ClassId,
    int StudentId, int StaffId, string Channel)
{
    internal string Purpose => Audience switch {
        CommunicationAudience.OutstandingFees => "FeeReminder",
        CommunicationAudience.Staff => "Staff",
        CommunicationAudience.Student or CommunicationAudience.Family => "Individual",
        _ => "Announcement"
    };
}

internal sealed record CommunicationPreviewEntry(
    int RecordId, string RecordName, string Kind, string? PhoneE164,
    string Reason, decimal? Outstanding,
    string? AdmissionNumber = null, DateTime? AdmissionDate = null)
{
    internal bool Included => Reason == "Included";
    internal int? FamilyId { get; init; }
    // Retain a destination only for eligible duplicate-family aggregation.
    // Declined, unrecorded, invalid and suppressed rows never expose one.
    internal string? EligiblePhone { get; init; }
    internal CommunicationPreparedMessages.Values? PreparedValues { get; init; }
}

internal sealed class CommunicationPreview
{
    internal CommunicationPreview(IReadOnlyList<CommunicationPreviewEntry> entries)
    {
        Entries = entries;
    }

    internal IReadOnlyList<CommunicationPreviewEntry> Entries { get; }
    internal int Included => Entries.Count(entry => entry.Included);
    internal int NotRecorded => Entries.Count(entry => entry.Reason == "Consent not recorded");
    internal int Declined => Entries.Count(entry => entry.Reason == "Consent declined");
    internal int InvalidNumber => Entries.Count(entry => entry.Reason == "Invalid or missing mobile");
    internal int Suppressed => Entries.Count(entry => entry.Reason == "Channel opt-out");
    internal int Duplicates => Entries.Count(entry => entry.Reason == "Duplicate destination");
    internal int Excluded => Entries.Count - Included;
}

/// <summary>Resolve fresh campus/class/fees/family records at preview time.</summary>
internal static class CommunicationRecipientResolver
{
    // The active placement is the same preference used by Outstanding Bills.
    // School and campus/class selection never require one picker action per student.
    private const string StudentCandidatesSql = """
        EXEC dbo.RequirePermission @UserId, N'Students.View';
        IF @Audience=N'OutstandingFees'
            EXEC dbo.RequirePermission @UserId, N'Fees.View';
        SELECT S.AdmissionId RecordId, S.StudentName RecordName,
               S.AdmissionNumber, S.AdmissionDate,
               S.ContactNumber, S.SchoolMessageConsent,
               CONVERT(varchar(8),'Student') Kind, B.Outstanding, FM.FamilyId
        FROM dbo.Students S
        LEFT JOIN dbo.FamilyMembers FM ON FM.AdmissionId=S.AdmissionId
        OUTER APPLY (
            SELECT TOP (1) E.ClassSectionId, E.Status
            FROM dbo.Enrollments E WHERE E.AdmissionId=S.AdmissionId
            ORDER BY CASE WHEN E.Status=N'Active' THEN 0 ELSE 1 END,
                     E.EnrollmentId DESC
        ) E
        LEFT JOIN dbo.CampusClassSections CCS ON CCS.ClassSectionId=E.ClassSectionId
        OUTER APPLY (
            SELECT SUM(dbo.GetFeeChargeBalance(BC.BillingChargeId)) Outstanding
            FROM dbo.BillingCharges BC
            WHERE @Audience=N'OutstandingFees'
              AND BC.AdmissionId=S.AdmissionId AND BC.Status<>N'Cancelled'
        ) B
        WHERE S.Status=N'Active' AND (
            @Audience=N'School'
            OR (@Audience=N'Campus' AND E.Status=N'Active' AND CCS.CampusId=@CampusId)
            OR (@Audience=N'Class' AND E.Status=N'Active'
                AND CCS.CampusId=@CampusId AND CCS.ClassId=@ClassId)
            OR (@Audience=N'OutstandingFees' AND E.Status=N'Active'
                AND (@CampusId=0 OR CCS.CampusId=@CampusId)
                AND (@ClassId=0 OR CCS.ClassId=@ClassId)
                AND (@StudentId=0 OR S.AdmissionId=@StudentId)
                AND B.Outstanding>0)
            OR (@Audience=N'Student' AND S.AdmissionId=@StudentId)
            OR (@Audience=N'Family' AND (
                S.AdmissionId=@StudentId
                OR EXISTS (
                    SELECT 1 FROM dbo.FamilyMembers FM
                    WHERE FM.AdmissionId=S.AdmissionId AND FM.FamilyId IN (
                        SELECT F2.FamilyId FROM dbo.FamilyMembers F2
                        WHERE F2.AdmissionId=@StudentId))))
        )
        ORDER BY S.StudentName, S.AdmissionId;
        """;

    // Staff records are not Admission guardians and never inherit Admission consent.
    // Staff access is independently permission-gated; no bulk staff target is exposed.
    private const string StaffCandidateSql = """
        EXEC dbo.RequirePermission @UserId, N'Staff.View';
        SELECT StaffId RecordId, StaffName RecordName, ContactNumber,
               CONVERT(nvarchar(60),NULL) AdmissionNumber,
               CONVERT(datetime2,NULL) AdmissionDate,
               CONVERT(bit,1) SchoolMessageConsent,
               CONVERT(varchar(8),'Staff') Kind, CONVERT(decimal(19,2),NULL) Outstanding
        FROM dbo.Staff WHERE StaffId=@StaffId AND Status=N'Active'
        ORDER BY StaffName, StaffId;
        """;

    private const string SuppressionSql = """
        EXEC dbo.RequirePermission @UserId, N'Students.View';
        SELECT PhoneE164 FROM msg.Suppression WHERE Channel=@Channel;
        """;

    internal static async Task<CommunicationPreview> ReadAsync(CommunicationTarget target)
    {
        Validate(target);
        int actor = CurrentUser.UserId;
        using DataTable candidates = target.Audience == CommunicationAudience.Staff
            ? await DatabaseHelper.QuerySqlAsync(StaffCandidateSql,
                DatabaseHelper.Int("@UserId", actor),
                DatabaseHelper.Int("@StaffId", target.StaffId))
            : await DatabaseHelper.QuerySqlAsync(StudentCandidatesSql,
                DatabaseHelper.Int("@UserId", actor),
                DatabaseHelper.P("@Audience", target.Audience.ToString()),
                DatabaseHelper.Int("@CampusId", target.CampusId),
                DatabaseHelper.Int("@ClassId", target.ClassId),
                DatabaseHelper.Int("@StudentId", target.StudentId));
        // Staff callers can have Staff.View without Students.View. Suppressions
        // remain mandatory for every channel; authorize by the relevant source.
        const string staffSuppressionSql = """
            EXEC dbo.RequirePermission @UserId, N'Staff.View';
            SELECT PhoneE164 FROM msg.Suppression WHERE Channel=@Channel;
            """;
        using DataTable blocked = await DatabaseHelper.QuerySqlAsync(
            target.Audience == CommunicationAudience.Staff ? staffSuppressionSql : SuppressionSql,
            DatabaseHelper.Int("@UserId", actor), DatabaseHelper.P("@Channel", target.Channel));
        if (actor != CurrentUser.UserId)
            throw new InvalidOperationException("The signed-in user changed. Review recipients again.");
        var preview = BuildPreview(candidates, blocked.AsEnumerable()
            .Select(row => Convert.ToString(row["PhoneE164"]) ?? "")
            .ToHashSet(StringComparer.Ordinal), target.Audience == CommunicationAudience.Staff);
        return target.Audience == CommunicationAudience.OutstandingFees ? PrepareFeePreview(preview) : preview;
    }

    internal static void Validate(CommunicationTarget target)
    {
        if (target.Channel is not ("Sms" or "WhatsApp"))
            throw new CommunicationValidationException("Choose SMS or WhatsApp.");
        if (!Enum.IsDefined(target.Audience))
            throw new CommunicationValidationException("Choose a supported school-message audience.");
        if (target.CampusId < 0 || target.ClassId < 0 || target.StudentId < 0 || target.StaffId < 0)
            throw new CommunicationValidationException("Choose valid school records and filters.");
        if ((target.Audience == CommunicationAudience.Campus && target.CampusId <= 0)
            || (target.Audience == CommunicationAudience.Class
                && (target.CampusId <= 0 || target.ClassId <= 0))
            || ((target.Audience is CommunicationAudience.Student or CommunicationAudience.Family)
                && target.StudentId <= 0)
            || (target.Audience == CommunicationAudience.Staff && target.StaffId <= 0))
            throw new CommunicationValidationException("Select the required campus, class or school record.");
    }

    /// <summary>
    /// Eligibility is based on live record consent, not communication preferences.
    /// Multiple students sharing a guardian number get only one send destination.
    /// </summary>
    internal static CommunicationPreview BuildPreview(
        DataTable candidates, IReadOnlySet<string> suppressions, bool staffAudience = false)
    {
        var destinations = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<CommunicationPreviewEntry>();
        foreach (DataRow row in candidates.Rows)
        {
            int id = Convert.ToInt32(row["RecordId"]);
            // Imported blank names use the same visible reference in preview
            // and live rendering; never manufacture a person's name.
            string name = DisplayName(kindName(row), id, Convert.ToString(row["RecordName"]));
            string? admissionNumber = candidates.Columns.Contains("AdmissionNumber")
                && !row.IsNull("AdmissionNumber")
                    ? Convert.ToString(row["AdmissionNumber"]) : null;
            DateTime? admissionDate = candidates.Columns.Contains("AdmissionDate")
                && !row.IsNull("AdmissionDate")
                    ? Convert.ToDateTime(row["AdmissionDate"]) : null;
            string kind = Convert.ToString(row["Kind"]) ?? "Student";
            bool? consent = row.IsNull("SchoolMessageConsent")
                ? null : Convert.ToBoolean(row["SchoolMessageConsent"]);
            decimal? balance = row.IsNull("Outstanding")
                ? null : Convert.ToDecimal(row["Outstanding"]);
            string? number = NormalizeMobile(Convert.ToString(row["ContactNumber"]));
            string reason = !staffAudience && consent is null ? "Consent not recorded"
                : !staffAudience && consent == false ? "Consent declined"
                : number is null ? "Invalid or missing mobile"
                : suppressions.Contains(number) ? "Channel opt-out"
                : !destinations.Add(number) ? "Duplicate destination"
                : "Included";
            // Never show denied-consent phone numbers in the user-facing preview.
            entries.Add(new CommunicationPreviewEntry(id, name, kind,
                reason == "Included" ? number : null, reason, balance,
                admissionNumber, admissionDate) {
                EligiblePhone = reason is "Included" or "Duplicate destination" ? number : null,
                FamilyId = candidates.Columns.Contains("FamilyId") && !row.IsNull("FamilyId")
                    ? Convert.ToInt32(row["FamilyId"]) : null
            });
        }
        return new CommunicationPreview(entries);
    }

    internal static CommunicationPreview PrepareFeePreview(CommunicationPreview preview)
    {
        // Match the live handoff's family/consent/scope aggregation before the
        // operator confirms. The TVP still contains one representative per phone.
        var entries = preview.Entries.Select(selected => {
            if (!selected.Included) return selected;
            var group = preview.Entries.Where(row => row.EligiblePhone == selected.EligiblePhone
                && row.EligiblePhone is not null).Select(row => new CommunicationPreparedMessages.FeeStudent(
                    row.RecordId, row.RecordName, row.AdmissionNumber, row.Outstanding ?? 0,
                    selected.FamilyId.HasValue && row.FamilyId == selected.FamilyId) { AdmissionDate = row.AdmissionDate }).ToArray();
            var values = CommunicationPreparedMessages.AggregateFeeReminder(
                CommunicationPreparedMessages.FromPreview(selected), group);
            return selected with { PreparedValues = values };
        }).ToArray();
        return new CommunicationPreview(entries);
    }

    internal static string DisplayName(string kind, int id, string? name) =>
        string.IsNullOrWhiteSpace(name) ? kind + " #" + id : name.Trim();

    private static string kindName(DataRow row)
        => Convert.ToString(row["Kind"]) == "Staff" ? "Staff" : "Student";

    /// <summary>Only Pakistan mobile numbers are accepted for both SMS and WhatsApp.</summary>
    internal static string? NormalizeMobile(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || !PakistanPhoneNumber.IsValidOptional(input))
            return null;
        string digits = new(input.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("0092", StringComparison.Ordinal)) digits = digits[4..];
        else if (digits.StartsWith("92", StringComparison.Ordinal)) digits = digits[2..];
        else if (digits.StartsWith('0')) digits = digits[1..];
        if (digits.Length != 10 || digits[0] != '3' || digits[1] is < '0' or > '6')
            return null;
        return "+92" + digits;
    }
}
