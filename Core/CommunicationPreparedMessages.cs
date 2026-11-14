using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;

namespace FMSC_SMS.Core;

/// <summary>
/// Reviewable message formats, not a second consent or recipient directory.
/// A formatted message is generated from authorized, current school records;
/// the batch keeps the format so a resumed handoff refreshes financial data.
/// </summary>
internal static class CommunicationPreparedMessages
{
    internal sealed record BuiltIn(string Name, string Purpose, string Body);
    internal sealed record Values(int RecordId, string RecordName,
        string? AdmissionNumber, DateTime? AdmissionDate, decimal? Outstanding);
    // Only in-scope, consented and still-active students can contribute to a
    // shared-number fee reminder. Family association is verified before
    // joining identities into one guardian-facing message.
    internal sealed record FeeStudent(
        int RecordId, string RecordName, string? AdmissionNumber,
        decimal Outstanding, bool SameFamily)
    {
        internal DateTime? AdmissionDate { get; init; }
    }

    internal static IReadOnlyList<BuiltIn> BuiltIns { get; } = new[]
    {
        new BuiltIn("Outstanding fee reminder", "FeeReminder",
            "Dear parent/guardian, the outstanding fee balance for [StudentName] (admission [AdmissionNumber]) is PKR [OutstandingAmount]. Please contact the school office if you have already paid."),
        new BuiltIn("New admission welcome", "Individual",
            "Dear parent/guardian, [StudentName] (admission [AdmissionNumber]) joined the school on [AdmissionDate]. Welcome to the school! Please contact the office if you need assistance."),
        new BuiltIn("Individual student notice", "Individual",
            "Dear parent/guardian of [StudentName], please contact the school office regarding an important notice."),
        new BuiltIn("School announcement", "Announcement",
            "Dear parent/guardian, this is an announcement from the school. Please contact the school office for more information.")
    };

    // These are deliberately limited, readable placeholders. No SQL, scripting,
    // formula expressions, or arbitrary data paths can be supplied by a template.
    private static readonly Regex Field = new(
        @"\[(?<key>[A-Za-z][A-Za-z0-9]*)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    internal static bool HasFields(string body) => Field.IsMatch(body);

    internal static Values FromPreview(CommunicationPreviewEntry entry)
        => entry.PreparedValues ?? new(entry.RecordId, entry.RecordName, entry.AdmissionNumber,
            entry.AdmissionDate, entry.Outstanding);

    internal static void ValidateFormat(string body, string? purpose = null)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > 1600)
            throw new CommunicationValidationException("Write a message of up to 1600 characters.");
        string remaining = Field.Replace(body, match => match.Groups["key"].Value switch {
            "StudentName" or "AdmissionNumber" or "AdmissionDate" => "",
            "OutstandingAmount" when purpose is null or "FeeReminder" => "",
            "OutstandingAmount" => throw new CommunicationValidationException("Use [OutstandingAmount] with an outstanding-fee reminder."),
            _ => throw new CommunicationValidationException("Unsupported prepared field " + match.Value + ". Use [StudentName], [AdmissionNumber], [AdmissionDate] or [OutstandingAmount].")
        });
        if (remaining.Contains('[') || remaining.Contains(']') || remaining.Contains("{{", StringComparison.Ordinal)
            || remaining.Contains("}}", StringComparison.Ordinal))
            throw new CommunicationValidationException("Complete each prepared field using [FieldName]. Remove unmatched brackets and {{...}} placeholders.");
    }

    internal static string Render(string body, Values values)
    {
        ValidateFormat(body);
        string rendered = Field.Replace(body, m => m.Groups["key"].Value switch
        {
            "StudentName" => !string.IsNullOrWhiteSpace(values.RecordName)
                ? values.RecordName.Trim() : "school record #" + values.RecordId.ToString(CultureInfo.InvariantCulture),
            "AdmissionNumber" => !string.IsNullOrWhiteSpace(values.AdmissionNumber)
                ? values.AdmissionNumber.Trim() : values.RecordId.ToString(CultureInfo.InvariantCulture),
            "AdmissionDate" => values.AdmissionDate.HasValue
                ? values.AdmissionDate.Value.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)
                : throw new CommunicationValidationException("This message has no single recorded admission date. Edit the format or choose one student."),
            "OutstandingAmount" => values.Outstanding is > 0
                ? values.Outstanding.Value.ToString("N2", CultureInfo.InvariantCulture)
                : throw new CommunicationValidationException("An outstanding balance is no longer available. Review the fee reminder again."),
            _ => throw new CommunicationValidationException("This message contains an unsupported prepared field: " + m.Value)
        });
        if (rendered.Contains("{{", StringComparison.Ordinal)
            || rendered.Contains("}}", StringComparison.Ordinal)
            || rendered.Length > 1600)
            throw new CommunicationValidationException("Prepared message contains an unresolved field or exceeds 1600 characters.");
        return rendered;
    }


    /// <summary>
    /// Never silently use only the first child's balance when several
    /// consented students share a WhatsApp destination. Refuse ambiguous
    /// contact numbers unless *every* in-scope row is linked by FamilyMembers
    /// to the originally reviewed student. No automatic sending is involved.
    /// </summary>
    internal static Values AggregateFeeReminder(Values selected, IReadOnlyList<FeeStudent> students)
    {
        if (students.Count == 0 ||
            students.Any(s => s.RecordId <= 0 || s.Outstanding <= 0) ||
            students.Select(s => s.RecordId).Distinct().Count() != students.Count ||
            students.All(s => s.RecordId != selected.RecordId))
            throw new CommunicationValidationException(
                "The shared-number outstanding-fee records changed. Review recipients again.");
        if (students.Count == 1)
            return selected with { Outstanding = students[0].Outstanding };
        if (students.Any(s => !s.SameFamily))
            throw new CommunicationValidationException(
                "This phone number belongs to multiple outstanding students without a verified shared family. " +
                "Review the family links and contact number before opening a fee reminder; no chat was launched.");

        string names = string.Join(", ", students.Select(s =>
            string.IsNullOrWhiteSpace(s.RecordName)
                ? "school record #" + s.RecordId.ToString(CultureInfo.InvariantCulture)
                : s.RecordName.Trim()));
        string admissions = string.Join(", ", students.Select(s =>
            string.IsNullOrWhiteSpace(s.AdmissionNumber)
                ? "record #" + s.RecordId.ToString(CultureInfo.InvariantCulture)
                : s.AdmissionNumber.Trim()));
        decimal total = students.Sum(s => s.Outstanding);
        DateTime? date = students[0].AdmissionDate;
        if (students.Any(student => student.AdmissionDate != date)) date = null;
        return selected with { RecordName = names, AdmissionNumber = admissions, Outstanding = total, AdmissionDate = date };
    }

    // The identical classroom/campus/active-enrollment rules used by both
    // preview and msg.OpenManualHandoff are re-applied immediately before
    // preparing the human-controlled WhatsApp URL. No unrelated family or
    // nonconsenting student is disclosed, and no other campus is added.
    private const string SharedFeeRecordsSql = """
        EXEC dbo.RequirePermission @UserId,N'Communication.Send';
        EXEC dbo.RequirePermission @UserId,N'Students.View';
        EXEC dbo.RequirePermission @UserId,N'Fees.View';
        SELECT S.AdmissionId RecordId,S.StudentName RecordName,
               S.AdmissionNumber,S.AdmissionDate,F.Outstanding,
               CONVERT(bit,CASE WHEN EXISTS (SELECT 1 FROM msg.ReviewedFeeStudent Reviewed
                   WHERE Reviewed.RecipientId=R.RecipientId AND Reviewed.AdmissionId=S.AdmissionId)
                   THEN 1 ELSE 0 END) Reviewed,
               (SELECT COUNT(*) FROM msg.ReviewedFeeStudent Reviewed WHERE Reviewed.RecipientId=R.RecipientId) ReviewedCount,
               CONVERT(bit,CASE WHEN EXISTS (
                   SELECT 1 FROM dbo.FamilyMembers Selected
                   JOIN dbo.FamilyMembers Related ON Related.FamilyId=Selected.FamilyId
                   WHERE Selected.AdmissionId=R.AdmissionId
                     AND Related.AdmissionId=S.AdmissionId
               ) THEN 1 ELSE 0 END) SameFamily
        FROM msg.ManualHandoff H
        JOIN msg.Recipient R ON R.RecipientId=H.RecipientId
        JOIN msg.Batch B ON B.BatchId=R.BatchId
        JOIN dbo.Students S ON msg.NormalizeMobile(S.ContactNumber)=R.PhoneE164
        OUTER APPLY (
            SELECT TOP (1) E.ClassSectionId,E.Status
            FROM dbo.Enrollments E WHERE E.AdmissionId=S.AdmissionId
            ORDER BY CASE WHEN E.Status=N'Active' THEN 0 ELSE 1 END,E.EnrollmentId DESC
        ) E
        LEFT JOIN dbo.CampusClassSections CCS ON CCS.ClassSectionId=E.ClassSectionId
        OUTER APPLY (
            SELECT SUM(dbo.GetFeeChargeBalance(C.BillingChargeId)) Outstanding
            FROM dbo.BillingCharges C
            WHERE C.AdmissionId=S.AdmissionId AND C.Status<>N'Cancelled'
        ) F
        WHERE R.RecipientId=@RecipientId
          AND B.Audience='OutstandingFees'
          AND H.State='Opened' AND R.State='Pending'
          AND S.Status=N'Active' AND S.SchoolMessageConsent=1
          AND E.Status=N'Active'
          AND (@CampusFilter=0 OR CCS.CampusId=@CampusFilter)
          AND (@ClassFilter=0 OR CCS.ClassId=@ClassFilter)
          AND (@StudentFilter=0 OR S.AdmissionId=@StudentFilter)
          AND F.Outstanding>0
          AND NOT EXISTS (SELECT 1 FROM msg.Suppression O
                          WHERE O.Channel='WhatsApp' AND O.PhoneE164=R.PhoneE164)
        ORDER BY S.StudentName,S.AdmissionId;
        """;

    private static async Task<Values> ReadSharedFeeValuesAsync(long recipientId, int actor, Values selected)
    {
        // Campus/class filters are read from the confirmed batch by SQL, not
        // supplied or overridden by a client-side picker after confirmation.
        const string scoped = """
            EXEC dbo.RequirePermission @UserId,N'Communication.Send';
            EXEC dbo.RequirePermission @UserId,N'Students.View';
            EXEC dbo.RequirePermission @UserId,N'Fees.View';
            SELECT B.CampusId,B.ClassId,CASE WHEN B.SingleStudentScope=1 THEN B.StudentId ELSE 0 END StudentId FROM msg.ManualHandoff H
            JOIN msg.Recipient R ON R.RecipientId=H.RecipientId
            JOIN msg.Batch B ON B.BatchId=R.BatchId
            WHERE R.RecipientId=@RecipientId AND H.State='Opened'
              AND R.State='Pending' AND B.Audience='OutstandingFees';
            """;
        using DataTable scope = await DatabaseHelper.QuerySqlAsync(scoped,
            DatabaseHelper.Int("@UserId",actor),DatabaseHelper.P("@RecipientId",recipientId));
        if (actor != CurrentUser.UserId || scope.Rows.Count != 1)
            throw new CommunicationValidationException("Fee reminder scope changed. Review the recipients again.");
        using DataTable rows = await DatabaseHelper.QuerySqlAsync(SharedFeeRecordsSql,
            DatabaseHelper.Int("@UserId",actor),
            DatabaseHelper.P("@RecipientId",recipientId),
            DatabaseHelper.Int("@CampusFilter",Convert.ToInt32(scope.Rows[0]["CampusId"])),
            DatabaseHelper.Int("@ClassFilter",Convert.ToInt32(scope.Rows[0]["ClassId"])),
            DatabaseHelper.Int("@StudentFilter",Convert.ToInt32(scope.Rows[0]["StudentId"])));
        if (actor != CurrentUser.UserId)
            throw new CommunicationValidationException("Signed-in account changed. Review the recipient again.");
        if (rows.Rows.Count == 0 || Convert.ToInt32(rows.Rows[0]["ReviewedCount"]) != rows.Rows.Count
            || rows.AsEnumerable().Any(row => !Convert.ToBoolean(row["Reviewed"])))
            throw new CommunicationValidationException(
                "The students in this fee reminder differ from the saved review. Skip this chat and preview a new reminder.");
        return AggregateFeeReminder(selected, rows.AsEnumerable().Select(row =>
            new FeeStudent(Convert.ToInt32(row["RecordId"]),
                CommunicationRecipientResolver.DisplayName("Student", Convert.ToInt32(row["RecordId"]), Convert.ToString(row["RecordName"])),
                row.IsNull("AdmissionNumber") ? null : Convert.ToString(row["AdmissionNumber"]),
                Convert.ToDecimal(row["Outstanding"]),
                Convert.ToBoolean(row["SameFamily"])) { AdmissionDate = row.IsNull("AdmissionDate") ? null : Convert.ToDateTime(row["AdmissionDate"]) }).ToArray());
    }

    /// <summary>
    /// Called only AFTER msg.OpenManualHandoff has revalidated current consent,
    /// recipient scope, opt-out, destination and sender authorization. Query
    /// admission/fee facts fresh before opening a browser chat, never on send.
    /// </summary>
    internal static async Task<Values> ReadLiveAsync(long recipientId, bool needsFees)
    {
        const string sql = """
            EXEC dbo.RequirePermission @UserId,N'Communication.Send';
            DECLARE @Audience varchar(20), @CampusId int, @ClassId int, @StudentId int, @StaffId int;
            SELECT @Audience=B.Audience,@CampusId=B.CampusId,@ClassId=B.ClassId,@StudentId=CASE WHEN B.Audience='OutstandingFees' AND B.SingleStudentScope=0 THEN 0 ELSE B.StudentId END,@StaffId=B.StaffId
            FROM msg.ManualHandoff H
            JOIN msg.Recipient R ON R.RecipientId=H.RecipientId
            JOIN msg.Batch B ON B.BatchId=R.BatchId
            WHERE H.RecipientId=@RecipientId AND H.State='Opened'
              AND R.State='Pending';
            IF @Audience IS NULL
                THROW 51120,'Manual recipient is no longer available.',1;
            IF @Audience='Staff' EXEC dbo.RequirePermission @UserId,N'Staff.View';
            ELSE EXEC dbo.RequirePermission @UserId,N'Students.View';
            IF @NeedFees=1
            BEGIN
                IF @Audience<>'OutstandingFees'
                    THROW 51120,'Fee amounts require the outstanding-fee audience.',1;
                EXEC dbo.RequirePermission @UserId,N'Fees.View';
            END;
            SELECT COALESCE(R.AdmissionId,R.StaffId) RecordId,
                COALESCE(NULLIF(LTRIM(RTRIM(S.StudentName)),N''),
                         NULLIF(LTRIM(RTRIM(T.StaffName)),N''),N'') RecordName,
                S.AdmissionNumber,S.AdmissionDate,
                F.Outstanding,@Audience AS Audience
            FROM msg.ManualHandoff H
            JOIN msg.Recipient R ON R.RecipientId=H.RecipientId
            LEFT JOIN dbo.Students S ON S.AdmissionId=R.AdmissionId
            LEFT JOIN dbo.Staff T ON T.StaffId=R.StaffId
            OUTER APPLY (SELECT TOP(1) E.ClassSectionId,E.Status FROM dbo.Enrollments E
                WHERE E.AdmissionId=S.AdmissionId
                ORDER BY CASE WHEN E.Status=N'Active' THEN 0 ELSE 1 END,E.EnrollmentId DESC) E
            LEFT JOIN dbo.CampusClassSections CCS ON CCS.ClassSectionId=E.ClassSectionId
            OUTER APPLY (
                SELECT SUM(dbo.GetFeeChargeBalance(C.BillingChargeId)) Outstanding
                FROM dbo.BillingCharges C
                WHERE (@NeedFees=1 OR @Audience='OutstandingFees') AND C.AdmissionId=S.AdmissionId
                  AND C.Status<>N'Cancelled'
            ) F
            WHERE H.RecipientId=@RecipientId AND H.State='Opened'
              AND R.State='Pending'
              AND NOT EXISTS (
                  SELECT 1 FROM msg.Suppression O
                  WHERE O.Channel='WhatsApp' AND O.PhoneE164=R.PhoneE164)
              AND ((S.AdmissionId IS NOT NULL AND S.Status=N'Active'
                    AND S.SchoolMessageConsent=1
                    AND msg.NormalizeMobile(S.ContactNumber)=R.PhoneE164
                    AND (@Audience='School'
                      OR (@Audience='Campus' AND E.Status=N'Active' AND CCS.CampusId=@CampusId)
                      OR (@Audience='Class' AND E.Status=N'Active' AND CCS.CampusId=@CampusId AND CCS.ClassId=@ClassId)
                      OR (@Audience='OutstandingFees' AND E.Status=N'Active'
                          AND (@CampusId=0 OR CCS.CampusId=@CampusId) AND (@ClassId=0 OR CCS.ClassId=@ClassId)
                          AND (@StudentId=0 OR S.AdmissionId=@StudentId) AND F.Outstanding>0)
                      OR (@Audience='Student' AND S.AdmissionId=@StudentId)
                      OR (@Audience='Family' AND (S.AdmissionId=@StudentId OR EXISTS(
                          SELECT 1 FROM dbo.FamilyMembers M JOIN dbo.FamilyMembers X ON X.FamilyId=M.FamilyId
                          WHERE M.AdmissionId=S.AdmissionId AND X.AdmissionId=@StudentId)))))
                OR (@Audience='Staff' AND T.StaffId=@StaffId AND T.Status=N'Active'
                    AND msg.NormalizeMobile(T.ContactNumber)=R.PhoneE164));
            """;
        int actor = CurrentUser.UserId;
        using DataTable table = await DatabaseHelper.QuerySqlAsync(sql,
            DatabaseHelper.Int("@UserId", actor),
            DatabaseHelper.P("@RecipientId", recipientId),
            DatabaseHelper.P("@NeedFees", needsFees));
        if (actor != CurrentUser.UserId || table.Rows.Count != 1)
            throw new CommunicationValidationException("School records or signed-in account changed. Review the WhatsApp recipient again.");
        DataRow row = table.Rows[0];
        Values selected = new(Convert.ToInt32(row["RecordId"]),
            CommunicationRecipientResolver.DisplayName(Convert.ToString(row["Audience"]) == "Staff" ? "Staff" : "Student",
                Convert.ToInt32(row["RecordId"]), Convert.ToString(row["RecordName"])),
            row.IsNull("AdmissionNumber") ? null : Convert.ToString(row["AdmissionNumber"]),
            row.IsNull("AdmissionDate") ? null : Convert.ToDateTime(row["AdmissionDate"]),
            row.IsNull("Outstanding") ? null : Convert.ToDecimal(row["Outstanding"]));
        return string.Equals(Convert.ToString(row["Audience"]), "OutstandingFees", StringComparison.Ordinal)
            ? await ReadSharedFeeValuesAsync(recipientId, actor, selected)
            : selected;
    }
}
