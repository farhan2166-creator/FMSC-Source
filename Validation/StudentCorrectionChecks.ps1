param([string]$Server)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
if(-not $Server){
 & 'C:/Program Files/Microsoft SQL Server/150/Tools/Binn/SqlLocalDB.exe' start MSSQLLocalDB | Out-Null
 $taskInfo=& 'C:/Program Files/Microsoft SQL Server/150/Tools/Binn/SqlLocalDB.exe' info MSSQLLocalDB
 $Server=($taskInfo|Select-String 'np:').ToString().Replace('Instance pipe name:','').Trim()
}
$taskSuffix=[Guid]::NewGuid().ToString('N').Substring(0,10)
$taskDb='FMSC_CorrectionChecks_'+$taskSuffix
$taskDirectory=Join-Path $taskRoot ('Tests/CorrectionDb-'+$taskSuffix)
New-Item -ItemType Directory -Path $taskDirectory -Force | Out-Null
function Open-Db([string]$catalog){
 $connection=New-Object System.Data.SqlClient.SqlConnection ('Server='+$Server+';Database='+$catalog+';Integrated Security=true;Connect Timeout=15')
 $connection.Open(); return $connection
}
function Query([string]$sql,[hashtable]$parameters=@{}){
 $command=$script:taskConnection.CreateCommand(); $command.CommandTimeout=120; $command.CommandText=$sql
 foreach($key in $parameters.Keys){[void]$command.Parameters.AddWithValue('@'+$key,$parameters[$key])}
 $table=New-Object System.Data.DataTable
 $reader=$null
 try{$reader=$command.ExecuteReader();$table.Load($reader); return ,$table}finally{if($reader){$reader.Dispose()};$command.Dispose()}
}
function Assert([bool]$condition,[string]$label){if(-not $condition){throw $label}; Write-Output ('PASS: '+$label)}
function Expect-Error([scriptblock]$action,[int]$number,[string]$label){
 try{& $action | Out-Null; throw 'Expected refusal did not occur'}catch{
  $exception=$_.Exception
  while($exception.InnerException){$exception=$exception.InnerException}
  if($exception -isnot [System.Data.SqlClient.SqlException] -or $exception.Number -ne $number){throw}
  Write-Output ('PASS: '+$label)
 }
}
$taskMaster=Open-Db 'master'
$script:taskConnection=$taskMaster
$taskCreated=$false
try{
 $backup=Join-Path $taskDirectory 'source.bak'
 [void](Query 'BACKUP DATABASE FMSC_DB TO DISK=@Path WITH COPY_ONLY,INIT;' @{Path=$backup})
 $files=Query 'RESTORE FILELISTONLY FROM DISK=@Path;' @{Path=$backup}
 $dataName=($files.Select("Type='D'")|Select-Object -First 1).LogicalName.Replace("'","''")
 $logName=($files.Select("Type='L'")|Select-Object -First 1).LogicalName.Replace("'","''")
 $dataPath=(Join-Path $taskDirectory 'checks.mdf').Replace("'","''")
 $logPath=(Join-Path $taskDirectory 'checks_log.ldf').Replace("'","''")
 [void](Query "RESTORE DATABASE [$taskDb] FROM DISK=@Path WITH MOVE N'$dataName' TO N'$dataPath',MOVE N'$logName' TO N'$logPath';" @{Path=$backup})
 $taskCreated=$true
 $script:taskConnection=Open-Db $taskDb
 [void](Query "IF (SELECT MAX(TRY_CONVERT(int,VersionNumber)) FROM dbo.DatabaseVersion)<>6 THROW 59990,'Use the current schema-6 database snapshot.',1;")
 [void](Query "IF COL_LENGTH(N'dbo.Enrollments',N'PlacementConfirmedAt') IS NULL OR OBJECT_ID(N'dbo.CompleteQuickAdmission',N'P') IS NULL THROW 59990,'Use the current database snapshot with the admission workflow already installed.',1;")
 # The real reported scenario is exercised on a disposable database copy only.
 $charge=Query 'SELECT * FROM dbo.BillingCharges WHERE BillingChargeId=1 AND AdmissionId=6589;'
 if($charge.Rows.Count -gt 0 -and $charge.Rows[0].Status -eq 'Open'){
  [void](Query "EXEC dbo.ReverseBillingCharge @UserId=1,@BillingChargeId=1,@Reason=N'Isolated test: erroneous October charge';")
  $reversed=Query 'SELECT Status FROM dbo.BillingCharges WHERE BillingChargeId=1;'
  Assert ($reversed.Rows[0].Status -eq 'Cancelled') 'Closed enrollment unpaid charge can be audited and reversed'
 }
 [void](Query @'
 INSERT dbo.FeeConfigurations(CampusId,ClassId,FeeTypeId,Amount,Frequency)
 SELECT CS.CampusId,CS.ClassId,F.FeeTypeId,2000,CASE WHEN F.FeeTypeCode=N'TUITION' THEN N'Monthly' ELSE N'Yearly' END
 FROM (SELECT DISTINCT CampusId,ClassId FROM dbo.CampusClassSections WHERE ClassSectionId IN(39,40)) CS
 CROSS JOIN dbo.FeeTypes F WHERE F.IsMandatory=1 AND NOT EXISTS(
  SELECT 1 FROM dbo.FeeConfigurations C WHERE C.CampusId=CS.CampusId AND C.ClassId=CS.ClassId AND C.FeeTypeId=F.FeeTypeId);
'@)
 $fixture=Query @'
 INSERT dbo.Students(AdmissionNumber,AdmissionDate,AdmissionCampusId,StudentName,Disability,Status)
 VALUES(@Number,'2026-04-01',1,N'Isolated correction test',N'No',N'Active');
 DECLARE @Admission int=CONVERT(int,SCOPE_IDENTITY());
 INSERT dbo.Enrollments(AdmissionId,AcademicYearId,ClassSectionId,EnrollmentDate,BillingStartDate,Status,BillingReady,UsesTransport)
 VALUES(@Admission,28,39,'2026-04-01','2026-10-01',N'Active',0,0);
 SELECT @Admission AdmissionId,CONVERT(int,SCOPE_IDENTITY()) EnrollmentId;
'@ @{Number=([string](900000000+(Get-Random -Minimum 1 -Maximum 9999999)))}
 $admission=[int]$fixture.Rows[0].AdmissionId; $enrollment=[int]$fixture.Rows[0].EnrollmentId
 [void](Query "EXEC dbo.AddStudentFeeAssignment @UserId=1,@EnrollmentId=$enrollment,@FeeTypeId=2;")
 $before=Query "SELECT COUNT(*) Total FROM dbo.BillingCharges WHERE EnrollmentId=$enrollment;"
 Assert ($before.Rows[0].Total -eq 0) 'Draft fee setup does not post charges'
 $correction=[IO.File]::ReadAllText((Join-Path $taskRoot 'Pages/Students/Admissions/PlacementCorrection.sql'))
 $params=@{UserId=1;AdmissionId=$admission;EnrollmentId=$enrollment;YearId=28;PlacementId=40;StartDate=[datetime]'2026-04-01';LeaveDate=[DBNull]::Value;BillingStartDate=[datetime]'2026-10-01';Status='Active';Reason='Correct draft class';Legacy=$false;Preview=$true;ExpectedSnapshot=[DBNull]::Value;RecordHistoricalDeparture=$false}
 $plan=Query $correction $params
 $params.Preview=$false; $params.ExpectedSnapshot=$plan.Rows[0].Snapshot
 [void](Query $correction $params)
 $saved=Query "SELECT ClassSectionId,PlacementConfirmedAt FROM dbo.Enrollments WHERE EnrollmentId=$enrollment;"
 Assert ($saved.Rows[0].ClassSectionId -eq 40 -and $saved.Rows[0].PlacementConfirmedAt -eq [DBNull]::Value) 'Draft placement correction saves without confirming'
 $params.YearId=27; $params.StartDate=[datetime]'2025-07-01'; $params.BillingStartDate=[datetime]'2025-10-01'; $params.Legacy=$true; $params.Preview=$true
 $plan=Query $correction $params
 $params.Preview=$false; $params.ExpectedSnapshot=$plan.Rows[0].Snapshot
 [void](Query $correction $params)
 $yearChanged=Query "SELECT AcademicYearId FROM dbo.Enrollments WHERE EnrollmentId=$enrollment;"
 Assert ($yearChanged.Rows[0].AcademicYearId -eq 27) 'Academic year can be corrected before confirmation'
 $params.YearId=28; $params.StartDate=[datetime]'2026-04-01'; $params.BillingStartDate=[datetime]'2026-10-01'; $params.Legacy=$false; $params.Preview=$true
 $plan=Query $correction $params
 [void](Query "UPDATE dbo.StudentFeeAssignments SET Amount=Amount+1 WHERE EnrollmentId=$enrollment;")
 $params.Preview=$false; $params.ExpectedSnapshot=$plan.Rows[0].Snapshot
 Expect-Error {Query $correction $params} 59618 'Stale correction preview cannot be applied'
 $params.Preview=$true; $plan=Query $correction $params
 $params.Preview=$false; $params.ExpectedSnapshot=$plan.Rows[0].Snapshot
 [void](Query $correction $params)
 $confirmation=Query "EXEC dbo.ConfirmStudentPlacement @UserId=1,@EnrollmentId=$enrollment;"
 $snap=$confirmation.Rows[0].Snapshot
 [void](Query "EXEC dbo.ConfirmStudentPlacement @UserId=1,@EnrollmentId=$enrollment,@Preview=0,@ExpectedSnapshot=@Snapshot;" @{Snapshot=$snap})
 $confirmed=Query "SELECT PlacementConfirmedAt,BillingReady FROM dbo.Enrollments WHERE EnrollmentId=$enrollment; SELECT COUNT(*) FROM dbo.BillingCharges WHERE EnrollmentId=$enrollment;"
 Assert ($confirmed.Rows[0].PlacementConfirmedAt -ne [DBNull]::Value -and $confirmed.Rows[0].BillingReady) 'Explicit confirmation locks placement and enables billing'
 Expect-Error {Query "UPDATE dbo.Enrollments SET AcademicYearId=27 WHERE EnrollmentId=$enrollment;"} 59640 'Confirmed academic year cannot be rewritten'
 $params.Preview=$true; $params.ExpectedSnapshot=[DBNull]::Value
 Expect-Error {Query $correction $params} 59621 'Correction path refuses confirmed placement'
 Expect-Error {Query "EXEC dbo.ConfirmStudentPlacement @UserId=1,@EnrollmentId=$enrollment,@Preview=0,@ExpectedSnapshot=@Snapshot;" @{Snapshot=$snap}} 59642 'Double confirmation cannot duplicate charges'
 $posted=Query "SELECT BillingChargeId FROM dbo.BillingCharges WHERE EnrollmentId=$enrollment AND Status=N'Open';"
 Assert ($posted.Rows.Count -eq 1) 'Confirmation posts opening tuition once'
 $paidId=[long]$posted.Rows[0].BillingChargeId
 [void](Query "UPDATE dbo.BillingCharges SET Status=N'Paid' WHERE BillingChargeId=$paidId;")
 Expect-Error {Query "EXEC dbo.ReverseBillingCharge @UserId=1,@BillingChargeId=$paidId,@Reason=N'Test paid safeguard';"} 53203 'Paid charges require Fee Corrections'
 Expect-Error {Query "INSERT dbo.BillingCharges(StudentFeeAssignmentId,EnrollmentId,AdmissionId,FeeTypeId,BillingMonth,ChargeDate,DueDate,GrossAmount,DiscountAmount,ChargeKind) SELECT StudentFeeAssignmentId,EnrollmentId,AdmissionId,FeeTypeId,'2027-10-01','2027-10-01','2027-10-01',Amount,DiscountAmount,N'Monthly' FROM dbo.StudentFeeAssignments WHERE EnrollmentId=$enrollment;"} 59633 'Out-of-year charges are rejected by the database'
 $params.EnrollmentId=0; $params.YearId=28; $params.PlacementId=40
 Expect-Error {Query $correction $params} 59612 'A second active enrollment is refused'
 [void](Query "UPDATE dbo.BillingCharges SET Status=N'Open' WHERE BillingChargeId=$paidId; UPDATE dbo.Enrollments SET Status=N'Left',LeavingDate=CONVERT(date,GETDATE()) WHERE EnrollmentId=$enrollment;")
 [void](Query "EXEC dbo.ReverseBillingCharge @UserId=1,@BillingChargeId=$paidId,@Reason=N'Isolated closed enrollment correction';")
 $closed=Query "SELECT Status FROM dbo.BillingCharges WHERE BillingChargeId=$paidId;"
 Assert ($closed.Rows[0].Status -eq 'Cancelled') 'Unpaid charge on a closed enrollment can be reversed with history'
 $actual=Query @'
 DECLARE @Selected dbo.FeeTypeIdList,@Discounts dbo.AdmissionFeeDiscountType;
 EXEC dbo.AdmitStudent @UserId=1,@AdmissionDate='2026-10-12',@AdmissionCampusId=1,@AcademicYearId=28,@ClassSectionId=39,
  @StudentName=N'Isolated admission test',@FatherName=NULL,@DateOfBirth='2010-01-01',@PlaceOfBirth=NULL,@Gender=N'Male',
  @BFormNumber=NULL,@Religion=NULL,@BloodGroup=NULL,@ContactNumber=NULL,@AlternateContactNumber=NULL,@Address=NULL,
  @PreviousSchool=NULL,@FatherOccupation=NULL,@Disability=N'No',@UsesTransport=0,@SelectedFeeTypeIds=@Selected,
  @AdmissionDiscounts=@Discounts,@AdmissionNumber=@Number;
'@ @{Number=([string](920000000+(Get-Random -Minimum 1 -Maximum 9999999)))}
 $actualStudent=[int]$actual.Rows[0].AdmissionId
 $newDraft=Query "SELECT EnrollmentId,PlacementConfirmedAt,BillingReady,(SELECT COUNT(*) FROM dbo.BillingCharges B WHERE B.EnrollmentId=E.EnrollmentId) Charges FROM dbo.Enrollments E WHERE AdmissionId=$actualStudent;"
 Assert ($newDraft.Rows[0].PlacementConfirmedAt -is [DateTime] -and $newDraft.Rows[0].BillingReady -and $newDraft.Rows[0].Charges -gt 0) 'Actual admission saves active placement and opening bills'
 $actualEnrollment=[int]$newDraft.Rows[0].EnrollmentId
 $actualBills=Query "SELECT BillingChargeId FROM dbo.BillingCharges WHERE EnrollmentId=$actualEnrollment AND Status=N'Open';"
 Assert ($actualBills.Rows.Count -ge 2) 'Actual admission opening charges are posted by Save'
 $allocatedCharge=[long]$actualBills.Rows[0].BillingChargeId
 [void](Query @"
 INSERT dbo.FeePayments(AdmissionId,EnrollmentId,PaymentDate,ReceiptNumber,TotalAmount,PaymentMethod,Status,CreatedByUserId)
 VALUES($actualStudent,$actualEnrollment,CONVERT(date,GETDATE()),N'ISOLATED-$taskSuffix',1,N'Cash',N'Completed',1);
 DECLARE @Payment int=CONVERT(int,SCOPE_IDENTITY());
 INSERT dbo.FeePaymentAllocations(FeePaymentId,AdmissionId,BillingChargeId,Amount)
 VALUES(@Payment,$actualStudent,$allocatedCharge,1);
"@)
 Expect-Error {Query "EXEC dbo.ReverseBillingCharge @UserId=1,@BillingChargeId=$allocatedCharge,@Reason=N'Isolated allocation safeguard';"} 53203 'Allocated payments protect charges even when charge status is Open'
 Expect-Error {Query "UPDATE dbo.BillingCharges SET Status=N'Cancelled' WHERE BillingChargeId=$allocatedCharge;"} 57103 'Database trigger protects allocated payments from direct cancellation'
 Write-Output 'Student correction integration checks passed. The live school records were not edited.'
}finally{
 if($script:taskConnection -ne $taskMaster){$script:taskConnection.Dispose()}
 $script:taskConnection=$taskMaster
 if($taskCreated){[void](Query "ALTER DATABASE [$taskDb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$taskDb];")}
 $taskMaster.Dispose()
 $resolvedTests=[IO.Path]::GetFullPath((Join-Path $taskRoot 'Tests')).TrimEnd('\')+'\'
 $resolvedOutput=[IO.Path]::GetFullPath($taskDirectory)
 if($resolvedOutput.StartsWith($resolvedTests,[StringComparison]::OrdinalIgnoreCase)){
  Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
 }
}
