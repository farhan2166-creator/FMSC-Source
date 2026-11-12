# Owns a unique schema-only LocalDB instance. Accepts no live database/connection parameter.
param([switch]$KeepFixture,[switch]$AdmissionOnly,[switch]$TransferOnly,[switch]$PlacementOnly,[switch]$CertificateOnly)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$owner=[guid]::NewGuid().ToString('N')
$catalog='FMSC_BillingChecks_'+$owner
$instance='(localdb)\'+$catalog
$run=Join-Path $projectRoot ('artifacts/billing/'+$owner)
$databaseRoot=Join-Path $run 'database'
New-Item -ItemType Directory -Path $databaseRoot -Force | Out-Null
$created=$false
$checks=0
function Native($operation) {if($LASTEXITCODE -ne 0){throw "$operation failed ($LASTEXITCODE)."}}
function Query([string]$sql,[hashtable]$parameters=@{}) {
 if($catalog -notmatch '^FMSC_BillingChecks_[0-9a-f]{32}$' -or $instance -ne ('(localdb)\'+$catalog)){throw 'Unsafe fixture identity.'}
 $connection=[Data.SqlClient.SqlConnection]::new("Server=$instance;Database=$catalog;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=15")
 try {
  $connection.Open()
  $guard=$connection.CreateCommand()
  $guard.CommandText="SELECT CONVERT(nvarchar(32),value) FROM sys.extended_properties WHERE class=0 AND name=N'FMSC_BillingChecksOwner';"
  if([string]$guard.ExecuteScalar() -ne $owner){throw 'Disposable database ownership mismatch.'}
  $command=$connection.CreateCommand();$command.CommandTimeout=90;$command.CommandText=$sql
  foreach($key in $parameters.Keys){[void]$command.Parameters.AddWithValue($key,$parameters[$key])}
  $data=[Data.DataSet]::new();$adapter=[Data.SqlClient.SqlDataAdapter]::new($command);[void]$adapter.Fill($data)
  return ,$data
 } finally {$connection.Dispose()}
}
function Check([bool]$condition,[string]$message) {
 if(-not $condition){throw "FAIL: $message"};$script:checks++;Write-Output "PASS: $message"
}
function Number([string]$sql) { $result=Query $sql;return [decimal]$result.Tables[0].Rows[0][0] }
function Preview {return Query 'EXEC dbo.RunAutomaticMonthlyBilling @UserId=1;'}
function Post($review,[bool]$missed=$false,[bool]$reviewMissed=$false) {
 return Query 'EXEC dbo.RunAutomaticMonthlyBilling @UserId=1,@Preview=0,@ExpectedSnapshot=@Token,@IncludeMissed=@Missed,@ReviewMissed=@Review;' @{
  '@Token'=[string]$review.Tables[0].Rows[0].Snapshot;'@Missed'=$missed;'@Review'=$reviewMissed}
}
try {
 & SqlLocalDB create $catalog -s;Native 'Create isolated instance';$created=$true
 $safePath=$databaseRoot.Replace("'","''")
 & sqlcmd -S $instance -d master -E -b -Q "CREATE DATABASE [$catalog] ON PRIMARY(NAME=N'$catalog',FILENAME=N'$safePath\fixture.mdf') LOG ON(NAME=N'${catalog}_log',FILENAME=N'$safePath\fixture_log.ldf');"
 Native 'Create disposable catalog'
 & sqlcmd -S $instance -d $catalog -E -b -Q "EXEC sys.sp_addextendedproperty @name=N'FMSC_BillingChecksOwner',@value=N'$owner';"
 Native 'Mark fixture owner'
 $sqlPackage=Join-Path $env:USERPROFILE '.nuget/packages/microsoft.sqlpackage/170.5.96/tools/net8.0/any/sqlpackage.dll'
 if(-not(Test-Path -LiteralPath $sqlPackage)){throw 'Restore the pinned SQLPackage tool in Tests/LifecycleSqlChecks first.'}
 $fixture=Join-Path $projectRoot 'Tests/LifecycleSqlChecks/Baseline/schema.dacpac'
 & dotnet $sqlPackage '/Action:Publish' "/SourceFile:$fixture" "/TargetConnectionString:Server=$instance;Database=$catalog;Integrated Security=True;TrustServerCertificate=True" '/p:DropObjectsNotInSource=False' '/p:DropExtendedPropertiesNotInSource=False' '/p:ScriptDatabaseOptions=False' '/Quiet:True'
 Native 'Publish data-free schema'
 # DacFx round-trips this recovery identity as varchar. Repair only this owned schema-only fixture.
 # Migration changes run under the same transaction boundary used by deployment.
 $fixtureScripts=@(Get-Item (Join-Path $projectRoot 'Tests/LifecycleSqlChecks/Baseline/Initialize.sql')) + @(Get-ChildItem (Join-Path $projectRoot 'Migrations') -Filter '*.sql' | Sort-Object {[int]$_.BaseName})
 foreach($file in $fixtureScripts) {
  $content=Get-Content -LiteralPath $file.FullName -Raw
  $connection=[Data.SqlClient.SqlConnection]::new("Server=$instance;Database=$catalog;Integrated Security=True;TrustServerCertificate=True")
  $connection.Open();$transaction=$connection.BeginTransaction()
  try {
   $guard=$connection.CreateCommand();$guard.Transaction=$transaction
   $guard.CommandText="IF CONVERT(nvarchar(32),(SELECT value FROM sys.extended_properties WHERE class=0 AND name=N'FMSC_BillingChecksOwner'))<>N'$owner' THROW 51000,'Wrong fixture owner',1;"
   [void]$guard.ExecuteNonQuery()
   foreach($batch in [regex]::Split($content,'(?im)^\s*GO\s*\r?$')) {
    if([string]::IsNullOrWhiteSpace($batch)){continue}
    $command=$connection.CreateCommand();$command.Transaction=$transaction;$command.CommandTimeout=90;$command.CommandText=$batch
    [void]$command.ExecuteNonQuery()
   }
   $transaction.Commit()
  } catch {if($transaction.Connection){$transaction.Rollback()};throw} finally {$connection.Dispose()}
 }
 $seed=Get-Content -LiteralPath (Join-Path $projectRoot 'Validation/AutomaticBillingFixture.sql') -Raw
 [void](Query $seed)
 if($CertificateOnly){
  [void](Query "INSERT dbo.SchoolProfile(SchoolProfileId,SchoolName,CreatedAt,UpdatedAt,CreatedByUserId,UpdatedByUserId) VALUES(1,N'Fictional certificate school',SYSDATETIME(),SYSDATETIME(),1,1);")
  [void](Query "EXEC sys.sp_addextendedproperty @name=N'FMSC_CertificateChecksOwner',@value=N'$owner';")
  $env:FMSC_CERTIFICATE_TEST_CONNECTION="Server=$instance;Database=$catalog;Integrated Security=True;TrustServerCertificate=True"
  $env:FMSC_CERTIFICATE_TEST_OWNER=$owner
  try {
   & dotnet run --project (Join-Path $projectRoot 'Tests/CertificateReissueSqlChecks/CertificateReissueSqlChecks.csproj') -c Release
   Native 'Baseline-1 certificate snapshot/recovery checks'
  } finally { Remove-Item Env:FMSC_CERTIFICATE_TEST_CONNECTION,Env:FMSC_CERTIFICATE_TEST_OWNER -ErrorAction SilentlyContinue }
  return
 }
 if($PlacementOnly){
  . (Join-Path $projectRoot 'Validation/PlacementPlanChecks.ps1')
  Write-Output "PASS: $checks placement-plan SQL checks."
  return
 }
 if($TransferOnly){
  . (Join-Path $projectRoot 'Validation/TransferFeeScopeChecks.ps1')
  Write-Output "PASS: $checks same-year transfer fee SQL checks."
  return
 }
 if($AdmissionOnly){
  . (Join-Path $projectRoot 'Validation/AdmissionBillingStartChecks.ps1')
  Write-Output "PASS: $checks admission billing-start SQL checks."
  return
 }
 $review=Preview
 Check ($review.Tables[0].Rows[0].CurrentCharges -eq 2) 'Only ready current-month tuition and transport are eligible.'
 Check ($review.Tables[2].Rows.Count -ge 3) 'Legacy, missing-start and missing-placement records have visible exceptions.'
 [void](Post $review)
 Check ((Number 'SELECT COUNT(*) FROM dbo.BillingCharges;') -eq 2) 'Current-month billing creates obligations without payments.'
 Check ((Number 'SELECT COUNT(*) FROM dbo.FeePayments;') -eq 0) 'Billing creates no zero-value payment.'
 [void](Post (Preview))
 Check ((Number 'SELECT COUNT(*) FROM dbo.BillingCharges;') -eq 2) 'Repeat automatic run creates no duplicate charges.'
 [void](Query "UPDATE dbo.BillingCharges SET Status=N'Cancelled' WHERE FeeTypeId=2;")
 $review=Preview
 Check ($review.Tables[0].Rows[0].CurrentCharges -eq 0) 'Automatic billing never recreates a reversed charge.'
 $status=Query 'EXEC dbo.GetBillingCycleStatus @UserId=1,@AcademicYearId=1;'
 Check ($status.Tables[0].Rows[0].RemainingToBill -eq 1) 'Billing status detects a missing fee even when another fee exists.'
 [void](Query "DECLARE @S dbo.PostingSelectionType,@Today date=CONVERT(date,GETDATE()); INSERT @S SELECT 1,2,DATEFROMPARTS(YEAR(@Today),MONTH(@Today),1); EXEC dbo.PostSelectedMonthlyFees @UserId=1,@BillingMonth=@Today,@Selection=@S,@Reason=N'Reviewed manual repost',@AcademicYearId=1;")
 Check ((Number "SELECT COUNT(*) FROM dbo.BillingCharges WHERE Status<>N'Cancelled';") -eq 2) 'Explicit reviewed manual repost remains available.'
 [void](Query "UPDATE dbo.BillingAutomationState SET StartMonth=DATEADD(month,-2,StartMonth);")
 $review=Preview
 Check ($review.Tables[0].Rows[0].MissedCharges -eq 4) 'Recent missed months appear with exact charge counts.'
 [void](Post $review $false $true)
 Check ((Number "SELECT COUNT(*) FROM dbo.BillingCharges WHERE BillingMonth<DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1);") -eq 0) 'Current-month-only choice leaves missed months unbilled.'
 $review=Preview
 Check ($review.Tables[0].Rows[0].MissedCharges -eq 0) 'Reviewed gaps do not repeatedly prompt on every opening.'
 Check ($review.Tables[1].Rows.Count -eq 2) 'Reviewed gaps remain visible for later administrative recovery.'
 [void](Post $review $true $true)
 Check ((Number "SELECT COUNT(*) FROM dbo.BillingCharges WHERE BillingMonth<DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1);") -eq 4) 'Administrator can deliberately recover previously deferred months.'
 $review=Preview
 [void](Query "UPDATE dbo.StudentFeeAssignments SET Amount=Amount+1 WHERE EnrollmentId=1;")
 # Use a new current eligible record to make the token sensitive to its rate.
 [void](Query "UPDATE dbo.Students SET LegacySetupPending=0 WHERE AdmissionId=2; UPDATE dbo.Enrollments SET BillingStartDate=EnrollmentDate WHERE EnrollmentId=2;")
 $review=Preview
 [void](Query "UPDATE dbo.StudentFeeAssignments SET Amount=Amount+1 WHERE EnrollmentId=2;")
 $refused=$false;try{[void](Post $review)}catch{$refused=$_.Exception.Message -like '*Billing changed since review*'}
 Check $refused 'Stale preview is refused before posting.'
 Check ((Number 'SELECT COUNT(*) FROM dbo.BillingCharges WHERE EnrollmentId=2;') -eq 0) 'Stale preview failure leaves no partial charges.'
 Check ((Number "SELECT COUNT(*) FROM dbo.BillingCharges B JOIN dbo.Enrollments E ON E.EnrollmentId=B.EnrollmentId WHERE B.BillingMonth<DATEFROMPARTS(YEAR(E.BillingStartDate),MONTH(E.BillingStartDate),1);") -eq 0) 'No charge is created before explicit billing commencement.'
 [void](Query 'EXEC dbo.SaveStudentFeeDiscount @UserId=1,@StudentFeeAssignmentId=1,@DiscountAmount=8000;')
 Check ((Number 'SELECT DiscountAmount FROM dbo.StudentFeeAssignments WHERE StudentFeeAssignmentId=1;') -eq 0) 'Assignment discount leaves the old fee version unchanged.'
 Check ((Number "SELECT DiscountAmount FROM dbo.StudentFeeAssignments WHERE EnrollmentId=1 AND FeeTypeId=1 AND EffectiveFrom=DATEADD(month,1,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1));") -eq 8000) 'Assignment discount starts next month.'
 Check ((Number 'SELECT SUM(DiscountAmount) FROM dbo.BillingCharges;') -eq 0) 'Assignment discount does not rewrite issued bills.'
 [void](Query "DECLARE @Id int=(SELECT StudentFeeAssignmentId FROM dbo.StudentFeeAssignments WHERE EnrollmentId=1 AND FeeTypeId=1 AND EffectiveTo IS NULL),@Next date=DATEADD(month,1,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1)); EXEC dbo.ChangeStudentFeeAssignmentAmount @UserId=1,@StudentFeeAssignmentId=@Id,@NewAmount=5000,@EffectiveFrom=@Next,@Reason=N'Fictional reviewed fee reduction';")
 Check ((Number 'SELECT DiscountAmount FROM dbo.StudentFeeAssignments WHERE EnrollmentId=1 AND FeeTypeId=1 AND EffectiveTo IS NULL;') -eq 5000) 'A reduced fee automatically caps its discount to the new fee.'
 Check ((Number 'SELECT Amount-DiscountAmount FROM dbo.StudentFeeAssignments WHERE EnrollmentId=1 AND FeeTypeId=1 AND EffectiveTo IS NULL;') -eq 0) 'Capped discount makes payable zero without creating credit.'
 [void](Query "EXEC dbo.SaveStudentFeeDiscount @UserId=1,@StudentFeeAssignmentId=1,@DiscountAmount=NULL;")
 Check ((Number 'SELECT DiscountAmount FROM dbo.StudentFeeAssignments WHERE EnrollmentId=1 AND FeeTypeId=1 AND EffectiveTo IS NULL;') -eq 0) 'Blank assignment discount means no discount.'
 Check ((Number "SELECT COUNT(*) FROM dbo.fn_MonthlyBillingCandidates(DATEADD(month,10,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1)),1) WHERE BlockReason IS NULL;") -eq 0) 'Automatic eligibility stops at the academic-year boundary.'
 [void](Query "INSERT dbo.SchoolProfile(SchoolProfileId,SchoolName,CreatedByUserId,UpdatedByUserId,FeeProrationCutoffDay) VALUES(1,N'Fictional School',1,1,16); UPDATE dbo.Enrollments SET LeavingDate=DATEADD(day,14,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1)) WHERE EnrollmentId=1;")
 Check ((Number "SELECT COUNT(*) FROM dbo.fn_MonthlyBillingCandidates(DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1),1) WHERE EnrollmentId=1 AND BlockReason IS NULL;") -eq 0) 'Leaving before cutoff excludes that month from monthly eligibility.'
 [void](Query "UPDATE dbo.Enrollments SET LeavingDate=DATEADD(day,15,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1)) WHERE EnrollmentId=1;")
 Check ((Number "SELECT COUNT(*) FROM dbo.fn_MonthlyBillingCandidates(DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1),1) WHERE EnrollmentId=1 AND BlockReason IS NULL;") -eq 2) 'Leaving on the cutoff retains that month in monthly eligibility.'
 [void](Query 'UPDATE dbo.Enrollments SET LeavingDate=NULL WHERE EnrollmentId=1;')
 $deposit=Query "DECLARE @Request uniqueidentifier='11111111-1111-1111-1111-111111111111'; EXEC dbo.ProcessStudentCredit @UserId=1,@AdmissionId=1,@Action=N'Deposit',@Amount=5000,@RequestId=@Request,@Reason=N'Fictional advance payment';"
 Check ((Number 'SELECT SUM(Amount) FROM dbo.StudentCreditEntries WHERE AdmissionId=1;') -eq 5000) 'Advance money is retained as available student credit.'
 [void](Query "DECLARE @Request uniqueidentifier='11111111-1111-1111-1111-111111111111'; EXEC dbo.ProcessStudentCredit @UserId=1,@AdmissionId=1,@Action=N'Deposit',@Amount=5000,@RequestId=@Request,@Reason=N'Fictional advance payment';")
 Check ((Number "SELECT COUNT(*) FROM dbo.StudentCreditEntries WHERE EntryKind=N'Deposit';") -eq 1) 'Exact credit-deposit retry does not receive money twice.'
 # Both automatic posting and its retained compatibility writer must preserve advance.
 [void](Query 'BEGIN TRAN; EXEC dbo.ApplyStudentCredit_Internal @AdmissionId=1,@UserId=1; COMMIT;')
 Check ((Number 'SELECT SUM(Amount) FROM dbo.StudentCreditEntries WHERE AdmissionId=1;') -eq 5000) 'Automatic fee posting leaves the student advance untouched.'
 Check ((Number 'SELECT COUNT(*) FROM dbo.FeePayments WHERE IsCreditApplication=1;') -eq 0) 'The posting compatibility writer creates no credit payments.'
 # This is an explicit fictional collection, not a billing-triggered settlement.
 # Credit allocation and its saved receipt share the same transaction.
 [void](Query @'
DECLARE @Bill bigint=(SELECT TOP(1) B.BillingChargeId FROM dbo.BillingCharges B
 WHERE B.EnrollmentId=1 AND B.FeeTypeId=1 AND
 B.BillingMonth=DATEADD(month,-2,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1)) AND B.Status<>N'Cancelled');
DECLARE @Before decimal(12,2)=dbo.GetFeeChargeBalance(@Bill);
DECLARE @TransactionDate date=CONVERT(date,GETDATE());
DECLARE @CreditJson nvarchar(max)=(SELECT @Bill ChargeId,1 AdmissionId,1 EnrollmentId,
 CONVERT(decimal(12,2),5000) Amount,@Before ExpectedBalance,
 CONVERT(decimal(12,2),5000) ExpectedCredit FOR JSON PATH);
BEGIN TRAN;
EXEC dbo.ApplyCollectionCredit @UserId=1,@TransactionDate=@TransactionDate,@CreditsJson=@CreditJson;
DECLARE @PaymentId int=(SELECT TOP(1) FeePaymentId FROM dbo.FeePayments
 WHERE AdmissionId=1 AND IsCreditApplication=1 ORDER BY FeePaymentId DESC);
DECLARE @Lines nvarchar(max)=(SELECT @Bill ChargeId,1 AdmissionId,1 EnrollmentId,
 @Before AmountBefore,CONVERT(decimal(12,2),0) Discount,
 CONVERT(decimal(12,2),0) Cash,CONVERT(decimal(12,2),5000) Credit FOR JSON PATH);
DECLARE @Payments nvarchar(max)=CONCAT('[',@PaymentId,']');
EXEC dbo.IssueCollectionReceipt @UserId=1,@PrimaryAdmissionId=1,
 @TransactionDate=@TransactionDate,@PaymentMethod=N'Account Credit',
 @Remarks=N'Explicit fictional credit settlement',@StudentIdsJson=N'[1]',
 @PaymentIdsJson=@Payments,@LinesJson=@Lines,
 @CashReceived=0,@CreditApplied=5000,@DiscountApplied=0;
COMMIT;
'@)
 Check ((Number 'SELECT SUM(Amount) FROM dbo.StudentCreditEntries WHERE AdmissionId=1;') -eq 0) 'Explicit collection alone consumes the selected student advance.'
 Check ((Number 'SELECT SUM(A.Amount) FROM dbo.FeePaymentAllocations A JOIN dbo.FeePayments P ON P.FeePaymentId=A.FeePaymentId WHERE P.IsCreditApplication=1;') -eq 5000) 'Collected credit uses the authoritative payment allocations.'
 Check ((Number 'SELECT COUNT(*) FROM dbo.CollectionReceiptPayments L JOIN dbo.FeePayments P ON P.FeePaymentId=L.FeePaymentId WHERE P.IsCreditApplication=1;') -eq 1) 'Explicit credit collection saves one durable receipt link.'
 Check ((Number "SELECT dbo.GetFeeChargeBalance(BillingChargeId) FROM dbo.BillingCharges WHERE EnrollmentId=1 AND FeeTypeId=1 AND BillingMonth=DATEADD(month,-2,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1)) AND Status<>N'Cancelled';") -eq 5000) 'Explicit credit collection settles the selected tuition bill.'
 $dashboard=Query 'EXEC dbo.GetDashboardSummary @UserId=1;'
 Check ($dashboard.Tables[0].Rows[0].FeeCollectedToday -eq 5000) 'Applying credit is not counted as new money on the dashboard.'
 Check ((Number 'SELECT SUM(TotalAmount) FROM dbo.DailyClosingPayments;') -eq 5000) 'Daily closing excludes noncash credit applications.'
 [void](Query "DECLARE @R uniqueidentifier=NEWID(); EXEC dbo.ProcessStudentCredit @UserId=1,@AdmissionId=1,@Action=N'Deposit',@Amount=3000,@RequestId=@R,@Reason=N'Fictional additional credit';")
 [void](Query "DECLARE @R uniqueidentifier=NEWID(); EXEC dbo.ProcessStudentCredit @UserId=1,@AdmissionId=1,@Action=N'Refund',@Amount=1000,@RequestId=@R,@Reason=N'Fictional unused credit refund';")
 Check ((Number 'SELECT SUM(Amount) FROM dbo.StudentCreditEntries WHERE AdmissionId=1;') -eq 2000) 'Refunding unused credit reduces its available amount.'
 [void](Query "INSERT dbo.Families(FamilyCode) VALUES(N'FICTIONAL'); INSERT dbo.FamilyMembers(FamilyId,AdmissionId,RelationshipType) VALUES(1,1,N'Sibling'),(1,2,N'Sibling'); DECLARE @R uniqueidentifier=NEWID(); EXEC dbo.ProcessStudentCredit @UserId=1,@AdmissionId=1,@Action=N'Transfer',@Amount=1000,@RequestId=@R,@TargetAdmissionId=2,@Reason=N'Fictional sibling transfer';")
 Check ((Number 'SELECT SUM(Amount) FROM dbo.StudentCreditEntries WHERE AdmissionId=1;') -eq 1000) 'Sibling transfer debits only the chosen amount.'
 Check ((Number 'SELECT SUM(Amount) FROM dbo.StudentCreditEntries WHERE AdmissionId=2;') -eq 1000) 'Sibling transfer credits the selected sibling.'
 Check ((Number "SELECT SUM(TotalAmount) FROM dbo.FeePayments WHERE IsCreditApplication=0 AND Status=N'Completed';") -eq 7000) 'Sibling transfer and credit application create no additional cash.'
 $refused=$false;try{[void](Query "DECLARE @R uniqueidentifier=NEWID(); EXEC dbo.ProcessStudentCredit @UserId=1,@AdmissionId=1,@Action=N'Refund',@Amount=5000,@RequestId=@R,@Reason=N'Fictional excessive refund';")}catch{$refused=$_.Exception.Message -like '*exceeds available*'}
 Check $refused 'Credit cannot be refunded below zero.'
 [void](Query "DECLARE @Bill bigint=(SELECT BillingChargeId FROM dbo.BillingCharges WHERE EnrollmentId=1 AND FeeTypeId=1 AND BillingMonth=DATEADD(month,-2,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1))),@Request uniqueidentifier='22222222-2222-2222-2222-222222222222'; EXEC dbo.AdjustBillingChargeRate @UserId=1,@BillingChargeId=@Bill,@NewAmount=12000,@PaidReductionAction=N'Credit',@Reason=N'Fictional rate increase',@RequestId=@Request;")
 Check ((Number 'SELECT SUM(GrossAmount) FROM dbo.BillingCharges WHERE CorrectionOfBillingChargeId IS NOT NULL;') -eq 2000) 'A paid/partial bill rate increase creates only the additional amount owed.'
 [void](Query "DECLARE @Bill bigint=(SELECT BillingChargeId FROM dbo.BillingCharges WHERE EnrollmentId=1 AND FeeTypeId=1 AND ChargeKind=N'Monthly' AND BillingMonth=DATEADD(month,-2,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1))),@Request uniqueidentifier='22222222-2222-2222-2222-222222222222'; EXEC dbo.AdjustBillingChargeRate @UserId=1,@BillingChargeId=@Bill,@NewAmount=12000,@PaidReductionAction=N'Credit',@Reason=N'Fictional rate increase',@RequestId=@Request;")
 Check ((Number 'SELECT COUNT(*) FROM dbo.FeeBillCorrections;') -eq 1) 'Retrying the same bill correction creates no duplicate difference.'
 $creditBeforeReduction=Number 'SELECT SUM(Amount) FROM dbo.StudentCreditEntries WHERE AdmissionId=1;'
 [void](Query "DECLARE @Bill bigint=(SELECT BillingChargeId FROM dbo.BillingCharges WHERE EnrollmentId=1 AND FeeTypeId=1 AND ChargeKind=N'Monthly' AND BillingMonth=DATEADD(month,-2,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1))),@R uniqueidentifier=NEWID(); EXEC dbo.AdjustBillingChargeRate @UserId=1,@BillingChargeId=@Bill,@NewAmount=8000,@PaidReductionAction=N'Credit',@Reason=N'Fictional unpaid reduction',@RequestId=@R;")
 Check ((Number 'SELECT SUM(Amount) FROM dbo.StudentCreditEntries WHERE AdmissionId=1;') -eq $creditBeforeReduction) 'A reduction covered by unpaid fees does not manufacture credit.'
 [void](Query "DECLARE @Bill bigint=(SELECT BillingChargeId FROM dbo.BillingCharges WHERE EnrollmentId=1 AND FeeTypeId=1 AND ChargeKind=N'Monthly' AND BillingMonth=DATEADD(month,-2,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1))),@R uniqueidentifier=NEWID(); EXEC dbo.AdjustBillingChargeRate @UserId=1,@BillingChargeId=@Bill,@NewAmount=2000,@PaidReductionAction=N'Credit',@Reason=N'Fictional paid reduction to credit',@RequestId=@R;")
 Check ((Number 'SELECT SUM(Amount) FROM dbo.StudentCreditEntries WHERE AdmissionId=1;') -eq 4000) 'Only the released paid difference becomes credit.'
 Check ((Number "SELECT SUM(TotalAmount) FROM dbo.FeePayments WHERE IsCreditApplication=0 AND Status=N'Completed';") -eq 7000) 'Converting a paid reduction to credit does not change cash received.'
 [void](Query "DECLARE @Bill bigint=(SELECT BillingChargeId FROM dbo.BillingCharges WHERE EnrollmentId=1 AND FeeTypeId=1 AND ChargeKind=N'Monthly' AND BillingMonth=DATEADD(month,-2,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1))),@R uniqueidentifier=NEWID(); EXEC dbo.AdjustBillingChargeRate @UserId=1,@BillingChargeId=@Bill,@NewAmount=1000,@PaidReductionAction=N'Refund',@Reason=N'Fictional paid reduction cash refund',@RequestId=@R;")
 Check ((Number "SELECT SUM(TotalAmount) FROM dbo.FeePayments WHERE IsCreditApplication=0 AND Status=N'Completed';") -eq 6000) 'Cash refund of a paid rate reduction reduces cash by the exact difference.'
 Check ((Number 'SELECT SUM(Amount) FROM dbo.StudentCreditEntries WHERE AdmissionId=1;') -eq 4000) 'A cash-refunded difference is not also credited.'
 [void](Query "DECLARE @Bill bigint=(SELECT BillingChargeId FROM dbo.BillingCharges WHERE EnrollmentId=1 AND FeeTypeId=1 AND ChargeKind=N'Monthly' AND BillingMonth=DATEADD(month,-1,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1))),@Today date=CONVERT(date,GETDATE()),@A dbo.FeePaymentAllocationType; INSERT @A VALUES(@Bill,4000); EXEC dbo.ProcessFeeTransaction @AdmissionId=1,@EnrollmentId=1,@TransactionType=N'Collection',@Amount=4000,@TransactionDate=@Today,@PaymentMethod=N'Cash',@ReferenceNumber=NULL,@Remarks=N'Fictional partial payment',@FeePaymentId=NULL,@BillingChargeId=NULL,@Allocations=@A,@UserId=1;")
 $beforePayments=Number 'SELECT COUNT(*) FROM dbo.FeePayments;'
 [void](Query "DECLARE @Bill bigint=(SELECT BillingChargeId FROM dbo.BillingCharges WHERE EnrollmentId=1 AND FeeTypeId=1 AND ChargeKind=N'Monthly' AND BillingMonth=DATEADD(month,-1,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1))),@Today date=CONVERT(date,GETDATE()),@A dbo.FeePaymentAllocationType; INSERT @A VALUES(@Bill,-6000); EXEC dbo.ProcessFeeTransaction @AdmissionId=1,@EnrollmentId=1,@TransactionType=N'Collection',@Amount=0,@TransactionDate=@Today,@PaymentMethod=N'Cash',@ReferenceNumber=NULL,@Remarks=N'Fictional remaining balance concession',@FeePaymentId=NULL,@BillingChargeId=NULL,@Allocations=@A,@UserId=1;")
 Check ((Number 'SELECT COUNT(*) FROM dbo.FeePayments;') -eq $beforePayments) 'Waiving the remaining partial balance creates neither a fake payment nor a refund.'
 Check ((Number "SELECT dbo.GetFeeChargeBalance(BillingChargeId) FROM dbo.BillingCharges WHERE EnrollmentId=1 AND FeeTypeId=1 AND ChargeKind=N'Monthly' AND BillingMonth=DATEADD(month,-1,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1));") -eq 0) 'A partial-bill concession settles only its remaining unpaid amount.'
 [void](Query "DECLARE @Month date=DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1),@S dbo.PostingSelectionType; INSERT @S VALUES(2,1,@Month); EXEC dbo.PostSelectedMonthlyFees @UserId=1,@BillingMonth=@Month,@Selection=@S,@AcademicYearId=1;")
 $creditBeforeSurplus=Number 'SELECT SUM(Amount) FROM dbo.StudentCreditEntries WHERE AdmissionId=2;'
 $surplus=Query "DECLARE @Bill bigint=(SELECT BillingChargeId FROM dbo.BillingCharges WHERE EnrollmentId=2 AND FeeTypeId=1 AND ChargeKind=N'Monthly'),@Today date=CONVERT(date,GETDATE()),@A dbo.FamilyFeeAllocationType,@R uniqueidentifier=NEWID(); INSERT @A VALUES(2,2,@Bill,9001); EXEC dbo.ProcessFamilyFeeTransaction @TransactionType=N'Collection',@TransactionDate=@Today,@PaymentMethod=N'Cash',@ReferenceNumber=NULL,@Remarks=N'Fictional excess payment',@Allocations=@A,@UserId=1,@ReceivedAmount=11001,@CreditAdmissionId=2,@CreditRequestId=@R;"
 Check ((Number 'SELECT SUM(Amount) FROM dbo.StudentCreditEntries WHERE AdmissionId=2;') -eq ($creditBeforeSurplus+2000)) 'Excess collection is retained as student credit under the same transaction.'
 $receipt=[string]$surplus.Tables[0].Rows[0].ReceiptNumber
 $lines=Query 'EXEC dbo.GetFeeReceiptLines @UserId=1,@ReceiptNumber=@Receipt;' @{'@Receipt'=$receipt}
 Check (([decimal]($lines.Tables[0].Rows | Measure-Object -Property Amount -Sum).Sum) -eq 11001) 'Receipt lines reconcile the settled fees plus future credit with money received.'
 $receiptHeader=Query 'EXEC dbo.GetFeeReceiptGroup @UserId=1,@ReceiptNumber=@Receipt;' @{'@Receipt'=$receipt}
 Check ($receiptHeader.Tables[0].Rows[0].IssuedAmount -eq 11001) 'Excess payment shares one accurate collection receipt.'
 [void](Query "DECLARE @Id int=(SELECT StudentFeeAssignmentId FROM dbo.StudentFeeAssignments WHERE EnrollmentId=2 AND FeeTypeId=1 AND EffectiveTo IS NULL); EXEC dbo.SaveStudentFeeDiscount @UserId=1,@StudentFeeAssignmentId=@Id,@DiscountAmount=10001,@ConcessionLabel=N'Free';")
 Check ((Number "SELECT COUNT(*) FROM dbo.StudentFeeAssignments WHERE EnrollmentId=2 AND EffectiveFrom=DATEADD(month,1,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1)) AND ConcessionLabel=N'Free' AND Amount=DiscountAmount;") -eq 1) 'Free concession is an effective-dated next-month assignment, not a blanket student exclusion.'
 Check ((Number "SELECT COUNT(*) FROM dbo.StudentFeeAssignments WHERE EnrollmentId=2 AND (EffectiveFrom IS NULL OR EffectiveFrom<DATEADD(month,1,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1))) AND ConcessionLabel IS NOT NULL;") -eq 0) 'New concession does not relabel historical fees.'
 [void](Query "INSERT dbo.FeeTypes(FeeTypeCode,FeeTypeName,DefaultFrequency) VALUES(N'ANNUAL_TEST',N'Fictional annual fee',N'Yearly'); DECLARE @F int=SCOPE_IDENTITY(),@Month date=DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1); INSERT dbo.StudentFeeAssignments(EnrollmentId,AdmissionId,FeeTypeId,Amount,DiscountAmount,Frequency) VALUES(1,1,@F,500,0,N'Yearly'); DECLARE @A int=SCOPE_IDENTITY(); INSERT dbo.BillingCharges(StudentFeeAssignmentId,EnrollmentId,AdmissionId,FeeTypeId,BillingMonth,ChargeDate,DueDate,GrossAmount,DiscountAmount,ChargeKind) VALUES(@A,1,1,@F,@Month,@Month,@Month,500,0,N'Annual');")
 [void](Query "UPDATE dbo.SchoolProfile SET FeeProrationCutoffDay=2;")
 $leave=Query "DECLARE @Day date=DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1); EXEC dbo.CloseStudentAdmission @UserId=1,@AdmissionId=1,@LeavingDate=@Day,@Reason=N'Fictional leaving before cutoff',@Preview=1,@PaidReductionAction=N'Credit';"
 [void](Query "DECLARE @Day date=DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1); EXEC dbo.CloseStudentAdmission @UserId=1,@AdmissionId=1,@LeavingDate=@Day,@Reason=N'Fictional leaving before cutoff',@Preview=0,@ExpectedSnapshot=@Token,@PaidReductionAction=N'Credit';" @{'@Token'=[string]$leave.Tables[0].Rows[0].Snapshot})
 Check ((Number "SELECT SUM(dbo.GetFeeChargeBalance(BillingChargeId)) FROM dbo.BillingCharges WHERE EnrollmentId=1 AND BillingMonth=DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1) AND ChargeKind=N'Monthly' AND Status<>N'Cancelled';") -eq 0) 'Official leaving before cutoff removes the current monthly liability.'
 Check ((Number "SELECT SUM(dbo.GetFeeChargeBalance(BillingChargeId)) FROM dbo.BillingCharges WHERE EnrollmentId=1 AND ChargeKind=N'Annual';") -eq 500) 'Leaving cutoff preserves non-monthly charges.'
 Check ((Number 'SELECT SUM(Amount) FROM dbo.StudentCreditEntries WHERE AdmissionId=1;') -eq 4000) 'Leaving preserves unused student credit.'
 [void](Query "UPDATE dbo.SchoolProfile SET FeeProrationCutoffDay=1;")
 $leave=Query "DECLARE @Day date=DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1); EXEC dbo.CloseStudentAdmission @UserId=1,@AdmissionId=2,@LeavingDate=@Day,@Reason=N'Fictional leaving on cutoff',@Preview=1;"
 [void](Query "DECLARE @Day date=DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1); EXEC dbo.CloseStudentAdmission @UserId=1,@AdmissionId=2,@LeavingDate=@Day,@Reason=N'Fictional leaving on cutoff',@Preview=0,@ExpectedSnapshot=@Token;" @{'@Token'=[string]$leave.Tables[0].Rows[0].Snapshot})
 Check ((Number "SELECT SUM(NetAmount) FROM dbo.BillingCharges WHERE EnrollmentId=2 AND ChargeKind=N'Monthly';") -eq 10001) 'Leaving on cutoff retains the current monthly charge.'
 [void](Query "UPDATE dbo.SchoolProfile SET FeeProrationCutoffDay=16;")
 [void](Query "DECLARE @Start date=DATEADD(month,-2,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1)); INSERT dbo.Students(AdmissionNumber,RegistrationNumber,AdmissionDate,AdmissionCampusId,StudentName,Status,LegacySetupPending) VALUES('1005',N'TEST-1005',@Start,1,N'Free tuition example',N'Active',0); INSERT dbo.Enrollments(AdmissionId,AcademicYearId,ClassSectionId,EnrollmentDate,BillingStartDate,Status,BillingReady,PlacementConfirmedAt) VALUES(5,1,1,@Start,@Start,N'Active',1,SYSDATETIME()); DECLARE @E int=SCOPE_IDENTITY(); INSERT dbo.StudentFeeAssignments(EnrollmentId,AdmissionId,FeeTypeId,Amount,DiscountAmount,Frequency,ConcessionLabel) VALUES(@E,5,1,10000,10000,N'Monthly',N'Free'),(@E,5,2,2000,0,N'Monthly',NULL); INSERT dbo.FamilyMembers(FamilyId,AdmissionId,RelationshipType) VALUES(1,5,N'Sibling'); DECLARE @Month date=DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1),@S dbo.PostingSelectionType; INSERT @S VALUES(@E,1,@Month),(@E,2,@Month); EXEC dbo.PostSelectedMonthlyFees @UserId=1,@BillingMonth=@Month,@Selection=@S,@AcademicYearId=1;")
 Check ((Number "SELECT SUM(dbo.GetFeeChargeBalance(BillingChargeId)) FROM dbo.BillingCharges WHERE AdmissionId=5;") -eq 2000) 'Free tuition does not exempt an assigned transport fee.'
 Check ((Number 'SELECT COUNT(*) FROM dbo.BillingCharges WHERE AdmissionId=5 AND FeeTypeId=1;') -eq 0) 'Fully free monthly fees are excluded from charge generation.'
 $concessions=Query "EXEC dbo.GetBillingAndDiscountsReport @UserId=1,@View=N'Concession Assignments';"
 Check (($concessions.Tables[0].Rows | Where-Object {$_.'Admission Number' -eq '1005' -and $_.Concession -eq 'Free'}).Count -eq 1) 'A free assignment is reportable without manufacturing a zero-value bill.'
 Check ($concessions.Tables.Count -eq 2 -and $concessions.Tables[1].Rows[0]['Concession Assignments'] -gt 0) 'Concession reporting supplies an assignment count instead of misleading billed totals.'
 $familySource=Get-Content -LiteralPath (Join-Path $projectRoot 'Core/Reports/Fees/FeeBillPreview.cs') -Raw
 $familySql=[regex]::Match($familySource,'private const string ChargesSql = """\r?\n([\s\S]*?)\r?\n\s*""";').Groups[1].Value
 if(-not $familySql){throw 'Family bill SQL could not be located.'}
 $familyBill=Query $familySql @{'@UserId'=1;'@Admission'=1;'@Ids'='[1,5]';'@WholeFamily'=$false}
 # Free is an assignment concession, not part of the stored fee-type name.
 # The visit SQL represents this without a posted charge by a negative assignment ID.
 Check (($familyBill.Tables[0].Rows | Where-Object {$_.Reference -eq '1005' -and $_.BillingChargeId -lt 0 -and $_.DiscountAmount -eq 10000 -and $_.Due -eq 0}).Count -eq 1) 'The production family bill shows the free child as a concession line without a zero-value charge.'

 $family=Query "DECLARE @A dbo.FamilyFeeAllocationType,@Today date=CONVERT(date,GETDATE()); INSERT @A SELECT 1,1,BillingChargeId,500 FROM dbo.BillingCharges WHERE EnrollmentId=1 AND ChargeKind=N'Annual'; EXEC dbo.ProcessFamilyFeeTransaction @TransactionType=N'Collection',@TransactionDate=@Today,@PaymentMethod=N'Cash',@ReferenceNumber=NULL,@Remarks=N'Fictional family including free child',@Allocations=@A,@UserId=1,@IncludedStudentsJson=N'[1,5]';"
 $receipt=[string]$family.Tables[0].Rows[0].ReceiptNumber
 $lines=Query 'EXEC dbo.GetFeeReceiptLines @UserId=1,@ReceiptNumber=@Receipt;' @{'@Receipt'=$receipt}
 Check (($lines.Tables[0].Select("AdmissionId=5 AND Amount=0")).Count -eq 1) 'An included free child is shown as a zero-payable receipt line without a fake payment.'
 $header=Query 'EXEC dbo.GetFeeReceiptGroup @UserId=1,@ReceiptNumber=@Receipt;' @{'@Receipt'=$receipt}
 Check ($header.Tables[0].Rows[0].StudentCount -eq 2) 'Family receipt count includes the explicitly selected free child.'
 $accountantSql=@'
IF (SELECT COUNT(*) FROM dbo.Roles WHERE RoleName=N'Accountant')<>1
 OR NOT EXISTS(SELECT 1 FROM dbo.Permissions WHERE PermissionCode=N'Fees.Collect')
 OR NOT EXISTS(SELECT 1 FROM dbo.Permissions WHERE PermissionCode=N'Fees.View')
    THROW 51017,'Billing fixture requires schema-16 reference roles and permissions.',1;
INSERT dbo.Users(Username,RoleId)
SELECT N'fictional_accountant',RoleId FROM dbo.Roles WHERE RoleName=N'Accountant';
IF SCOPE_IDENTITY()<>2
    THROW 51018,'The accountant fixture must be synthetic user 2.',1;
INSERT dbo.UserRoles(UserId,RoleId)
SELECT 2,RoleId FROM dbo.Roles WHERE RoleName=N'Accountant';
INSERT dbo.RolePermissions(RoleId,PermissionId)
SELECT R.RoleId,P.PermissionId FROM dbo.Roles R CROSS JOIN dbo.Permissions P
WHERE R.RoleName=N'Accountant'
  AND P.PermissionCode IN(N'Fees.Collect',N'Fees.View')
  AND NOT EXISTS(SELECT 1 FROM dbo.RolePermissions RP
                 WHERE RP.RoleId=R.RoleId AND RP.PermissionId=P.PermissionId);
'@
 [void](Query $accountantSql)
 $creditBeforeAccountant=Number 'SELECT SUM(Amount) FROM dbo.StudentCreditEntries WHERE AdmissionId=2;'
 [void](Query "DECLARE @R uniqueidentifier=NEWID(); EXEC dbo.ProcessStudentCredit @UserId=2,@AdmissionId=2,@Action=N'Refund',@Amount=500,@RequestId=@R,@Reason=N'Fictional accountant credit refund';")
 Check ((Number 'SELECT SUM(Amount) FROM dbo.StudentCreditEntries WHERE AdmissionId=2;') -eq ($creditBeforeAccountant-500)) 'Accountant can refund unused credit with existing collection authority.'
 [void](Query "DECLARE @R uniqueidentifier=NEWID(); EXEC dbo.ProcessStudentCredit @UserId=2,@AdmissionId=2,@Action=N'Transfer',@Amount=500,@RequestId=@R,@TargetAdmissionId=1,@Reason=N'Fictional accountant sibling transfer';")
 Check ((Number 'SELECT SUM(Amount) FROM dbo.StudentCreditEntries WHERE AdmissionId=2;') -eq ($creditBeforeAccountant-1000)) 'Accountant can transfer credit to a sibling.'
 $refused=$false;try{[void](Query 'EXEC dbo.RunAutomaticMonthlyBilling @UserId=2,@IncludeMissed=1;')}catch{$refused=$_.Exception.Message -like '*not authorized*'}
 Check $refused 'Accountant cannot authorize missed-month recovery without administrative permission.'
 [void](Query "UPDATE dbo.FeeTypes SET IsMandatory=1 WHERE FeeTypeId IN(1,3); UPDATE dbo.FeeTypes SET IsTransportLinked=1 WHERE FeeTypeId=2; INSERT dbo.FeeConfigurations(CampusId,ClassId,FeeTypeId,Amount,Frequency) VALUES(1,1,1,10000,N'Monthly'),(1,1,2,2000,N'Monthly'),(1,1,3,500,N'Yearly'); UPDATE dbo.AdmissionNumberCounter SET LastNumber=2000 WHERE CounterId=1;")
 function Admit([int]$day,[bool]$quick=$false,[string]$concession="") {
  return Query "DECLARE @Month date=DATEADD(month,-1,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1)),@Date date,@Birth date,@Ids dbo.FeeTypeIdList,@D dbo.AdmissionFeeDiscountType; SET @Date=DATEADD(day,@Day-1,@Month); SET @Birth=DATEADD(year,-12,@Date); INSERT @D VALUES(1,2000); EXEC dbo.AdmitStudent @UserId=1,@AdmissionDate=@Date,@AdmissionCampusId=1,@AcademicYearId=1,@ClassSectionId=1,@StudentName=@Name,@FatherName=N'Fictional Parent',@DateOfBirth=@Birth,@PlaceOfBirth=N'Fictional Town',@Gender=N'Male',@BFormNumber=NULL,@Religion=NULL,@BloodGroup=NULL,@ContactNumber=NULL,@AlternateContactNumber=NULL,@Address=N'Fictional Street',@PreviousSchool=NULL,@FatherOccupation=NULL,@Disability=N'No',@UsesTransport=0,@SelectedFeeTypeIds=@Ids,@AdmissionDiscounts=@D,@QuickRegistration=@Quick,@FeeConcessionsJson=@ConcessionJson;" @{'@Day'=$day;'@Quick'=$quick;'@Name'=('Admission '+$day+' '+$quick);'@ConcessionJson'=$(if($concession){'[{"FeeTypeId":1,"ConcessionLabel":"'+$concession+'"}]'}else{'[]'})}
 }
 $beforeCutoff=Admit 15
 $earlyId=[int]$beforeCutoff.Tables[0].Rows[0].AdmissionId
 $earlyEnrollment=[int]$beforeCutoff.Tables[0].Rows[0].EnrollmentId
 Check ((Number "SELECT SUM(NetAmount) FROM dbo.BillingCharges WHERE AdmissionId=$earlyId AND ChargeKind=N'Monthly' AND BillingMonth=DATEFROMPARTS(YEAR(dbo.GetMonthlyBillingStartDate(CONVERT(date,GETDATE()))),MONTH(dbo.GetMonthlyBillingStartDate(CONVERT(date,GETDATE()))),1);") -eq 8000) 'Backdated admission starts tuition from today under the school cutoff and the applicable concession.'
 Check ((Number "SELECT DiscountAmount FROM dbo.StudentFeeAssignments WHERE AdmissionId=$earlyId AND FeeTypeId=1 AND EffectiveTo IS NULL;") -eq 2000) 'Admission records its student discount for next month.'
 $afterCutoff=Admit 16
 $lateId=[int]$afterCutoff.Tables[0].Rows[0].AdmissionId
 $lateEnrollment=[int]$afterCutoff.Tables[0].Rows[0].EnrollmentId
 Check ((Number "SELECT COUNT(*) FROM dbo.BillingCharges WHERE AdmissionId=$lateId AND ChargeKind=N'Monthly' AND BillingMonth=DATEFROMPARTS(YEAR(dbo.GetMonthlyBillingStartDate(CONVERT(date,GETDATE()))),MONTH(dbo.GetMonthlyBillingStartDate(CONVERT(date,GETDATE()))),1);") -eq 1) 'The earlier admission day does not change current-date monthly billing.'
 Check ((Number "SELECT SUM(NetAmount) FROM dbo.BillingCharges WHERE AdmissionId=$lateId AND ChargeKind=N'Annual' AND BillingMonth=DATEADD(month,-1,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1));") -eq 500) 'Admission annual fee remains immediately chargeable despite delayed monthly tuition.'
 $qr=Admit 16 $true
 $qrId=[int]$qr.Tables[0].Rows[0].AdmissionId
 Check ((Number "SELECT COUNT(*) FROM dbo.BillingCharges WHERE AdmissionId=$qrId AND ChargeKind=N'Annual';") -eq 1) 'Quick Registration uses the same immediate non-monthly opening-charge policy.'
 # Transfer checks below model already-billed legacy records, independently of new admission policy.
 [void](Query "BEGIN TRAN; DELETE dbo.BillingCharges WHERE EnrollmentId IN($earlyEnrollment,$lateEnrollment) AND ChargeKind=N'Monthly'; UPDATE dbo.Enrollments SET BillingStartDate=dbo.GetMonthlyBillingStartDate(EnrollmentDate) WHERE EnrollmentId IN($earlyEnrollment,$lateEnrollment); EXEC dbo.PostEnrollmentOpeningCharges $earlyEnrollment; EXEC dbo.PostEnrollmentOpeningCharges $lateEnrollment; COMMIT;")
 [void](Query "INSERT dbo.Classes(ClassCode,ClassName,AcademicProgramId) VALUES(N'TARGET',N'Fictional Destination',1); INSERT dbo.CampusClassSections(CampusId,ClassId,SectionId) VALUES(1,2,1); INSERT dbo.FeeConfigurations(CampusId,ClassId,FeeTypeId,Amount,Frequency) VALUES(1,2,1,12000,N'Monthly'),(1,2,2,2500,N'Monthly'),(1,2,3,500,N'Yearly');")
 [void](Query "DECLARE @Date date=DATEADD(day,14,DATEADD(month,-1,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1))); EXEC dbo.TransferEnrollmentClass @UserId=1,@EnrollmentId=$earlyEnrollment,@NewClassSectionId=2,@TransferDate=@Date,@Remarks=N'Fictional transfer before cutoff',@FeePolicy=N'SCHOOL_CUTOFF',@FeeSyncReason=N'Destination fixture rate',@PaidReductionAction=N'Credit';")
 Check ((Number "SELECT SUM(NetAmount) FROM dbo.BillingCharges WHERE AdmissionId=$earlyId AND FeeTypeId=1 AND BillingMonth=DATEADD(month,-1,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1)) AND Status<>N'Cancelled';") -eq 12000) 'Transfer before cutoff applies the new monthly rate to the current bill through a difference.'
 Check ((Number "SELECT DiscountAmount FROM dbo.StudentFeeAssignments WHERE AdmissionId=$earlyId AND FeeTypeId=1 AND EffectiveTo IS NULL;") -eq 2000) 'Transfer preserves the student-specific discount.'
 [void](Query "DECLARE @Date date=DATEADD(day,15,DATEADD(month,-1,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1))); EXEC dbo.TransferEnrollmentClass @UserId=1,@EnrollmentId=$lateEnrollment,@NewClassSectionId=2,@TransferDate=@Date,@Remarks=N'Fictional transfer on cutoff',@FeePolicy=N'SCHOOL_CUTOFF',@FeeSyncReason=N'Destination fixture rate';")
 Check ((Number "SELECT SUM(NetAmount) FROM dbo.BillingCharges WHERE AdmissionId=$lateId AND FeeTypeId=1 AND BillingMonth=DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1) AND Status<>N'Cancelled';") -eq 10000) 'Transfer on cutoff applies the new rate from next month while retaining the discount.'
 Check ((Number "SELECT COUNT(*) FROM dbo.BillingCharges WHERE AdmissionId=$lateId AND FeeTypeId=1 AND BillingMonth=DATEADD(month,-1,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1));") -eq 0) 'Transfer on cutoff does not create current-month tuition for a next-month starter.'
 $returning=Query "DECLARE @Start date=DATEADD(month,-2,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1)),@Left date=DATEADD(day,-1,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1)); INSERT dbo.Students(AdmissionNumber,RegistrationNumber,AdmissionDate,AdmissionCampusId,StudentName,Status,LegacySetupPending) VALUES('4000',N'TEST-4000',@Start,1,N'Returning fixture',N'Active',0); DECLARE @Id int=SCOPE_IDENTITY(); INSERT dbo.Enrollments(AdmissionId,AcademicYearId,ClassSectionId,EnrollmentDate,BillingStartDate,Status,BillingReady,PlacementConfirmedAt) VALUES(@Id,1,1,@Start,@Start,N'Active',1,SYSDATETIME()); DECLARE @E int=SCOPE_IDENTITY(); INSERT dbo.StudentFeeAssignments(EnrollmentId,AdmissionId,FeeTypeId,Amount,DiscountAmount,Frequency,ConcessionLabel) VALUES(@E,@Id,1,10000,1000,N'Monthly',N'Scholarship'); UPDATE dbo.Enrollments SET Status=N'Left',LeavingDate=@Left,BillingReady=0 WHERE EnrollmentId=@E; UPDATE dbo.Students SET Status=N'Left',WithdrawalDate=@Left WHERE AdmissionId=@Id; SELECT @Id AdmissionId;"
 $returnId=[int]$returning.Tables[0].Rows[0].AdmissionId
 [void](Query "DECLARE @R uniqueidentifier=NEWID(); EXEC dbo.ProcessStudentCredit @UserId=1,@AdmissionId=$returnId,@Action=N'Deposit',@Amount=1000,@RequestId=@R,@Reason=N'Fictional existing credit before return';")
 [void](Query "DECLARE @Date date=CONVERT(date,GETDATE()),@Start date=dbo.GetMonthlyBillingStartDate(CONVERT(date,GETDATE())),@Rates nvarchar(max)=(SELECT FC.FeeTypeId,FC.Amount,FC.Frequency FROM dbo.FeeConfigurations FC JOIN dbo.FeeTypes F ON F.FeeTypeId=FC.FeeTypeId WHERE FC.CampusId=1 AND FC.ClassId=1 AND F.IsTransportLinked=0 FOR JSON PATH); SET @Start=DATEFROMPARTS(YEAR(@Start),MONTH(@Start),1); EXEC dbo.ReAdmitStudent @UserId=1,@AdmissionId=$returnId,@CampusId=1,@AcademicYearId=1,@ClassSectionId=1,@EnrollmentDate=@Date,@Reason=N'Fictional readmission',@UsesTransport=0,@MonthlyStartMonth=@Start,@ChargeYearlyFees=1,@ReviewedRates=@Rates;")
 Check ((Number "SELECT COUNT(*) FROM dbo.Enrollments WHERE AdmissionId=$returnId AND Status=N'Active' AND BillingReady=1 AND PlacementConfirmedAt IS NOT NULL;") -eq 1) 'Readmission confirms a new billing placement on the same student.'
 Check ((Number "SELECT COUNT(*) FROM dbo.Enrollments WHERE AdmissionId=$returnId;") -eq 2) 'Readmission preserves the historical placement.'
 Check ((Number "SELECT COUNT(*) FROM dbo.StudentFeeAssignments A JOIN dbo.Enrollments E ON E.EnrollmentId=A.EnrollmentId WHERE E.AdmissionId=$returnId AND E.Status=N'Active' AND (A.DiscountAmount<>0 OR A.ConcessionLabel IS NOT NULL);") -eq 0) 'Readmission loads reviewed class rates without carrying the prior concession into its new assignments.'
 Check ((Number "SELECT COALESCE((SELECT SUM(Amount) FROM dbo.StudentCreditEntries WHERE AdmissionId=$returnId),0)+COALESCE((SELECT SUM(A.Amount) FROM dbo.FeePaymentAllocations A JOIN dbo.FeePayments P ON P.FeePaymentId=A.FeePaymentId WHERE P.AdmissionId=$returnId AND P.IsCreditApplication=1),0);") -eq 1000) 'Readmission preserves existing credit without losing or recreating it.'
 $freeAdmission=Admit 16 $false 'Free'
 $freeId=[int]$freeAdmission.Tables[0].Rows[0].AdmissionId
 Check ((Number "SELECT COUNT(*) FROM dbo.StudentFeeAssignments WHERE AdmissionId=$freeId AND EffectiveTo IS NULL AND FeeTypeId=1 AND ConcessionLabel=N'Free' AND Amount=DiscountAmount;") -eq 1) 'Admission can explicitly designate free monthly tuition from next month.'
 Check ((Number "SELECT COUNT(*) FROM dbo.StudentFeeAssignments WHERE AdmissionId=$freeId AND FeeTypeId=1 AND EffectiveTo IS NOT NULL AND ConcessionLabel IS NOT NULL;") -eq 0) 'Admission concession does not relabel the preceding month.'
 $scholarshipAdmission=Admit 16 $true 'Scholarship'
 $scholarshipId=[int]$scholarshipAdmission.Tables[0].Rows[0].AdmissionId
 Check ((Number "SELECT DiscountAmount FROM dbo.StudentFeeAssignments WHERE AdmissionId=$scholarshipId AND FeeTypeId=1 AND EffectiveTo IS NULL AND ConcessionLabel=N'Scholarship';") -eq 2000) 'Quick Registration preserves a partial scholarship amount and label.'
 $pendingChange=Admit 15 $true
 $pendingId=[int]$pendingChange.Tables[0].Rows[0].AdmissionId
 $pendingDiscounts=Number "SELECT SUM(DiscountAmount) FROM dbo.BillingCharges WHERE AdmissionId=$pendingId;"
 # Legacy discount edits target NEXT month, not every already-issued opening bill.
 # At month-end that month may already be billed; at the academic-year end it
 # may be outside the placement. Keep all three SQL-authoritative cases covered.
 $window=Query "SELECT TOP(1) DATEADD(month,1,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1)) NextMonth,Y.EndDate YearEnd FROM dbo.Enrollments E JOIN dbo.AcademicYears Y ON Y.AcademicYearId=E.AcademicYearId WHERE E.AdmissionId=$pendingId;"
 $nextMonth=[datetime]$window.Tables[0].Rows[0].NextMonth
 $yearEnd=[datetime]$window.Tables[0].Rows[0].YearEnd
 $futureBills=Number "SELECT COUNT(*) FROM dbo.BillingCharges WHERE AdmissionId=$pendingId AND Status<>N'Cancelled' AND BillingMonth>=DATEADD(month,1,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1));"
 $directError=$null
 try{[void](Query "DECLARE @Id int=(SELECT StudentFeeAssignmentId FROM dbo.StudentFeeAssignments WHERE AdmissionId=$pendingId AND FeeTypeId=1 AND EffectiveTo IS NULL); EXEC dbo.SaveStudentFeeDiscount @UserId=1,@StudentFeeAssignmentId=@Id,@DiscountAmount=3000;")}
 catch{$directError=$_.Exception.Message}
 if($nextMonth -gt $yearEnd){
  Check ($directError -like '*within this placement and academic year*') 'A legacy concession cannot extend beyond the recorded academic year.'
  Check ((Number "SELECT DiscountAmount FROM dbo.StudentFeeAssignments WHERE AdmissionId=$pendingId AND FeeTypeId=1 AND EffectiveTo IS NULL;") -eq 2000) 'An out-of-year request leaves the original fee agreement unchanged.'
 }elseif($futureBills -gt 0){
  Check ($directError -like '*Issued bills exist*') 'An already-billed legacy concession period requires explicit reviewed fee corrections.'
  Check ((Number "SELECT DiscountAmount FROM dbo.StudentFeeAssignments WHERE AdmissionId=$pendingId AND FeeTypeId=1 AND EffectiveTo IS NULL;") -eq 2000) 'Refusing an already-billed edit preserves the fee agreement.'
 }else{
  Check (-not $directError) 'A future-only discount can be saved while its next-month period has no issued bill.'
  Check ((Number "SELECT COUNT(*) FROM dbo.StudentFeeAssignments WHERE AdmissionId=$pendingId AND FeeTypeId=1 AND EffectiveFrom=DATEADD(month,1,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1)) AND DiscountAmount=3000;") -eq 1) 'The prospective concession starts next month, not on already-billed dates.'
  Check ((Number "SELECT COUNT(*) FROM dbo.StudentFeeAssignments WHERE AdmissionId=$pendingId AND FeeTypeId=1 AND DiscountAmount=2000 AND EffectiveTo=DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1);") -eq 1) 'The previous month retains its original agreed concession.'
 }
 Check ((Number "SELECT SUM(DiscountAmount) FROM dbo.BillingCharges WHERE AdmissionId=$pendingId;") -eq $pendingDiscounts) 'Prospective or refused concession edits never rewrite an existing opening bill.'
 # Schema 76 retains the preview's informational credit count, but opening and
 # posting bills must not spend advance; explicit collection above owns that.
 [void](Post (Preview))
 [void](Query "DECLARE @R uniqueidentifier=NEWID(); EXEC dbo.ProcessStudentCredit @UserId=1,@AdmissionId=5,@Action=N'Deposit',@Amount=500,@RequestId=@R,@Reason=N'Fictional credit for issued bill';")
 $review=Preview
 Check ($review.Tables[0].Rows[0].PendingCreditApplications -ge 1) 'Opening check notices credit against already-issued current bills.'
 $creditPaymentsBeforeOpening=Number 'SELECT COUNT(*) FROM dbo.FeePayments WHERE IsCreditApplication=1;'
 [void](Post $review)
 Check ((Number 'SELECT SUM(Amount) FROM dbo.StudentCreditEntries WHERE AdmissionId=5;') -eq 500) 'Opening check preserves advance against already-issued bills for explicit collection.'
 Check ((Number 'SELECT COUNT(*) FROM dbo.FeePayments WHERE IsCreditApplication=1;') -eq $creditPaymentsBeforeOpening) 'Opening check creates no credit-payment side effect.'
 [void](Query "DECLARE @Id int=(SELECT StudentFeeAssignmentId FROM dbo.StudentFeeAssignments WHERE AdmissionId=5 AND FeeTypeId=1),@Next date=DATEADD(month,1,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1)); EXEC dbo.ChangeStudentFeeAssignmentAmount @UserId=1,@StudentFeeAssignmentId=@Id,@NewAmount=15000,@EffectiveFrom=@Next,@Reason=N'Fictional full-concession rate increase';")
 Check ((Number 'SELECT Amount-DiscountAmount FROM dbo.StudentFeeAssignments WHERE AdmissionId=5 AND FeeTypeId=1 AND EffectiveTo IS NULL;') -eq 0) 'A full labelled concession stays fully free after a rate increase.'
 # Inject failure in this owned fixture only, then retry two competing clients.
 [void](Query 'UPDATE dbo.Enrollments SET BillingStartDate=EnrollmentDate WHERE EnrollmentId=3;')
 [void](Query "CREATE TRIGGER dbo.BillingFixtureFailure ON dbo.BillingCharges AFTER INSERT AS BEGIN IF EXISTS(SELECT 1 FROM inserted WHERE AdmissionId=3) THROW 51000,'Injected billing failure',1; END;")
 $failed=$false;try{[void](Post (Preview))}catch{$failed=$_.Exception.Message -like '*Injected billing failure*'}
 Check $failed 'An unsuccessful automatic run surfaces its failure.'
 Check ((Number 'SELECT COUNT(*) FROM dbo.BillingCharges WHERE AdmissionId=3;') -eq 0) 'An unsuccessful run leaves no partial bills.'
 [void](Query 'DROP TRIGGER dbo.BillingFixtureFailure;')
 $clients=@();$commands=@();$pending=@()
 try {
  foreach($i in 1..2){
   $client=[Data.SqlClient.SqlConnection]::new("Server=$instance;Database=$catalog;Integrated Security=True;TrustServerCertificate=True")
   $client.Open();$clients+=,$client
   $guard=$client.CreateCommand();$guard.CommandText="SELECT CONVERT(nvarchar(32),value) FROM sys.extended_properties WHERE class=0 AND name=N'FMSC_BillingChecksOwner';"
   if([string]$guard.ExecuteScalar() -ne $owner){throw 'Concurrent test ownership mismatch.'}
   $command=$client.CreateCommand();$command.CommandTimeout=45
   $command.CommandText="DECLARE @M date=DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1),@S dbo.PostingSelectionType; INSERT @S VALUES(3,1,@M); EXEC dbo.PostSelectedMonthlyFees @UserId=1,@BillingMonth=@M,@Selection=@S,@AcademicYearId=1;"
   $commands+=,$command;$pending+=,$command.ExecuteNonQueryAsync()
  }
  foreach($task in $pending){[void]$task.GetAwaiter().GetResult()}
 } finally {foreach($command in $commands){$command.Dispose()};foreach($client in $clients){$client.Dispose()}}
 Check ((Number "SELECT COUNT(*) FROM dbo.BillingCharges WHERE AdmissionId=3 AND BillingMonth=DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1);") -eq 1) 'Two competing posting clients safely retry the failed month without duplicate bills.'
 $promotion=Query "DECLARE @M date=DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1); UPDATE dbo.AcademicYears SET IsCurrent=0; INSERT dbo.AcademicYears(YearName,StartDate,EndDate,IsCurrent) VALUES(N'Fictional previous',DATEADD(year,-1,@M),DATEADD(day,-1,@M),0),(N'Destination fixture',@M,DATEADD(day,-1,DATEADD(year,1,@M)),1); DECLARE @OldYear int=(SELECT AcademicYearId FROM dbo.AcademicYears WHERE YearName=N'Fictional previous'),@NewYear int=SCOPE_IDENTITY(); INSERT dbo.Students(AdmissionNumber,RegistrationNumber,AdmissionDate,AdmissionCampusId,StudentName,Status,LegacySetupPending) VALUES('4001',N'TEST-4001',DATEADD(month,-1,@M),1,N'Promotion with arrears',N'Active',0); DECLARE @Id int=SCOPE_IDENTITY(); INSERT dbo.Enrollments(AdmissionId,AcademicYearId,ClassSectionId,EnrollmentDate,BillingStartDate,Status,BillingReady,PlacementConfirmedAt) VALUES(@Id,@OldYear,1,DATEADD(month,-1,@M),DATEADD(month,-1,@M),N'Active',1,SYSDATETIME()); DECLARE @E int=SCOPE_IDENTITY(); INSERT dbo.StudentFeeAssignments(EnrollmentId,AdmissionId,FeeTypeId,Amount,DiscountAmount,Frequency,ConcessionLabel) VALUES(@E,@Id,1,5000,1000,N'Monthly',N'Scholarship'); DECLARE @S dbo.PostingSelectionType,@Prev date=DATEADD(month,-1,@M); INSERT @S VALUES(@E,1,@Prev); EXEC dbo.PostSelectedMonthlyFees @UserId=1,@BillingMonth=@Prev,@Selection=@S,@AcademicYearId=@OldYear; DECLARE @D dbo.BulkPromotionDecisionType; INSERT @D VALUES(@Id,N'PROMOTE'); EXEC dbo.BulkPromoteStudents @UserId=1,@SourceAcademicYearId=@OldYear,@DestinationAcademicYearId=@NewYear,@SourceCampusId=1,@SourceClassId=1,@SourceSectionId=1,@DestinationClassSectionId=2,@EffectiveDate=@M,@Remarks=N'Fictional selected promotion',@Decisions=@D; SELECT @Id AdmissionId;"
 $promotedId=[int]$promotion.Tables[$promotion.Tables.Count-1].Rows[0].AdmissionId
 Check ((Number "SELECT COUNT(*) FROM dbo.Enrollments WHERE AdmissionId=$promotedId;") -eq 2) 'Selected promotion preserves one identity and both enrolments.'
 Check ((Number "SELECT SUM(dbo.GetFeeChargeBalance(BillingChargeId)) FROM dbo.BillingCharges WHERE AdmissionId=$promotedId;") -eq 4000) 'Promotion preserves previous-year arrears at their historical amount.'
 Check ((Number "SELECT COUNT(*) FROM dbo.StudentFeeAssignments A JOIN dbo.Enrollments E ON E.EnrollmentId=A.EnrollmentId WHERE E.AdmissionId=$promotedId AND E.Status=N'Active' AND A.FeeTypeId=1 AND A.Amount=12000 AND A.DiscountAmount=0 AND A.ConcessionLabel IS NULL;") -eq 1) 'Promotion reads the latest class matrix and resets the old concession.'
 Check ((Number "SELECT COUNT(*) FROM dbo.Enrollments WHERE AdmissionId=$promotedId AND Status=N'Active' AND BillingReady=0;") -eq 1) 'Promoted students await return confirmation before automatic billing.'
 [void](Query "DECLARE @E int=(SELECT EnrollmentId FROM dbo.Enrollments WHERE AdmissionId=$promotedId AND Status=N'Active'),@Today date=CONVERT(date,GETDATE()),@F dbo.ReturnFeeChoiceType; INSERT @F VALUES(1,12000,0),(3,500,0); EXEC dbo.ConfirmPromotedAdmission @UserId=1,@EnrollmentId=@E,@ReturnDate=@Today,@UsesTransport=0,@FeeChoices=@F;")
 $outstanding=Query "EXEC dbo.GetOutstandingCharges @AdmissionId=$promotedId,@EnrollmentId=0,@UserId=1,@AsOfDate=NULL;"
 Check (($outstanding.Tables[0].Rows | Where-Object {$_.Balance -eq 4000}).Count -gt 0) 'Collection can load the old enrolment arrears after promotion and return.'
 $oldConnection=$env:FMSC_BILLING_CHECK_CONNECTION;$oldOwner=$env:FMSC_BILLING_CHECK_OWNER
 try {
  $env:FMSC_BILLING_CHECK_CONNECTION="Server=$instance;Database=$catalog;Integrated Security=True;TrustServerCertificate=True"
  $env:FMSC_BILLING_CHECK_OWNER=$owner
  & dotnet run -c Release --project (Join-Path $projectRoot 'Tests/QuickRegistrationSqlChecks/QuickRegistrationSqlChecks.csproj') -- --billing-compatibility
  Native 'Production billing schema compatibility'
 } finally {$env:FMSC_BILLING_CHECK_CONNECTION=$oldConnection;$env:FMSC_BILLING_CHECK_OWNER=$oldOwner}
 Write-Output "PASS: $checks automatic-billing SQL checks."
} finally {
 if($created -and -not $KeepFixture){
  & SqlLocalDB stop $catalog;Native 'Stop disposable instance'
  & SqlLocalDB delete $catalog;Native 'Delete disposable instance'
  $resolved=[IO.Path]::GetFullPath($run)
  $allowed=[IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts/billing'))+[IO.Path]::DirectorySeparatorChar
  if(-not $resolved.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -ne $owner){throw 'Unsafe fixture cleanup path.'}
  Remove-Item -LiteralPath $resolved -Recurse -Force
 } elseif($created){Write-Output "Retained disposable fixture: $catalog ($run)"}
}
$global:LASTEXITCODE=0
