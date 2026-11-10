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

    internal static IReadOnlyList<BuiltIn> BuiltIns { get; } = new[]
    {
        new BuiltIn("Outstanding fee reminder", "FeeReminder",
            "Dear parent/guardian of [StudentName], our records show an outstanding fee balance of PKR [OutstandingAmount] for admission [AdmissionNumber]. Please contact the school office if you have already paid."),
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
        => new(entry.RecordId, entry.RecordName, entry.AdmissionNumber,
            entry.AdmissionDate, entry.Outstanding);

    internal static string Render(string body, Values values)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > 1600)
            throw new ArgumentException("Write a message of up to 1600 characters.");
        string rendered = Field.Replace(body, m => m.Groups["key"].Value switch
        {
            "StudentName" => !string.IsNullOrWhiteSpace(values.RecordName)
                ? values.RecordName.Trim() : "school record #" + values.RecordId.ToString(CultureInfo.InvariantCulture),
            "AdmissionNumber" => !string.IsNullOrWhiteSpace(values.AdmissionNumber)
                ? values.AdmissionNumber.Trim() : values.RecordId.ToString(CultureInfo.InvariantCulture),
            "AdmissionDate" => values.AdmissionDate.HasValue
                ? values.AdmissionDate.Value.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)
                : throw new InvalidOperationException("This record has no admission date. Edit the message before continuing."),
            "OutstandingAmount" => values.Outstanding is > 0
                ? values.Outstanding.Value.ToString("N2", CultureInfo.InvariantCulture)
                : throw new InvalidOperationException("An outstanding balance is no longer available. Review the fee reminder again."),
            _ => throw new ArgumentException("This message contains an unsupported prepared field: " + m.Value)
        });
        if (rendered.Contains("{{", StringComparison.Ordinal)
            || rendered.Contains("}}", StringComparison.Ordinal)
            || rendered.Length > 1600)
            throw new InvalidOperationException("Prepared message contains an unresolved field or exceeds 1600 characters.");
        return rendered;
    }

    /// <summary>
    /// Called only AFTER msg.OpenManualHandoff has revalidated current consent,
    /// recipient scope, opt-out, destination and sender authorization. Query
    /// admission/fee facts fresh before opening a browser chat, never on send.
    /// </summary>

    // A bulk fee batch deduplicates WhatsApp destinations, NOT debts.
    // Rebuild all currently eligible students for this batch scope and phone.
    private const string SharedOutstandingSql = """
        EXEC dbo.RequirePermission @UserId,N'Communication.Send';
        EXEC dbo.RequirePermission @UserId,N'Students.View';
        EXEC dbo.RequirePermission @UserId,N'Fees.View';
        SELECT S2.AdmissionId RecordId, S2.StudentName RecordName,
               S2.AdmissionNumber, F.Outstanding
        FROM msg.ManualHandoff H
        JOIN msg.Recipient R ON R.RecipientId=H.RecipientId
        JOIN msg.Batch B ON B.BatchId=R.BatchId
        JOIN dbo.Students S ON S.AdmissionId=R.AdmissionId
        JOIN dbo.Students S2 ON S2.Status=N'Active' AND S2.SchoolMessageConsent=1
            AND msg.NormalizeMobile(S2.ContactNumber)=R.PhoneE164
        OUTER APPLY (
            SELECT TOP (1) E.ClassSectionId,E.Status
            FROM dbo.Enrollments E WHERE E.AdmissionId=S2.AdmissionId
            ORDER BY CASE WHEN E.Status=N'Active' THEN 0 ELSE 1 END,E.EnrollmentId DESC
        ) E2
        LEFT JOIN dbo.CampusClassSections CCS ON CCS.ClassSectionId=E2.ClassSectionId
        OUTER APPLY (
            SELECT SUM(dbo.GetFeeChargeBalance(C.BillingChargeId)) Outstanding
            FROM dbo.BillingCharges C
            WHERE C.AdmissionId=S2.AdmissionId AND C.Status<>N'Cancelled'
        ) F
        WHERE H.RecipientId=@RecipientId AND H.State='Opened'
          AND R.State='Pending' AND B.Audience='OutstandingFees'
          AND S.Status=N'Active' AND S.SchoolMessageConsent=1
          AND msg.NormalizeMobile(S.ContactNumber)=R.PhoneE164
          AND NOT EXISTS (
              SELECT 1 FROM msg.Suppression O
              WHERE O.Channel='WhatsApp' AND O.PhoneE164=R.PhoneE164)
          AND E2.Status=N'Active'
          AND (B.CampusId=0 OR CCS.CampusId=B.CampusId)
          AND (B.ClassId=0 OR CCS.ClassId=B.ClassId)
          AND F.Outstanding>0
        ORDER BY S2.AdmissionId;
        """;

    // A pure aggregation step is separately exercised with synthetic records.
    internal static Values AggregateOutstanding(Values selected, DataTable eligible)
    {
        if (eligible.Rows.Count == 0 ||
            !eligible.AsEnumerable().Any(row => Convert.ToInt32(row["RecordId"]) == selected.RecordId))
            throw new InvalidOperationException(
                "The recipient's current outstanding amount or consent changed. Review the fee audience again.");
        var students = eligible.AsEnumerable()
            .GroupBy(row => Convert.ToInt32(row["RecordId"]))
            .Select(group => group.Single())
            .ToArray();
        decimal total = 0;
        var names = new List<string>();
        var admissions = new List<string>();
        foreach (DataRow row in students)
        {
            decimal amount = row.IsNull("Outstanding") ? 0 : Convert.ToDecimal(row["Outstanding"]);
            if (amount <= 0)
                throw new InvalidOperationException("An outstanding balance changed. Review this recipient again.");
            total = checked(total + amount);
            string name = Convert.ToString(row["RecordName"])?.Trim() ?? "";
            names.Add(name.Length > 0 ? name : "school record #" + row["RecordId"]);
            string number = Convert.ToString(row["AdmissionNumber"])?.Trim() ?? "";
            admissions.Add(number.Length > 0 ? number : Convert.ToString(row["RecordId"])!);
        }
        return selected with {
            RecordName = string.Join(", ", names),
            AdmissionNumber = string.Join(", ", admissions),
            // A single admission date must not be claimed for multiple children.
            AdmissionDate = students.Length == 1 ? selected.AdmissionDate : null,
            Outstanding = total
        };
    }

    internal static async Task<Values> ReadLiveAsync(long recipientId, bool needsFees)
    {
        const string sql = """
            EXEC dbo.RequirePermission @UserId,N'Communication.Send';
            DECLARE @Audience varchar(20);
            SELECT @Audience=B.Audience
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
                F.Outstanding
            FROM msg.ManualHandoff H
            JOIN msg.Recipient R ON R.RecipientId=H.RecipientId
            LEFT JOIN dbo.Students S ON S.AdmissionId=R.AdmissionId
            LEFT JOIN dbo.Staff T ON T.StaffId=R.StaffId
            OUTER APPLY (
                SELECT SUM(dbo.GetFeeChargeBalance(C.BillingChargeId)) Outstanding
                FROM dbo.BillingCharges C
                WHERE @NeedFees=1 AND C.AdmissionId=S.AdmissionId
                  AND C.Status<>N'Cancelled'
            ) F
            WHERE H.RecipientId=@RecipientId AND H.State='Opened'
              AND R.State='Pending'
              AND NOT EXISTS (
                  SELECT 1 FROM msg.Suppression O
                  WHERE O.Channel='WhatsApp' AND O.PhoneE164=R.PhoneE164)
              AND ((S.AdmissionId IS NOT NULL AND S.Status=N'Active'
                    AND S.SchoolMessageConsent=1
                    AND msg.NormalizeMobile(S.ContactNumber)=R.PhoneE164)
                OR (T.StaffId IS NOT NULL AND T.Status=N'Active'
                    AND msg.NormalizeMobile(T.ContactNumber)=R.PhoneE164));
            """;
        int actor = CurrentUser.UserId;
        using DataTable table = await DatabaseHelper.QuerySqlAsync(sql,
            DatabaseHelper.Int("@UserId", actor),
            DatabaseHelper.P("@RecipientId", recipientId),
            DatabaseHelper.P("@NeedFees", needsFees));
        if (actor != CurrentUser.UserId || table.Rows.Count != 1)
            throw new InvalidOperationException("School records or signed-in account changed. Review the WhatsApp recipient again.");
        DataRow row = table.Rows[0];
        var selected = new Values(Convert.ToInt32(row["RecordId"]),
            Convert.ToString(row["RecordName"]) ?? "",
            row.IsNull("AdmissionNumber") ? null : Convert.ToString(row["AdmissionNumber"]),
            row.IsNull("AdmissionDate") ? null : Convert.ToDateTime(row["AdmissionDate"]),
            row.IsNull("Outstanding") ? null : Convert.ToDecimal(row["Outstanding"]));
        if (!needsFees) return selected;
        using DataTable audience = await DatabaseHelper.QuerySqlAsync(SharedOutstandingSql,
            DatabaseHelper.Int("@UserId", actor),
            DatabaseHelper.P("@RecipientId", recipientId));
        if (actor != CurrentUser.UserId)
            throw new InvalidOperationException("The signed-in user changed. Review the fee reminder again.");
        return AggregateOutstanding(selected, audience);
    }
}
