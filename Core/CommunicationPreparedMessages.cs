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
    internal sealed record BuiltIn(string Name, string Purpose, string Body, bool FamilyOnly = false, string? Channel = null, string LanguageCode = "en");
    internal sealed record ComposeTemplate(string Name, string Purpose, string Body, string? Channel = null, bool FamilyOnly = false, string LanguageCode = "en")
    {
        internal bool Matches(CommunicationTarget target) => !HasEventContext(Body) && Purpose == target.Purpose
            && (Channel is null || Channel == target.Channel)
            && (!FamilyOnly || target.Audience == CommunicationAudience.Family);
        public override string ToString() => Name;
    }
    internal sealed record Values(int RecordId, string RecordName,
        string? AdmissionNumber, DateTime? AdmissionDate, decimal? Outstanding)
    {
        internal string? FatherName { get; init; }
        internal string? ClassName { get; init; }
    }
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
        new BuiltIn("Payment received acknowledgement", "Individual",
            "Dear parent/guardian, a fee payment for [StudentName] (admission [AdmissionNumber]) has been recorded. Please refer to the issued receipt for the official payment details."),
        new BuiltIn("Student birthday greeting", "Individual",
            "Dear parent/guardian, best wishes to [StudentName] on their birthday! Warm greetings from the school."),
        new BuiltIn("Individual student notice", "Individual",
            "Dear parent/guardian of [StudentName], please contact the school office regarding an important notice."),
        new BuiltIn("School announcement", "Announcement",
            "Dear parent/guardian, this is an announcement from the school. Please contact the school office for more information."),
        new BuiltIn("Parent-teacher meeting request", "Individual",
            "Dear parent/guardian of [StudentName], the school would like to arrange a parent-teacher meeting. Please contact the office to agree on a convenient date and time."),
        new BuiltIn("Attendance follow-up", "Individual",
            "Dear parent/guardian of [StudentName] (admission [AdmissionNumber]), please contact the school office to discuss the student's attendance."),
        new BuiltIn("Admission documents follow-up", "Individual",
            "Dear parent/guardian of [StudentName], please contact the school office about documents required for the admission record."),
        new BuiltIn("Current class information", "Individual",
            "Dear parent/guardian, [StudentName] (admission [AdmissionNumber]) is recorded in [ClassName]. Please contact the school office if the class information needs correction."),
        new BuiltIn("Examination information", "Announcement",
            "Dear parent/guardian, the school has an important examination or assessment notice. Please contact the school office for the confirmed schedule and instructions."),
        new BuiltIn("Timetable update", "Announcement",
            "Dear parent/guardian, there is an update regarding the school timetable. Please contact the school office for the current schedule."),
        new BuiltIn("School schedule notice", "Announcement",
            "Dear parent/guardian, the school has an important update regarding its operating schedule. Please contact the school office for details."),
        new BuiltIn("Staff coordination notice", "Staff",
            "Dear [RecipientName], please contact the school administration regarding a staff coordination update. Thank you."),
        new BuiltIn("Fee payment arrangements", "FeeReminder",
            "Dear parent/guardian, the current outstanding balance for [StudentName] is PKR [OutstandingAmount]. If you need to discuss payment arrangements, please contact the school accounts office."),
        new BuiltIn("Outstanding fee statement", "FeeReminder",
            "Dear parent/guardian, the outstanding fee balance for [StudentName] (admission [AdmissionNumber]) is PKR [OutstandingAmount]. You can request a statement of charges and payments from the school accounts office."),
        new BuiltIn("Leave documentation request", "Individual",
            "Dear parent/guardian of [StudentName], please provide the class teacher with a written leave request and the dates involved so the school can update the attendance record."),
        new BuiltIn("Student progress discussion", "Individual",
            "Dear parent/guardian of [StudentName], the class teacher would like to discuss the student's progress and agree on how best to support their learning. Please reply with a convenient time to talk."),
        new BuiltIn("Guardian contact details confirmation", "Individual",
            "Dear parent/guardian of [StudentName], please contact the school office if your phone number or emergency contact details have changed so we can update the school record."),
        new BuiltIn("Exam day preparation", "Announcement",
            "Dear parent/guardian, please check the examination timetable for arrival times and help your child pack the stationery and materials listed in the exam instructions."),
        new BuiltIn("Books and classroom supplies", "Announcement",
            "Dear parent/guardian, please review the class book and supplies list and label your child's materials before sending them to school. Ask the class teacher for a copy of the list if needed."),
        new BuiltIn("Report card collection", "Announcement",
            "Dear parent/guardian, please contact the school office to confirm when and where you can collect your child's report card."),
        new BuiltIn("School reopening preparations", "Announcement",
            "Dear parent/guardian, as school reopening approaches, please check the published timetable and help your child prepare their books, supplies and uniform for the first day back."),
        new BuiltIn("School event attendance", "Announcement",
            "Dear parent/guardian, please review the school's event notice for the venue and timings. Let the class teacher know whether your child will attend."),
        new BuiltIn("Staff meeting preparation", "Staff",
            "Dear [RecipientName], please bring any agenda items and relevant updates to the next staff meeting. Send any items you would like included to the school administration in advance."),
        new BuiltIn("Staff record update", "Staff",
            "Dear [RecipientName], please inform the school administration if your contact details have changed so your staff record can be updated."),
        new BuiltIn("Family records update", "Individual",
            "Dear parent/guardian, please let the school office know if the contact or emergency details for any of your children have changed.", FamilyOnly: true),
        new BuiltIn("Family meeting request", "Individual",
            "Dear parent/guardian, the school would like to arrange a meeting about your children's school records. Please contact the office to agree on a convenient time.", FamilyOnly: true),
        // Curated Urdu drafts: explicitly chosen and reviewed by the operator.
        // Limit to free manual WhatsApp; billed Unicode SMS needs separate
        // segment/cost disclosure, and Meta needs an approved provider template.
        new BuiltIn("Outstanding fee reminder (Urdu)", "FeeReminder",
            "محترم والدین/سرپرست، [StudentName] (داخلہ نمبر [AdmissionNumber]) کی بقایا فیس PKR [OutstandingAmount] ہے۔ اگر ادائیگی ہو چکی ہے تو براہ کرم اسکول دفتر سے رابطہ کریں۔", Channel: "WhatsApp", LanguageCode: "ur"),
        new BuiltIn("New admission welcome (Urdu)", "Individual",
            "محترم والدین/سرپرست، [StudentName] (داخلہ نمبر [AdmissionNumber]) کا اسکول میں داخلہ [AdmissionDate] کو ہوا۔ آپ کو خوش آمدید کہتے ہیں۔", Channel: "WhatsApp", LanguageCode: "ur"),
        new BuiltIn("Payment received acknowledgement (Urdu)", "Individual",
            "محترم والدین/سرپرست، [StudentName] کی فیس کی ادائیگی درج ہو گئی ہے۔ سرکاری تفصیل کے لیے جاری کردہ رسید ملاحظہ کریں۔", Channel: "WhatsApp", LanguageCode: "ur"),
        new BuiltIn("Student birthday greeting (Urdu)", "Individual",
            "محترم والدین/سرپرست، [StudentName] کو سالگرہ کی بہت مبارک ہو۔ اسکول کی طرف سے نیک تمنائیں۔", Channel: "WhatsApp", LanguageCode: "ur"),
        new BuiltIn("Examination information (Urdu)", "Announcement",
            "محترم والدین/سرپرست، اسکول کی جانب سے امتحانات کے متعلق اہم اطلاع ہے۔ تصدیق شدہ شیڈول اور ہدایات کے لیے دفتر سے رابطہ کریں۔", Channel: "WhatsApp", LanguageCode: "ur"),
        new BuiltIn("School announcement (Urdu)", "Announcement",
            "محترم والدین/سرپرست، اسکول کی جانب سے ایک اہم اعلان ہے۔ مزید تفصیل کے لیے اسکول دفتر سے رابطہ کریں۔", Channel: "WhatsApp", LanguageCode: "ur"),
        new BuiltIn("Parent-teacher meeting request (Urdu)", "Individual",
            "محترم والدین/سرپرست، [StudentName] کے متعلق والدین اور استاد کی ملاقات طے کرنے کے لیے براہ کرم اسکول دفتر سے رابطہ کریں۔", Channel: "WhatsApp", LanguageCode: "ur")
    };

    internal static IReadOnlyList<ComposeTemplate> BuiltInChoices { get; } = BuiltIns
        .Select(item => new ComposeTemplate(item.Name, item.Purpose, item.Body,
            Channel: item.Channel, FamilyOnly: item.FamilyOnly, LanguageCode: item.LanguageCode)).ToArray();

    // These are deliberately limited, readable placeholders. No SQL, scripting,
    // formula expressions, or arbitrary data paths can be supplied by a template.
    private static readonly Regex Field = new(
        @"\[(?<key>[A-Za-z][A-Za-z0-9]*)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    internal static bool HasEventContext(string body)=>new[]{"[PaymentDetails]","[PaymentAmount]","[CreditApplied]","[ReceiptNumber]","[NoticeTitle]","[NoticeText]"}.Any(field=>body.Contains(field,StringComparison.Ordinal));
    internal static bool HasFields(string body) => Field.IsMatch(body);

    internal static Values FromPreview(CommunicationPreviewEntry entry)
        => entry.PreparedValues ?? new Values(entry.RecordId, entry.RecordName, entry.AdmissionNumber,
            entry.AdmissionDate, entry.Outstanding)
        {
            FatherName = entry.FatherName,
            ClassName = entry.ClassName
        };

    internal static void ValidateFormat(string body, string? purpose = null,bool allowEventContext=false)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > 1600)
            throw new CommunicationValidationException("Write a message of up to 1600 characters.");
        string remaining = Field.Replace(body, match => match.Groups["key"].Value switch {
            "PaymentDetails" or "PaymentAmount" or "CreditApplied" or "ReceiptNumber" or "NoticeTitle" or "NoticeText" when allowEventContext => "",
            "RecipientName" or "StudentName" or "AdmissionNumber" or "AdmissionDate" => "",
            "FatherName" or "ClassName" when purpose != "Staff" => "",
            "FatherName" or "ClassName" => throw new CommunicationValidationException("Student and class fields cannot be used for a staff message."),
            "OutstandingAmount" when purpose is null or "FeeReminder" => "",
            "OutstandingAmount" => throw new CommunicationValidationException("Use [OutstandingAmount] with an outstanding-fee reminder."),
            _ => throw new CommunicationValidationException("Unsupported prepared field " + match.Value + ". Use [RecipientName], [StudentName], [FatherName], [ClassName], [AdmissionNumber], [AdmissionDate] or [OutstandingAmount].")
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
            "RecipientName" or "StudentName" => !string.IsNullOrWhiteSpace(values.RecordName)
                ? values.RecordName.Trim() : "school record #" + values.RecordId.ToString(CultureInfo.InvariantCulture),
            "FatherName" => !string.IsNullOrWhiteSpace(values.FatherName)
                ? values.FatherName.Trim()
                : throw new CommunicationValidationException("Father's name is missing for this student. Check the school record or remove [FatherName]."),
            "ClassName" => !string.IsNullOrWhiteSpace(values.ClassName)
                ? values.ClassName.Trim()
                : throw new CommunicationValidationException("No current class is available for this student. Check the placement or remove [ClassName]."),
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
        // A single guardian number may represent siblings in different classes.
        // Never attribute one child's class or father name to the whole family.
        return selected with
        {
            RecordName = names, AdmissionNumber = admissions,
            Outstanding = total, AdmissionDate = date,
            FatherName = null, ClassName = null
        };
    }

    internal static Values FamilyFeeValues(CommunicationPreviewEntry selected)
    {
        var members=selected.FamilyFees.OrderBy(m=>m.RecordId).ToArray();
        if(members.Length==0 || members.Any(m=>m.RecordId<=0 || m.Outstanding<0) ||
            members.Select(m=>m.RecordId).Distinct().Count()!=members.Length ||
            members.All(m=>m.RecordId!=selected.RecordId) ||
            members.Sum(m=>m.Outstanding)!=(selected.Outstanding ?? 0))
            throw new CommunicationValidationException("The family fee records changed. Review the family balance again.");
        var values=FromPreview(selected);
        if(members.Length==1) return values;
        return values with {
            RecordName=string.Join(", ",members.Select(m=>CommunicationRecipientResolver.DisplayName("Student",m.RecordId,m.RecordName))),
            AdmissionNumber=string.Join(", ",members.Select(m=>string.IsNullOrWhiteSpace(m.AdmissionNumber)
                ? m.RecordId.ToString(CultureInfo.InvariantCulture) : m.AdmissionNumber)),
            AdmissionDate=members.All(m=>m.AdmissionDate==members[0].AdmissionDate) ? members[0].AdmissionDate : null,
            FatherName=null,ClassName=null
        };
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
        CROSS JOIN dbo.Students S
        CROSS APPLY msg.RecordGuardian(S.AdmissionId) Guardian
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
          AND (H.State='Opened' OR (@PreviewBatchId=B.BatchId AND H.State='Pending'))
          AND R.State='Pending' AND B.State IN ('Confirmed','Sending')
          AND S.Status=N'Active' AND Guardian.HasConsent=1 AND Guardian.PhoneE164=R.PhoneE164
          AND E.Status=N'Active'
          AND (B.CampusId=0 OR CCS.CampusId=B.CampusId)
          AND (B.ClassId=0 OR CCS.ClassId=B.ClassId)
          AND (B.SingleStudentScope=0 OR B.StudentId=0 OR S.AdmissionId=B.StudentId)
          AND F.Outstanding>0
          AND NOT EXISTS (SELECT 1 FROM msg.Suppression O
                          WHERE O.Channel='WhatsApp' AND O.PhoneE164=R.PhoneE164)
        ORDER BY S.StudentName,S.AdmissionId;
        """;

    private static async Task<Values> ReadSharedFeeValuesAsync(
        long recipientId, int actor, Values selected, Guid? previewBatchId)
    {
        using DataTable rows = await DatabaseHelper.QuerySqlAsync(SharedFeeRecordsSql,
            DatabaseHelper.Int("@UserId",actor),
            DatabaseHelper.P("@RecipientId",recipientId),
            DatabaseHelper.P("@PreviewBatchId",previewBatchId));
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

    // Previewing a saved message leaves the handoff ledger untouched. Opening
    // the chat still requires msg.OpenManualHandoff to validate and record it.
    internal static async Task<string> ReadManualPreviewAsync(Guid batchId, long recipientId)
    {
        var (values, body) = await ReadManualRecipientAsync(recipientId, needsFees: false, previewBatchId: batchId);
        return Render(body, values);
    }

    internal static async Task<Values> ReadLiveAsync(long recipientId, bool needsFees) =>
        (await ReadManualRecipientAsync(recipientId, needsFees, previewBatchId: null)).Values;

    private static async Task<(Values Values, string Body)> ReadManualRecipientAsync(
        long recipientId, bool needsFees, Guid? previewBatchId)
    {
        const string sql = """
            SET NOCOUNT ON;
            EXEC dbo.RequirePermission @UserId,N'Communication.Send';
            DECLARE @Audience varchar(20), @CampusId int, @ClassId int, @StudentId int,
                @StaffId int, @Owner int, @Body nvarchar(1600);
            SELECT @Audience=B.Audience,@CampusId=B.CampusId,@ClassId=B.ClassId,
                @StudentId=CASE WHEN B.Audience='OutstandingFees' AND B.SingleStudentScope=0 THEN 0 ELSE B.StudentId END,
                @StaffId=B.StaffId,@Owner=M.CreatedBy,@Body=B.Body
            FROM msg.ManualHandoff H
            JOIN msg.Recipient R ON R.RecipientId=H.RecipientId
            JOIN msg.Batch B ON B.BatchId=R.BatchId
            JOIN msg.ManualBatch M ON M.BatchId=B.BatchId
            WHERE H.RecipientId=@RecipientId AND (@PreviewBatchId IS NULL OR B.BatchId=@PreviewBatchId)
              AND (H.State='Opened' OR (@PreviewBatchId IS NOT NULL AND H.State='Pending'))
              AND R.State='Pending' AND B.State IN ('Confirmed','Sending');
            IF @Audience IS NULL
                THROW 51120,'Manual recipient is no longer available.',1;
            IF @Owner<>@UserId EXEC dbo.RequirePermission @UserId,N'Communication.View';
            IF @Audience='Staff' EXEC dbo.RequirePermission @UserId,N'Staff.View';
            ELSE EXEC dbo.RequirePermission @UserId,N'Students.View';
            IF @NeedFees=1 AND @Audience NOT IN ('OutstandingFees','FeeStudent','FeeFamily')
                THROW 51120,'Fee amounts require a fee-specific audience.',1;
            IF @Audience IN ('OutstandingFees','FeeStudent','FeeFamily') EXEC dbo.RequirePermission @UserId,N'Fees.View';
            SELECT COALESCE(R.AdmissionId,R.StaffId) RecordId,
                COALESCE(NULLIF(LTRIM(RTRIM(S.StudentName)),N''),
                         NULLIF(LTRIM(RTRIM(T.StaffName)),N''),N'') RecordName,
                S.AdmissionNumber,S.AdmissionDate,S.FatherName,
                CASE WHEN E.Status=N'Active' THEN ClassRecord.ClassName END AS ClassName,
                F.Outstanding,@Audience AS Audience,@Body AS Body,R.PhoneE164,
                (SELECT STRING_AGG(CONVERT(varchar(max),M.AdmissionId),',')
                    WITHIN GROUP(ORDER BY M.AdmissionId)
                 FROM msg.ReviewedFeeStudent M WHERE M.RecipientId=R.RecipientId) ReviewedFamilyIds
            FROM msg.ManualHandoff H
            JOIN msg.Recipient R ON R.RecipientId=H.RecipientId
            LEFT JOIN dbo.Students S ON S.AdmissionId=R.AdmissionId
            LEFT JOIN dbo.Staff T ON T.StaffId=R.StaffId
            OUTER APPLY msg.RecordGuardian(S.AdmissionId) Guardian
            OUTER APPLY (SELECT TOP(1) E.ClassSectionId,E.Status FROM dbo.Enrollments E
                WHERE E.AdmissionId=S.AdmissionId
                ORDER BY CASE WHEN E.Status=N'Active' THEN 0 ELSE 1 END,E.EnrollmentId DESC) E
            LEFT JOIN dbo.CampusClassSections CCS ON CCS.ClassSectionId=E.ClassSectionId
            LEFT JOIN dbo.Classes ClassRecord ON ClassRecord.ClassId=CCS.ClassId
            OUTER APPLY (
                SELECT SUM(dbo.GetFeeChargeBalance(C.BillingChargeId)) Outstanding
                FROM dbo.BillingCharges C
                JOIN dbo.Students FeeStudent
                  ON FeeStudent.AdmissionId=C.AdmissionId AND FeeStudent.Status=N'Active'
                WHERE (@NeedFees=1 OR @Audience IN ('OutstandingFees','FeeStudent','FeeFamily'))
                  AND C.Status<>N'Cancelled'
                  AND (C.AdmissionId=S.AdmissionId
                    OR (@Audience='FeeFamily' AND EXISTS (
                      SELECT 1 FROM dbo.FamilyMembers Origin
                      JOIN dbo.FamilyMembers Related ON Related.FamilyId=Origin.FamilyId
                      WHERE Origin.AdmissionId=S.AdmissionId
                        AND Related.AdmissionId=C.AdmissionId)))
            ) F
            WHERE H.RecipientId=@RecipientId
              AND (H.State='Opened' OR (@PreviewBatchId IS NOT NULL AND H.State='Pending'))
              AND R.State='Pending'
              AND EXISTS(SELECT 1 FROM msg.PreparedEventEligible(R.BatchId))
              AND NOT EXISTS (
                  SELECT 1 FROM msg.Suppression O
                  WHERE O.Channel='WhatsApp' AND O.PhoneE164=R.PhoneE164)
              AND ((S.AdmissionId IS NOT NULL AND S.Status=N'Active'
                    AND ((@Audience IN ('Student','Family','FeeStudent','FeeFamily') AND S.AdmissionId=@StudentId
                        AND Guardian.HasConsent=1 AND Guardian.PhoneE164=R.PhoneE164)
                    OR (Guardian.HasConsent=1 AND Guardian.PhoneE164=R.PhoneE164
                    AND (@Audience='School'
                      OR (@Audience='Campus' AND E.Status=N'Active' AND CCS.CampusId=@CampusId)
                      OR (@Audience='Class' AND E.Status=N'Active' AND CCS.CampusId=@CampusId AND CCS.ClassId=@ClassId)
                      OR (@Audience='OutstandingFees' AND E.Status=N'Active'
                          AND (@CampusId=0 OR CCS.CampusId=@CampusId) AND (@ClassId=0 OR CCS.ClassId=@ClassId)
                          AND (@StudentId=0 OR S.AdmissionId=@StudentId) AND F.Outstanding>0)))))
                OR (@Audience='Staff' AND T.StaffId=@StaffId AND T.Status=N'Active'
                    AND msg.NormalizeMobile(T.ContactNumber)=R.PhoneE164));
            """;
        int actor = CurrentUser.UserId;
        using DataTable table = await DatabaseHelper.QuerySqlAsync(sql,
            DatabaseHelper.Int("@UserId", actor),
            DatabaseHelper.P("@RecipientId", recipientId),
            DatabaseHelper.P("@PreviewBatchId", previewBatchId),
            DatabaseHelper.P("@NeedFees", needsFees));
        if (actor != CurrentUser.UserId || table.Rows.Count != 1)
            throw new CommunicationValidationException("School records or signed-in account changed. Review the WhatsApp recipient again.");
        DataRow row = table.Rows[0];
        Values selected = new(Convert.ToInt32(row["RecordId"]),
            CommunicationRecipientResolver.DisplayName(Convert.ToString(row["Audience"]) == "Staff" ? "Staff" : "Student",
                Convert.ToInt32(row["RecordId"]), Convert.ToString(row["RecordName"])),
            row.IsNull("AdmissionNumber") ? null : Convert.ToString(row["AdmissionNumber"]),
            row.IsNull("AdmissionDate") ? null : Convert.ToDateTime(row["AdmissionDate"]),
            row.IsNull("Outstanding") ? null : Convert.ToDecimal(row["Outstanding"]))
        {
            FatherName = row.IsNull("FatherName") ? null : Convert.ToString(row["FatherName"]),
            ClassName = row.IsNull("ClassName") ? null : Convert.ToString(row["ClassName"])
        };
        if (string.Equals(Convert.ToString(row["Audience"]), "OutstandingFees", StringComparison.Ordinal))
            selected = await ReadSharedFeeValuesAsync(recipientId, actor, selected, previewBatchId);
        if (string.Equals(Convert.ToString(row["Audience"]), "FeeFamily", StringComparison.Ordinal))
        {
            var family=await CommunicationRecipientResolver.ReadAsync(
                new(CommunicationAudience.FeeFamily,0,0,selected.RecordId,0,"WhatsApp"));
            var current=family.Entries.SingleOrDefault(e=>e.Included);
            if(actor!=CurrentUser.UserId || current is null || current.PhoneE164!=Convert.ToString(row["PhoneE164"]) ||
                string.Join(",",current.FamilyFees.Select(m=>m.RecordId).Order())!=Convert.ToString(row["ReviewedFamilyIds"]))
                throw new CommunicationValidationException("The reviewed family changed. Review a new family fee message; no chat was opened.");
            selected=FromPreview(current);
        }
        return (selected, (string)row["Body"]);
    }
}
