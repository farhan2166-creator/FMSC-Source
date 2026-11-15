using System.Globalization;
using System.Text.Json;
using Microsoft.Reporting.WinForms;

namespace FMSC_SMS.Core;

/// <summary>A bill or saved receipt prepared from the counter's reporting sources.</summary>
internal sealed class FeeMessageDocument : IDisposable
{
    private readonly DataTable data;
    private readonly int admissionId;
    private readonly int[] billedStudents;
    private DataTable? issuedBill;
    private readonly bool family;
    private readonly bool receipt;
    internal decimal Amount { get; }
    internal string ReceiptNumber { get; }
    internal string SuggestedFileName => ReportDocumentNaming.ExportFileName(
        receipt ? $"{(family ? "Family" : "Student")} Receipt {ReceiptNumber}" : family ? "Family Fee Bill" : "Student Fee Bill");
    internal string ReviewKey { get; }
    internal string OutstandingDetails { get; }

    internal FeeMessageDocument(DataTable data, int admissionId, int[] members, int[] billedStudents,
        bool family, bool receipt, decimal amount, string receiptNumber = "", string outstandingDetails = "")
    {
        this.data = data;
        this.admissionId = admissionId;
        this.billedStudents = billedStudents;
        this.family = family;
        this.receipt = receipt;
        Amount = amount;
        ReceiptNumber = receiptNumber;
        OutstandingDetails = outstandingDetails;
        string content;
        if (receipt)
        {
            using var writer = new StringWriter(CultureInfo.InvariantCulture);
            data.WriteXml(writer, XmlWriteMode.WriteSchema);
            content = writer.ToString();
        }
        else content = FeeBillDocuments.Snapshot(data);
        ReviewKey = string.Join(",", members) + "\n" + content + "\n" + outstandingDetails;
    }

    internal static async Task<FeeMessageDocument> ReadAsync(int admissionId, bool family, bool receipt)
    {
        if (!CommunicationAccess.CanMessageStudent(CurrentUser.HasPermission, fees: true))
            throw new UnauthorizedAccessException("School-message, student-view and fee-view permissions are required.");
        using var members = await FeeBillPreview.ReadMembersAsync(admissionId, "Fees.View");
        var anchor = members.AsEnumerable().SingleOrDefault(row => row.Field<int>("AdmissionId") == admissionId)
            ?? throw new CommunicationValidationException("The selected student is no longer available.");
        int[] ids = members.AsEnumerable().Select(row => row.Field<int>("AdmissionId")).Order().ToArray();
        if (family && ids.Length < 2)
            throw new CommunicationValidationException("This student has no linked siblings. Choose This student.");
        var institution = await InstitutionProfile.GetPrintIdentityAsync();
        if (string.IsNullOrWhiteSpace(institution.Name))
            throw new CommunicationValidationException("Check School Profile before preparing a fee document.");
        if (receipt) return await ReadReceiptAsync(institution, admissionId, ids, family);

        using var charges = await FeeBillPreview.ReadChargesAsync(admissionId, family ? ids : new[] { admissionId }, wholeFamily: family);
        var balances = charges.AsEnumerable().Where(row => Convert.ToDecimal(row["Due"]) > 0).ToArray();
        if (balances.Length == 0)
            throw new CommunicationValidationException("There are no posted outstanding charges for this selection.");
        string parent = Convert.ToString(anchor["FatherName"]) ?? "";
        int[] billed = balances.Select(row => row.Field<int>("AdmissionId")).Distinct().Order().ToArray();
        var data = family
            ? FamilyBillReport.BuildData(institution.Name, institution.ContactNumber, institution.Address, parent, charges)
            : StudentBillReport.BuildData(institution.Name, institution.ContactNumber, institution.Address, parent, charges);
        decimal total = balances.Sum(row => Convert.ToDecimal(row["Due"]));
        var students = FeeStudentOrder.Rows(charges, "Reference").Where(row => Convert.ToDecimal(row["Due"]) > 0)
            .GroupBy(row => row.Field<int>("AdmissionId")).Select(group => new CommunicationFeeDetails.Student(group.Key,
                Convert.ToString(group.First()["StudentName"]) ?? "", Convert.ToString(group.First()["Reference"]) ?? "",
                string.Join(" / ", group.Select(FeeBillDataBuilder.PlacementText).Where(value => value.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)), group.Sum(row => Convert.ToDecimal(row["Due"])))).ToArray();
        return new(data, admissionId, ids, billed, family, false, total,
            outstandingDetails: CommunicationFeeDetails.Format(institution.Name, DateTime.Today, students, total, family));
    }

    private static async Task<FeeMessageDocument> ReadReceiptAsync(InstitutionProfile.PrintIdentity institution,
        int admissionId, int[] familyIds, bool family)
    {
        // A whole receipt may be shared only when every saved child is still in this family.
        // For one student, the existing child-copy builder removes all other children and totals.
        using var latest = await DatabaseHelper.QuerySqlAsync("""
            EXEC dbo.RequirePermission @UserId,N'Fees.View';
            SELECT TOP(1) R.ReceiptNumber
            FROM dbo.CollectionReceipts R
            WHERE R.PaymentMethod<>N'Fee changes'
                AND EXISTS(SELECT 1 FROM dbo.CollectionReceiptPayments L JOIN dbo.FeePayments P ON P.FeePaymentId=L.FeePaymentId
                    WHERE L.CollectionReceiptId=R.CollectionReceiptId AND P.Status=N'Completed' AND P.TotalAmount>0
                        AND P.IsCreditApplication=0 AND (@Family=1 OR P.AdmissionId=@AdmissionId))
                AND ((@Family=0 AND EXISTS(SELECT 1 FROM OPENJSON(R.SnapshotJson,'$.Students') WITH(AdmissionId int) S
                            WHERE S.AdmissionId=@AdmissionId))
                    OR (@Family=1 AND (SELECT COUNT(*) FROM OPENJSON(R.SnapshotJson,'$.Students'))>1
                        AND NOT EXISTS(SELECT AdmissionId FROM OPENJSON(R.SnapshotJson,'$.Students') WITH(AdmissionId int)
                            EXCEPT SELECT CONVERT(int,[value]) FROM OPENJSON(@FamilyIds))))
            ORDER BY R.VisitSequence DESC,R.ReceiptNumber DESC;
            """, DatabaseHelper.Int("@UserId", CurrentUser.UserId), DatabaseHelper.Int("@AdmissionId", admissionId),
            DatabaseHelper.Bit("@Family", family), DatabaseHelper.P("@FamilyIds", JsonSerializer.Serialize(familyIds)));
        if (latest.Rows.Count != 1)
            throw new CommunicationValidationException("No saved payment receipt is available for this selection.");
        string number = latest.Rows[0].Field<string>("ReceiptNumber")!;
        using var saved = await DatabaseHelper.QueryUserSetAsync("GetCollectionReceipt", DatabaseHelper.P("@ReceiptNumber", number));
        if (saved.Tables.Count < 3 || saved.Tables[0].Rows.Count != 1)
            throw new CommunicationValidationException("The saved receipt could not be read. Review it in Recent Payments.");
        var header = saved.Tables[0].Rows[0];
        if (FeeChangeReport.IsAcknowledgement(header) || (family && saved.Tables[1].AsEnumerable()
            .Any(row => !familyIds.Contains(row.Field<int>("AdmissionId")))))
            throw new CommunicationValidationException("The saved receipt does not match this family or payment.");
        await FeeReceiptDocuments.ReadChildCashReceivedAsync(number, header, saved.Tables[1]);
        FeeReceiptDocuments.ApplyLegacyReceiptNotice(header);
        using var child = family ? null : FeeReceiptChildCopy.Extract(saved, admissionId);
        var source = child ?? saved;
        using var lines = source.Tables[2].Copy();
        await FeeCarryForwardLabels.ApplyAsync(lines, number);
        var data = FeeReceiptDocuments.BuildData(institution, source.Tables[0].Rows[0], source.Tables[1], lines);
        return new(data, admissionId, familyIds, Array.Empty<int>(), family, true,
            Convert.ToDecimal(source.Tables[0].Rows[0]["CashReceived"]), number);
    }

    private async Task<DataTable> PrepareAsync()
    {
        if (!receipt && issuedBill is null)
            issuedBill = await FeeBillDocuments.IssueAsync(data,
                billedStudents.Contains(admissionId) ? admissionId : billedStudents[0], billedStudents);
        using var saved = issuedBill is null ? null : FeeBillDocuments.ReadSavedData(issuedBill.Rows[0]);
        DataTable source = saved ?? data;
        if (receipt) await FeeDocumentParents.ApplyAsync(source);
        bool grouped = source.Columns.Contains("FamilyRowKind");
        var display = receipt ? grouped ? FamilyFeeDocumentPresentation.Receipt(source) : FeeReceiptPresentation.Prepare(source)
            : grouped ? FamilyFeeDocumentPresentation.Bill(source) : FeeBillPresentation.Prepare(source);
        if (receipt && !grouped) FeeDocumentData.GroupPeriods(display);
        return display;
    }

    private (string Path, string DataSet) Template(DataTable display)
    {
        bool grouped = display.Columns.Contains("FamilyRowKind");
        var (template, dataSet) = (receipt, grouped) switch
        {
            (true, true) => (FamilyReceiptReport.TemplateFile, FamilyReceiptReport.DataSetName),
            (true, false) => (StudentReceiptReport.TemplateFile, StudentReceiptReport.DataSetName),
            (false, true) => (FamilyBillReport.TemplateFile, FamilyBillReport.DataSetName),
            _ => (StudentBillReport.TemplateFile, StudentBillReport.DataSetName)
        };
        return (Path.Combine(AppContext.BaseDirectory, "Reports", template), dataSet);
    }

    internal async Task<Bitmap> RenderImageAsync()
    {
        using var display = await PrepareAsync();
        var (path, dataSet) = Template(display);
        // Rendering stays off the UI thread; clipboard access belongs to the caller's STA thread.
        return await Task.Run(() =>
        {
            if (!receipt)
            {
                using var bills = FeeBillRollDocument.Render(path, dataSet, display);
                return FeeMessageImage.Combine(bills.Pages.Select(page => page.Image));
            }
            using var printable = RecordTextPolicy.PrintCopy(display);
            using var report = new LocalReport();
            report.LoadReportDefinition(new StringReader(FeeDocumentLayout.SizeToContent(path, display)));
            report.DataSources.Add(new ReportDataSource(dataSet, printable));
            var pages = new List<MemoryStream>();
            try
            {
                report.Render("IMAGE", "<DeviceInfo><OutputFormat>EMF</OutputFormat><StartPage>0</StartPage><EndPage>0</EndPage></DeviceInfo>",
                    (name, extension, encoding, mime, seek) =>
                    {
                        var stream = new MemoryStream();
                        pages.Add(stream);
                        return stream;
                    }, out _);
                return FeeMessageImage.Combine(pages);
            }
            finally { foreach (var page in pages) page.Dispose(); }
        });
    }

    internal async Task ExportAsync(string file)
    {
        using var display = await PrepareAsync();
        var (templatePath, dataSet) = Template(display);
        byte[] pdf;
        if (receipt)
        {
            using var printable = RecordTextPolicy.PrintCopy(display);
            using var report = new LocalReport();
            report.LoadReportDefinition(new StringReader(FeeDocumentLayout.SizeToContent(templatePath, display)));
            report.DataSources.Add(new ReportDataSource(dataSet, printable));
            pdf = report.Render("PDF");
        }
        else
        {
            using var report = FeeBillRollDocument.Render(templatePath, dataSet, display);
            pdf = report.ExportPdf();
        }
        AtomicExportFile.Publish(file, path => File.WriteAllBytes(path, pdf), PdfTableExporter.ValidatePdf);
    }

    public void Dispose()
    {
        issuedBill?.Dispose();
        data.Dispose();
    }
}
