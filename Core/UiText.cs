namespace FMSC_SMS.Core;
/// <summary>Operator-facing sentences and captions that more than one screen says, in one place, so a wording change happens once instead of in fourteen files.</summary>
public static class UiText
{
    /// <summary>Shared captions. Choose by behavior; New, Clear and Reset are distinct actions.</summary>
    public static class Actions
    {
        public const string New = "New";
        public const string Clear = "Clear";
        public const string Reset = "Reset";
        public const string Save = "Save";
        public const string Refresh = "Refresh";
        public const string Reload = "Reload";
        public const string Close = "Close";
        public const string Cancel = "Cancel";
        public const string Print = "Print";
        public const string Export = "Export";
        public const string Preview = "Preview";
        public const string SelectAll = "Select All";
        public const string SelectNone = "Select None";
    }

    /// <summary>Guidance connecting fee setup, student rates and billing.</summary>
    public static class Fees
    {
        // Amount is the fee before discount; Net Amount is Amount - Discount.
        // Payments/balances and old/new comparisons retain their distinct meaning.
        public const string FeeType = "Fee Type";
        public const string Amount = "Amount (Rs.)";
        public const string Discount = "Discount (Rs.)";
        public const string NetAmount = "Net Amount (Rs.)";
        public const string Frequency = "Frequency";
        public const string CurrentAmount = "Current Amount (Rs.)";
        public const string NewAmount = "New Amount (Rs.)";
        public const string PostedAmount = "Posted Amount (Rs.)";
        public const string CollectedAmount = "Collected Amount (Rs.)";
        public const string UncollectedAmount = "Uncollected Amount (Rs.)";
        public const string Due = "Due (Rs.)";
        public const string PayNow = "Pay Now (Rs.)";
        public const string SetupSteps = "Define fee types → Save class rates → Update student rates if needed → Post monthly fees.";
        public const string MatrixScope = "Save Grid saves class rates. Use Update Student Fee Rates for existing students; Fee Posting creates bills.";
        public const string MatrixSaved = "Class rates saved. Existing student fees and bills are unchanged.";
        public const string StudentRateScope = "New rates start from the chosen month. Existing bills stay unchanged. Eligible fees can update even if others cannot.";
        public const string StudentRatesApplied = "No bills created. Use Fee Posting > Post Monthly Fees.";
    }

    /// <summary>Asking the operator for the thing an action needs before it can run. These are the sentences that appear on the most screens.</summary>
    public static class Prompts
    {
        public const string SelectStudent = "Select a student first.";
        public const string SelectStudentAndEnrollment = "Select a student and academic placement first.";
        public const string SearchForStudent = "Search for and select a student.";
        public const string SelectCampusAndClass = "Select a campus and class first.";
        public const string SearchingStudents = "Searching students...";
        public const string NoStudentsFound = "No students found.";
        /// <summary>The student search boxes' placeholder text.</summary>
        public const string StudentSearchPlaceholder = "Search by admission number or student name…";
    }
    /// <summary>The messages an empty grid shows (see UiHelper.SetGridEmptyMessage).</summary>
    public static class EmptyGrid
    {
        public const string SelectStudent = "Select a student to view records.";
        public const string NoRecords = "No records found.";
        public const string RunReport = "Run the report to view results.";
        /// <summary>The fetch failed (shown inline, see UiHelper.SetGridState).</summary>
        public const string LoadFailed = "Could not load the records. Try again.";
        public const string NoHistory = "No history recorded yet.";
    }
    /// <summary>Short field-level messages for inline validation (see FormValidator).</summary>
    public static class Validation
    {
        public const string Required = "Required";
        public const string SelectOne = "Select one";
        public const string InvalidDate = "Invalid date";
        public const string DateRange = "Start is after end";
        public const string EnterAmount = "Enter an amount";
    }
    /// <summary>Wording that belongs to a dialog rather than to a screen.</summary>
    public static class Dialogs
    {
        public const string UnsavedChanges = "Unsaved Changes";
        public const string Continue = "Do you want to continue?";
    }
    /// <summary>Captions for fields whose name is the same on every screen.</summary>
    public static class Fields
    {
        public const string Reason = "Reason (required)";
        public const string RegistrationNumber = "Registration No.";
    }
    /// <summary>The two status lines every report screen shows.</summary>
    public static class Reports
    {
        public const string MatchesFilters = "Report matches the current filters.";
        public const string FiltersChanged = "Filters changed. Run the report again before exporting.";
    }
    /// <summary>Shared captions for ten certificate types across the student and staff certificate workspaces (character, leave, college leave, date of birth, no objection, provisional, regular student, sports, regular lecturer and bank account).</summary>
    public static class Certificates
    {
        /// <summary>Window title of a new certificate.</summary>
        public const string Create = "Create";
        public const string New = "New";
        public const string NextStudent = "Next Student";
        public const string IssueAgainAction = "Issue Again";
        public const string MarkCollected = "Mark Collected";
        /// <summary>Window title of an existing certificate.</summary>
        public const string Update = "Update Certificate";
        public const string StudentGuidance = "Select a student to create a certificate. Select a saved certificate to edit or print.";
        public const string StaffGuidance = "Select staff to create a certificate. Select a saved certificate to edit or print.";
        public const string IssueGuidance = "Select a student to create a certificate. Then issue again, mark collected or print.";
        public const string IssueHistoryGuidance = "See who issued and collected each copy, and when.";
        public const string Details = "Certificate Details";
        public const string Date = "Certificate Date";
        public const string IssuanceHistory = "Certificate Issuance History";
        public const string Collection = "Certificate Collection";
        public const string IssueAgain = "Issue Certificate Again";
        public const string PrintDataMissing = "Certificate print data was not found.";
        public const string FutureDateRefused = "Certificate date cannot be in the future.";
        public const string InvalidIssuance = "The selected certificate issuance is invalid.";
    }
}
