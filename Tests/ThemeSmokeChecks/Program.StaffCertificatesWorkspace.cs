using System.Data;
using FMSC_SMS.Pages.Staff.Certificates;
using FMSC_SMS.Pages.Students.Certificates;

internal static partial class Program
{
    private static void CheckStaffCertificatesWorkspace()
    {
        var kinds = StaffCertificateRegisterReader.Kinds;
        Check(kinds.Length == 2 && kinds.Select(kind => kind.Key).Distinct().Count() == 2,
            "Staff workspace uses the two existing certificate kinds, with stable distinct keys.");
        Check(kinds.Single(kind => kind.Key == "Bank").NeedsUserId,
            "Bank history keeps the user parameter required by its existing stored procedure.");
        foreach (Type type in new[] { typeof(RegularLecturerCertificatePage), typeof(BankAccountOpeningCertificatePage) })
            Check(typeof(IStaffCertificateWorkspaceEditor).IsAssignableFrom(type),
                type.Name + " retains its original workflow behind the new workspace.");

        using var teacher = new DataTable();
        teacher.Columns.Add("StaffId", typeof(int));
        teacher.Columns.Add("RegularLecturerCertificateId", typeof(int));
        teacher.Columns.Add("CertificateNumber");
        teacher.Columns.Add("CertificateDate", typeof(DateTime));
        teacher.Columns.Add("CreatedAt", typeof(DateTime));
        teacher.Columns.Add("CollectionStatus");
        teacher.Columns.Add("CollectedAt", typeof(DateTime));
        teacher.Columns.Add("PreviousCertificateId", typeof(int));
        teacher.Rows.Add(7, 32, "T-32", DateTime.Today.AddDays(-4),
            DateTime.Today.AddDays(-4), "Collected", DateTime.Today.AddDays(-3), DBNull.Value);
        teacher.Rows.Add(7, 33, "T-33", DateTime.Today.AddDays(-1),
            DateTime.Today.AddDays(-1), "Not Collected", DBNull.Value, 32);

        using var bank = new DataTable();
        bank.Columns.Add("StaffId", typeof(int));
        bank.Columns.Add("BankAccountOpeningCertificateId", typeof(int));
        bank.Columns.Add("CertificateDate", typeof(DateTime));
        bank.Columns.Add("CreatedAt", typeof(DateTime));
        bank.Columns.Add("CollectionStatus");
        bank.Rows.Add(7, 49, DateTime.Today.AddDays(-2),
            DateTime.Today.AddDays(-2), "Not Collected");

        using var combined = StaffCertificateRegisterReader.CreateTable();
        StaffCertificateRegisterReader.AppendRecords(combined, kinds[0], teacher, 7);
        StaffCertificateRegisterReader.AppendRecords(combined, kinds[1], bank, 7);
        Check(combined.Rows.Count == 3, "Both staff certificate types appear in one register.");
        Check(combined.AsEnumerable().Select(row => (string)row["Kind"]).Distinct().Count() == 2,
            "The register preserves type identity for exact copy dispatch.");
        Check(combined.AsEnumerable().Single(row => Convert.ToInt32(row["RecordId"]) == 32)
                .Field<string>("Collection") == "Collected",
            "A collected staff copy retains its saved collection state.");
        Check(Convert.ToInt32(combined.AsEnumerable().Single(row => Convert.ToInt32(row["RecordId"]) == 33)["SourceCopyId"]) == 32,
            "The new copy links to its collected predecessor, without altering it.");
        Check(combined.AsEnumerable().Single(row => Convert.ToInt32(row["RecordId"]) == 49)
                .Field<string>("Reference") == "Record #49",
            "Missing certificate number displays a grounded record reference.");

        combined.DefaultView.Sort = "RecordedAt DESC, RecordId DESC";
        var selection = CertificateWorkspaceSelection.Resolve(combined.DefaultView,
            new CertificateWorkspaceSelection.Key("Lecturer", 32, 0));
        Check(selection is not null && Convert.ToInt32(selection["RecordId"]) == 32,
            "Exact historical teacher copies stay selected after refresh.");
        selection = CertificateWorkspaceSelection.Resolve(combined.DefaultView,
            new CertificateWorkspaceSelection.Key("Lecturer", 0, 0, PreferNewest: true));
        Check(selection is not null && Convert.ToInt32(selection["RecordId"]) == 33,
            "New teacher issuance selects the latest copy of its own type.");
        selection = CertificateWorkspaceSelection.Resolve(combined.DefaultView,
            new CertificateWorkspaceSelection.Key("Bank", 49, 0));
        Check(selection is not null && Convert.ToString(selection["Kind"]) == "Bank",
            "Bank certificates cannot accidentally switch into the lecturer editor.");

        teacher.Rows[1]["StaffId"] = 8;
        bool refused = false;
        try { StaffCertificateRegisterReader.AppendRecords(StaffCertificateRegisterReader.CreateTable(), kinds[0], teacher, 7); }
        catch (InvalidOperationException) { refused = true; }
        Check(refused, "The shared register refuses another staff member's certificates.");
        using var malformed = new DataTable();
        malformed.Columns.Add("StaffId", typeof(int));
        refused = false;
        try { StaffCertificateRegisterReader.AppendRecords(StaffCertificateRegisterReader.CreateTable(), kinds[0], malformed, 7); }
        catch (InvalidOperationException) { refused = true; }
        Check(refused, "A missing certificate identity fails closed rather than displaying an unrelated row.");
    }
}
