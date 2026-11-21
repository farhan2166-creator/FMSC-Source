# Runs only within TestAutomaticBilling's uniquely owned disposable fixture.
[void](Query @'
INSERT dbo.SchoolProfile(SchoolProfileId,SchoolName,CreatedByUserId,UpdatedByUserId,FeeProrationCutoffDay)
 VALUES(1,N'Fictional Transfer School',1,1,16);
UPDATE dbo.FeeTypes SET IsMandatory=1 WHERE FeeTypeId=1;
UPDATE dbo.FeeTypes SET IsTransportLinked=1 WHERE FeeTypeId=2;
INSERT dbo.FeeTypes(FeeTypeCode,FeeTypeName,DefaultFrequency,IsMandatory)
 VALUES(N'ADMISSION',N'Admission Fee',N'Yearly',1),(N'BOOKS',N'Books Fee',N'Yearly',1),(N'LAB',N'Lab Fee',N'Monthly',1);
INSERT dbo.Classes(ClassCode,ClassName,AcademicProgramId) VALUES(N'TARGET',N'Destination Class',1);
INSERT dbo.Campuses(CampusCode,CampusName,CampusType) VALUES(N'OTHER',N'Destination Campus',N'School');
INSERT dbo.CampusClassSections(CampusId,ClassId,SectionId) VALUES(1,2,1),(2,2,1);
INSERT dbo.FeeConfigurations(CampusId,ClassId,FeeTypeId,Amount,Frequency)
 VALUES(1,1,1,10000,N'Monthly'),(1,1,2,2000,N'Monthly'),(1,1,3,5000,N'Yearly'),(1,1,4,200,N'Yearly'),(1,1,5,300,N'Monthly'),
 (1,2,1,12000,N'Monthly'),(1,2,2,2500,N'Monthly'),(1,2,3,6000,N'Yearly'),(1,2,4,400,N'Yearly'),(1,2,5,500,N'Monthly'),
 (2,2,1,13000,N'Monthly'),(2,2,2,2800,N'Monthly'),(2,2,3,7000,N'Yearly'),(2,2,4,600,N'Yearly'),(2,2,5,700,N'Monthly');
INSERT dbo.StudentFeeAssignments(EnrollmentId,AdmissionId,FeeTypeId,Amount,DiscountAmount,Frequency)
 VALUES(1,1,3,5000,0,N'Yearly'),(1,1,4,200,0,N'Yearly'),(1,1,5,300,0,N'Monthly');
BEGIN TRAN;EXEC dbo.PostEnrollmentOpeningCharges 1;COMMIT;
DECLARE @Bill bigint=(SELECT BillingChargeId FROM dbo.BillingCharges WHERE EnrollmentId=1 AND FeeTypeId=3),
 @Today date=CONVERT(date,GETDATE()),@A dbo.FeePaymentAllocationType;
INSERT @A VALUES(@Bill,5000);
EXEC dbo.ProcessFeeTransaction @AdmissionId=1,@EnrollmentId=1,@TransactionType=N'Collection',@Amount=5000,
 @TransactionDate=@Today,@PaymentMethod=N'Cash',@ReferenceNumber=NULL,@Remarks=N'Fictional paid admission fee',
 @FeePaymentId=NULL,@BillingChargeId=NULL,@Allocations=@A,@UserId=1;
'@)
function Snapshot([string]$sql){$data=Query $sql;return [string]$data.Tables[0].Rows[0][0]}
$agreements=Snapshot 'SELECT * FROM dbo.StudentFeeAssignments WHERE EnrollmentId=1 AND FeeTypeId IN(3,4,5) ORDER BY StudentFeeAssignmentId FOR JSON PATH,INCLUDE_NULL_VALUES;'
$charges=Snapshot 'SELECT * FROM dbo.BillingCharges WHERE EnrollmentId=1 AND FeeTypeId IN(3,4,5) ORDER BY BillingChargeId FOR JSON PATH,INCLUDE_NULL_VALUES;'
$payments=Snapshot 'SELECT * FROM dbo.FeePayments ORDER BY FeePaymentId FOR JSON PATH,INCLUDE_NULL_VALUES;'
$allocations=Snapshot 'SELECT * FROM dbo.FeePaymentAllocations ORDER BY FeePaymentId,BillingChargeId FOR JSON PATH,INCLUDE_NULL_VALUES;'
[void](Query "DECLARE @Today date=CONVERT(date,GETDATE()); EXEC dbo.TransferEnrollmentClass @UserId=1,@EnrollmentId=1,@NewClassSectionId=2,@TransferDate=@Today,@Remarks=N'Fictional same-year class transfer',@FeePolicy=N'SCHOOL_CUTOFF',@FeeSyncReason=N'Reviewed tuition and transport';")
Check ((Number "SELECT Amount FROM dbo.StudentFeeAssignments WHERE EnrollmentId=1 AND FeeTypeId=1 AND EffectiveTo IS NULL AND CancelledAt IS NULL;") -eq 12000) 'Same-year class transfer updates tuition to the destination rate.'
Check ((Number "SELECT Amount FROM dbo.StudentFeeAssignments WHERE EnrollmentId=1 AND FeeTypeId=2 AND EffectiveTo IS NULL AND CancelledAt IS NULL;") -eq 2500) 'Transport already in use follows the reviewed destination rate.'
Check ((Snapshot 'SELECT * FROM dbo.StudentFeeAssignments WHERE EnrollmentId=1 AND FeeTypeId IN(3,4,5) ORDER BY StudentFeeAssignmentId FOR JSON PATH,INCLUDE_NULL_VALUES;') -eq $agreements) 'Admission, books and other monthly fee agreements are unchanged.'
Check ((Snapshot 'SELECT * FROM dbo.BillingCharges WHERE EnrollmentId=1 AND FeeTypeId IN(3,4,5) ORDER BY BillingChargeId FOR JSON PATH,INCLUDE_NULL_VALUES;') -eq $charges) 'A paid admission bill and other excluded bills are unchanged.'
Check ((Snapshot 'SELECT * FROM dbo.FeePayments ORDER BY FeePaymentId FOR JSON PATH,INCLUDE_NULL_VALUES;') -eq $payments -and (Snapshot 'SELECT * FROM dbo.FeePaymentAllocations ORDER BY FeePaymentId,BillingChargeId FOR JSON PATH,INCLUDE_NULL_VALUES;') -eq $allocations) 'Transfer preserves the original admission payment and allocations.'
[void](Query "DECLARE @Today date=CONVERT(date,GETDATE()); EXEC dbo.TransferEnrollmentCampus @UserId=1,@EnrollmentId=1,@NewClassSectionId=3,@TransferDate=@Today,@Remarks=N'Fictional same-year campus transfer',@FeePolicy=N'SCHOOL_CUTOFF',@FeeSyncReason=N'Reviewed campus tuition and transport';")
Check ((Number "SELECT Amount FROM dbo.StudentFeeAssignments WHERE EnrollmentId=1 AND FeeTypeId=1 AND EffectiveTo IS NULL AND CancelledAt IS NULL;") -eq 13000) 'Same-year campus transfer updates tuition.'
Check ((Number "SELECT Amount FROM dbo.StudentFeeAssignments WHERE EnrollmentId=1 AND FeeTypeId=2 AND EffectiveTo IS NULL AND CancelledAt IS NULL;") -eq 2800) 'Same-year campus transfer updates existing transport.'
Check ((Snapshot 'SELECT * FROM dbo.StudentFeeAssignments WHERE EnrollmentId=1 AND FeeTypeId IN(3,4,5) ORDER BY StudentFeeAssignmentId FOR JSON PATH,INCLUDE_NULL_VALUES;') -eq $agreements) 'Campus transfer also preserves all excluded agreements.'
[void](Query "UPDATE dbo.Students SET LegacySetupPending=0 WHERE AdmissionId=2; DECLARE @Today date=CONVERT(date,GETDATE()); EXEC dbo.TransferEnrollmentCampus @UserId=1,@EnrollmentId=2,@NewClassSectionId=3,@TransferDate=@Today,@Remarks=N'Fictional tuition-only student transfer',@FeePolicy=N'SCHOOL_CUTOFF',@FeeSyncReason=N'Reviewed tuition only';")
Check ((Number 'SELECT COUNT(*) FROM dbo.StudentFeeAssignments WHERE EnrollmentId=2 AND FeeTypeId IN(2,3,4,5);') -eq 0) 'Destination rates do not add unused transport, admission, books or other monthly fees.'
$expectedSchema=(Get-ChildItem (Join-Path $projectRoot 'Migrations') -Filter '*.sql' | ForEach-Object { [int]$_.BaseName } | Measure-Object -Maximum).Maximum
Check ((Number 'SELECT TOP(1) TRY_CONVERT(int,VersionNumber) FROM dbo.DatabaseVersion ORDER BY VersionId DESC;') -eq $expectedSchema) 'The full migration chain reaches the current source schema target.'
