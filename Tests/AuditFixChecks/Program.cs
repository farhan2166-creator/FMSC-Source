using FMSC_SMS.Core;
using System.Data;

if (AppInstanceChecks.TryRunChild(args, out int instanceExit)) { Environment.ExitCode = instanceExit; return; }

int checks=0;
void Assert(bool condition,string message){if(!condition)throw new Exception(message);checks++;}
void Reject(Action action,string message){try{action();}catch(ArgumentException){checks++;return;}throw new Exception(message);}

AppInstanceChecks.Run(Assert);
if (args.Contains("--single-instance"))
{
    Console.WriteLine($"PASS: {checks} cross-process single-instance and crash recovery checks."); return;
}
CollectionCreditScopeChecks.Run(Assert);
if(args.Contains("--collection-credit"))
{
    Console.WriteLine($"PASS: {checks} collection advance ownership and allocation checks.");return;
}

foreach(decimal amount in new[]{0m,1m,999m,1000.00m,-100m,9999999999m})
{
    WholeRupees.Require(amount);
    Assert(WholeRupees.IsWhole(amount),"Whole rupees must be retained exactly");
}
foreach(decimal amount in new[]{.01m,.50m,.99m,999.50m,999.999m,-.50m})
{
    Reject(()=>WholeRupees.Require(amount),"Fraction must never be rounded");
    Reject(()=>WholeRupees.ValidateParameter("@Amount",amount),"Scalar fee guard");
    Reject(()=>WholeRupees.ValidateParameter("@discountamount",amount),"Case-insensitive scalar fee guard");
    Reject(()=>Tvp.AdmissionFeeDiscounts(new[]{(1,amount)}),"QR/admission discounts");
    Reject(()=>Tvp.StudentFeeDiscounts(new[]{(1,amount)}),"Assignment discounts");
    Reject(()=>Tvp.ReturnFeeChoices(new[]{(1,amount,0m)}),"Readmission rates");
    Reject(()=>Tvp.FeeConfigurationPlan(new[]{(1,1,1,1,amount,(string?)"Monthly",(string?)null)}),"Bulk/configuration rates");
    Reject(()=>Tvp.FeeAllocations(new[]{(1L,amount)}),"Collection allocations");
    Reject(()=>Tvp.FamilyAllocations(new[]{(1,1,(IEnumerable<(long,decimal)>)new[]{(1L,amount)})}),"Family collection allocations");
}
Assert((decimal)Tvp.FeeAllocations(new[]{(1L,-100m)}).Rows[0]["Amount"]==-100m,"Whole-rupee negative discount allocations remain valid");
Assert((decimal)WholeRupees.ValidateParameter("@ExpectedBalance",999.50m)! == 999.50m,"Existing fractional state can be inspected without rounding");
Assert(WholeRupees.ValidateParameter("@Amount",null) is null,"Optional monetary parameters");

var closingAmounts = new DataTable();
closingAmounts.Columns.Add("Payment Method");
foreach (string column in new[] { "Recorded Amount", "Actual Amount", "Difference" }) closingAmounts.Columns.Add(column, typeof(decimal));
closingAmounts.Rows.Add("Cash", 100m, 90m, -10m);
closingAmounts.Rows.Add("Refund", -10m, DBNull.Value, DBNull.Value);
closingAmounts.Rows.Add("Correction", 0m, DBNull.Value, DBNull.Value);
var printableClosing = DailyClosingSummary.CreatePrintable(closingAmounts);
var closingTotal = printableClosing.Rows[3];
Assert((decimal)closingTotal["Recorded Amount"] == 90m && (decimal)closingTotal["Actual Amount"] == 90m && (decimal)closingTotal["Difference"] == 0m, "Closing print tolerates blank adjustment actuals and retains recorded net semantics");
Assert(closingAmounts.Rows.Count == 3 && closingAmounts.Rows[1].IsNull("Actual Amount") && printableClosing.Rows[1].IsNull("Actual Amount"), "Printing leaves source and adjustment blanks unchanged");
closingAmounts.Rows[0]["Actual Amount"] = DBNull.Value;
var uncheckedClosing = DailyClosingSummary.CreatePrintable(closingAmounts).Rows[3];
Assert(uncheckedClosing.IsNull("Actual Amount") && uncheckedClosing.IsNull("Difference"), "An unchecked ordinary payment method does not print a completed actual total");
closingAmounts.Rows[0]["Actual Amount"] = 90.25m;
Assert((decimal)DailyClosingSummary.CreatePrintable(closingAmounts).Rows[3]["Actual Amount"] == 90.25m, "Historical closing amounts remain exact without silent rounding");

DataTable Fees(){var t=new DataTable();t.Columns.Add("FeeTypeId",typeof(int));t.Columns.Add("Amount",typeof(decimal));t.Columns.Add("Frequency",typeof(string));t.Columns.Add("IsMandatory",typeof(bool));t.Columns.Add("IsTransportLinked",typeof(bool));t.Rows.Add(1,2000m,"Monthly",true,false);t.Rows.Add(2,500m,"Yearly",false,false);return t;}
Assert(InputParse.TryWholeAmount("1,000",out decimal parsedWhole)&&parsedWhole==1000m,"Whole-number UI parsing accepts formatted thousands");
Assert(!InputParse.TryWholeAmount("Rs. 1000.00",out _),"Numeric UI input rejects currency text and decimal points");
Assert(!InputParse.TryWholeAmount("999.50",out _),"Fractional UI input is rejected without rounding");
Assert(!InputParse.TryWholeAmount("999.999",out _),"Sub-paisa input cannot round into an accepted fee");
var original=Fees();string quote=QuickRegistrationQuote.Create(original);
Assert(QuickRegistrationQuote.Create(original.Copy())==quote,"Unchanged rates accepted");
var reordered=original.Clone();reordered.ImportRow(original.Rows[1]);reordered.ImportRow(original.Rows[0]);
Assert(QuickRegistrationQuote.Create(reordered)==quote,"SQL row order is irrelevant");
foreach(var change in new (string,object)[]{("Amount",2500m),("Frequency","Yearly"),("IsMandatory",false),("IsTransportLinked",true)})
{var t=original.Copy();t.Rows[0][change.Item1]=change.Item2;Assert(QuickRegistrationQuote.Create(t)!=quote,"Rate/fee policy drift must invalidate quote");}
var added=original.Copy();added.Rows.Add(3,100m,"Monthly",false,false);Assert(QuickRegistrationQuote.Create(added)!=quote,"New fee invalidates quote");
var removed=original.Copy();removed.Rows.RemoveAt(1);Assert(QuickRegistrationQuote.Create(removed)!=quote,"Removed fee invalidates quote");
var missing=original.Copy();missing.Rows[0]["Amount"]=DBNull.Value;Assert(QuickRegistrationQuote.Create(missing)!=quote,"Missing configuration invalidates quote");
Console.WriteLine($"PASS: {checks} production money/TVP and quotation regression checks.");
Console.WriteLine($"PASS: {RemainingFindingChecks.Run()} role, literal search and working-area checks.");
Console.WriteLine($"PASS: {CompatibilityGuidanceChecks.Run()} database compatibility guidance checks.");
Console.WriteLine($"PASS: {ConnectionRecoveryChecks.Run()} connection recovery explanation and action checks.");
Console.WriteLine($"PASS: {await DurabilityChecks.RunAsync()} settings, photo input and payment journal durability checks.");
Console.WriteLine($"PASS: {DashboardChecks.Run()} dashboard count and academic-year checks.");
Console.WriteLine($"PASS: {await RegistrationPhotoChecks.RunAsync()} registration view and photo save/retry checks.");
Console.WriteLine($"PASS: {await AdmissionCompressionChecks.RunAsync()} admission confirmation and compact photo compression checks.");
Console.WriteLine($"PASS: {await FilePersistenceChecks.RunAsync()} logo preflight, report settings recovery and export durability checks.");
Console.WriteLine($"PASS: {AdministrativeAccessChecks.Run()} administrative drafts, access previews and transaction-outcome checks.");
Console.WriteLine($"PASS: {ThemeAppearanceChecks.Run()} lightweight palettes, saved preferences and refresh delivery checks.");


Console.WriteLine($"PASS: {QuickRegistrationRecoveryChecks.Run()} registration commit-outcome and durable recovery checks.");

if(args.Contains("--profile-search"))ReviewFilterProfile.Run();

Console.WriteLine($"PASS: {MutationRecoveryChecks.Run()} mutation commit-outcome and durable journal checks.");

Console.WriteLine($"PASS: {DpiLayoutChecks.Run()} synthetic DPI density and monitor work-area checks.");

var creditState = FMSC_SMS.Pages.Finance.StudentAccounts.PaymentAdjustmentState.Create("Completed", "Cash", 3000m, false, 0m, 0m, creditEntry: true);
Assert(creditState.Actions.Length == 0 && creditState.Remaining == 0, "Credit deposits cannot be adjusted as ordinary cash collections");
var releasedState = FMSC_SMS.Pages.Finance.StudentAccounts.PaymentAdjustmentState.Create("Completed", "Cash", 5000m, false, 0m, 0m, releasedToCredit: 1000m);
Assert(releasedState.Remaining == 4000m && !releasedState.Actions.Contains("Reversal"), "Paid money released to credit is excluded from the remaining adjustable receipt");
Console.WriteLine("PASS: 2 credit/receipt adjustment eligibility checks.");
