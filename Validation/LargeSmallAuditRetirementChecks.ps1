# Runs only inside Tools/TestSmallAuditRetirement.ps1's owned disposable fixture.
if($catalog -notmatch '^FMSC_BillingChecks_[0-9a-f]{32}$' -or $instance -ne ('(localdb)\'+$catalog)){throw 'This check requires an owned disposable fixture.'}

# Setup and protected role edits.
[void](Query "INSERT dbo.Roles(RoleName) VALUES(N'Accountant');")
$roleId=[int](Number "SELECT RoleId FROM dbo.Roles WHERE RoleName=N'Accountant';")
Refused "EXEC dbo.SaveRole @UserId=2,@RoleName=N'Accountant',@RoleId=$roleId;" 50003 'Role saving retains permission checks.'
Refused "EXEC dbo.SaveRole @UserId=1,@RoleName=N'New role',@RoleId=0;" 57410 'Creating extra roles remains disabled.'
Refused "EXEC dbo.SaveRole @UserId=1,@RoleName=N'Administrator',@RoleId=1;" 57410 'Administrator remains protected.'
Refused "EXEC dbo.SaveRole @UserId=1,@RoleName=N'Renamed',@RoleId=$roleId;" 57410 'Renaming Accountant remains disabled.'
$result=Query "EXEC dbo.SaveRole @UserId=1,@RoleName=N'  Accountant  ',@RoleId=$roleId,@Description=N'  Reviewed role  ';"
Check ($result.Tables[0].Rows[0].RoleId -eq $roleId -and (Number "SELECT COUNT(*) FROM dbo.Roles WHERE RoleId=$roleId AND RoleName=N'Accountant' AND Description=N'Reviewed role';") -eq 1) 'Accountant description can still be edited with trimming and returned identity.'
[void](Query "EXEC dbo.SaveRole @UserId=1,@RoleName=N'Accountant',@RoleId=$roleId,@Description=N' ';" )
Check ((Number "SELECT COUNT(*) FROM dbo.Roles WHERE RoleId=$roleId AND Description IS NULL;") -eq 1) 'Blank role description remains NULL.'
$school="EXEC dbo.SaveSchoolProfile @UserId=1,@SchoolName=N'  Fictional School  ',@AddressLine=N'  Address  ',@City=N'  Peshawar  ',@ContactNumber=N'  000  ',@Email=N'  example@example.invalid  ',@LogoImage=0x010203,@LogoFileName=N'  logo.png  ',@LogoContentType=N'  image/png  ',@FeeProrationCutoffDay=16;"
Refused ($school.Replace('@UserId=1','@UserId=2')) 50003 'School setup retains permission checks.'
Refused ($school.Replace("@SchoolName=N'  Fictional School  '","@SchoolName=N' '")) 59000 'School name remains required.'
Refused ($school.Replace('@FeeProrationCutoffDay=16','@FeeProrationCutoffDay=29')) 59001 'Invalid fee cutoff remains rejected.'
$result=Query $school
Check ($result.Tables[0].Rows[0].SchoolProfileId -eq 1 -and (Number "SELECT COUNT(*) FROM dbo.SchoolProfile WHERE SchoolProfileId=1 AND SchoolName=N'Fictional School' AND AddressLine=N'Address' AND City=N'Peshawar' AND ContactNumber=N'000' AND Email=N'example@example.invalid' AND LogoImage=0x010203 AND LogoFileName=N'logo.png' AND LogoContentType=N'image/png' AND FeeProrationCutoffDay=16 AND CreatedByUserId=1 AND UpdatedByUserId=1;") -eq 1) 'School creation preserves all fields, image bytes, cutoff and returned identity.'
[void](Query ($school.Replace("@SchoolName=N'  Fictional School  '","@SchoolName=N' Updated School '").Replace('@FeeProrationCutoffDay=16','@FeeProrationCutoffDay=0')))
Check ((Number "SELECT COUNT(*) FROM dbo.SchoolProfile WHERE SchoolName=N'Updated School' AND FeeProrationCutoffDay IS NULL AND UpdatedAt IS NOT NULL AND UpdatedByUserId=1;") -eq 1) 'School updates normalize zero cutoff to NULL and record updater.'
$fee="EXEC dbo.SaveFeeConfiguration @UserId=1,@CampusId=1,@ClassId=1,@FeeTypeId=1,@Amount=500,@Frequency=N'Monthly',@FeeConfigurationId=0,@Remarks=N'  Rate note  ';"
Refused ($fee.Replace('@UserId=1','@UserId=2')) 50003 'Fee rate saving retains permission checks.'
Refused ($fee.Replace('@CampusId=1','@CampusId=999999')) 56310 'Fee rate rejects unknown campus.'
Refused ($fee.Replace('@ClassId=1','@ClassId=999999')) 56312 'Fee rate rejects unknown class.'
Refused ($fee.Replace('@FeeTypeId=1','@FeeTypeId=999999')) 56313 'Fee rate rejects unknown fee type.'
Refused ($fee.Replace('@Amount=500','@Amount=-1')) 56314 'Fee rate rejects negative amount.'
Refused ($fee.Replace('@Amount=500','@Amount=500.50')) 547 'Fee rate retains whole-rupee constraint.'
$result=Query $fee;$feeId=[int]$result.Tables[0].Rows[0].FeeConfigurationId
Check ($feeId -gt 0 -and (Number "SELECT COUNT(*) FROM dbo.FeeConfigurations WHERE FeeConfigurationId=$feeId AND Amount=500 AND Frequency=N'Monthly' AND Remarks=N'Rate note';") -eq 1) 'Fee rate creation returns its ID and saves trimmed remarks.'
$feeUpdate=$fee.Replace('@FeeConfigurationId=0',"@FeeConfigurationId=$feeId")
[void](Query $feeUpdate)
Check ((Number "SELECT COUNT(*) FROM dbo.FeeConfigurations WHERE FeeConfigurationId=$feeId AND Amount=500 AND UpdatedAt IS NOT NULL;") -eq 1) 'Unchanged fee rate still refreshes its timestamp.'
[void](Query ($feeUpdate.Replace('@Amount=500','@Amount=600').Replace("@Remarks=N'  Rate note  '","@Remarks=N' '")))
Check ((Number "SELECT COUNT(*) FROM dbo.FeeConfigurations WHERE FeeConfigurationId=$feeId AND Amount=600 AND Remarks IS NULL;") -eq 1) 'Changed fee rate saves amount and clears blank remarks.'
Refused ($fee.Replace('@FeeConfigurationId=0','@FeeConfigurationId=999999')) 56315 'Unknown fee rate update remains rejected.'

# Billing start changes and number edits: preserve transaction/counter contracts.
Refused "EXEC dbo.SetEnrollmentBillingStartDate @UserId=2,@EnrollmentId=1,@BillingStartDate='20260101',@Reason=N'Test';" 50003 'Billing start editing retains permission checks.'
Refused "EXEC dbo.SetEnrollmentBillingStartDate @UserId=1,@EnrollmentId=1,@BillingStartDate=NULL,@Reason=N'Test';" 58001 'Billing start date remains required.'
Refused "EXEC dbo.SetEnrollmentBillingStartDate @UserId=1,@EnrollmentId=1,@BillingStartDate='20260101',@Reason=N' ';" 58002 'Billing start change still requires a reason.'
Refused "EXEC dbo.SetEnrollmentBillingStartDate @UserId=1,@EnrollmentId=999999,@BillingStartDate='20260101',@Reason=N'Test';" 58003 'Billing start rejects unknown placement.'
Refused "DECLARE @D date=(SELECT DATEADD(day,-1,EnrollmentDate) FROM dbo.Enrollments WHERE EnrollmentId=1); EXEC dbo.SetEnrollmentBillingStartDate @UserId=1,@EnrollmentId=1,@BillingStartDate=@D,@Reason=N'Test';" 58004 'Billing start still cannot precede enrollment.'
$result=Query "DECLARE @D date=(SELECT DATEADD(day,5,EnrollmentDate) FROM dbo.Enrollments WHERE EnrollmentId=1); EXEC dbo.SetEnrollmentBillingStartDate @UserId=1,@EnrollmentId=1,@BillingStartDate=@D,@Reason=N'Reviewed legacy date';"
Check ($result.Tables[0].Rows[0].EnrollmentId -eq 1 -and $result.Tables[0].Rows[0].AdmissionId -eq 1 -and ([datetime]$result.Tables[0].Rows[0].NewBillingStartDate-$result.Tables[0].Rows[0].OldBillingStartDate).Days -eq 5) 'Billing start returns original/new dates and identities.'
Check ((Number 'SELECT COUNT(*) FROM dbo.Enrollments WHERE EnrollmentId=1 AND BillingStartDate=DATEADD(day,5,EnrollmentDate);') -eq 1) 'Billing start editing preserves the independent enrollment date.'
[void](Query 'UPDATE dbo.AdmissionNumberCounter SET LastNumber=1004 WHERE CounterId=1;')
Refused "EXEC dbo.SetStudentAdmissionNumber @UserId=1,@AdmissionId=1,@AdmissionNumber='2001a';" 59205 'Admission number editing still requires the caller transaction.'
Refused "BEGIN TRAN; EXEC dbo.SetStudentAdmissionNumber @UserId=2,@AdmissionId=1,@AdmissionNumber='2001a'; COMMIT;" 50003 'Admission number editing retains permission checks.'
Refused "BEGIN TRAN; EXEC dbo.SetStudentAdmissionNumber @UserId=1,@AdmissionId=1,@AdmissionNumber='bad'; COMMIT;" 59203 'Invalid admission number remains rejected.'
Refused "BEGIN TRAN; EXEC dbo.SetStudentAdmissionNumber @UserId=1,@AdmissionId=1,@AdmissionNumber='1002'; COMMIT;" 59204 'Duplicate admission number remains rejected.'
Refused "BEGIN TRAN; EXEC dbo.SetStudentAdmissionNumber @UserId=1,@AdmissionId=999999,@AdmissionNumber='2001a'; COMMIT;" 51710 'Admission number rejects unknown student.'
[void](Query "BEGIN TRAN; EXEC dbo.SetStudentAdmissionNumber @UserId=1,@AdmissionId=1,@AdmissionNumber=' 2001A '; COMMIT;")
Check ((Number "SELECT COUNT(*) FROM dbo.Students WHERE AdmissionId=1 AND AdmissionNumber='2001a';") -eq 1 -and (Number 'SELECT LastNumber FROM dbo.AdmissionNumberCounter WHERE CounterId=1;') -eq 2001) 'Renumbering normalizes the suffix and synchronizes the numeric counter.'
[void](Query "BEGIN TRAN; EXEC dbo.SetStudentAdmissionNumber @UserId=1,@AdmissionId=1,@AdmissionNumber='2001a'; COMMIT;")
Check ((Number 'SELECT LastNumber FROM dbo.AdmissionNumberCounter WHERE CounterId=1;') -eq 2001) 'Saving the same admission number remains a no-op.'

# Legacy verification and pending-admission confirmation preserve operational history.
Refused 'EXEC dbo.VerifyLegacyStudent @UserId=2,@AdmissionId=2;' 50003 'Legacy verification retains permission checks.'
Refused 'EXEC dbo.VerifyLegacyStudent @UserId=1,@AdmissionId=1;' 59730 'Legacy verification rejects a regular record.'
[void](Query "EXEC dbo.VerifyLegacyStudent @UserId=1,@AdmissionId=2;")
Check ((Number 'SELECT COUNT(*) FROM dbo.Students WHERE AdmissionId=2 AND LegacySetupPending=1 AND LegacyVerifiedAt IS NOT NULL AND LegacyVerifiedBy=1;') -eq 1) 'Legacy verification saves the verification marker without activating billing.'
[void](Query 'UPDATE dbo.Students SET LegacySetupPending=1 WHERE AdmissionId=4;')
Refused 'EXEC dbo.VerifyLegacyStudent @UserId=1,@AdmissionId=4;' 59733 'Legacy verification still requires exactly one active placement.'
$quickSeed=Query "DECLARE @D date=CONVERT(date,GETDATE()); INSERT dbo.Students(QuickReference,AdmissionDate,AdmissionCampusId,StudentName,FatherName,Gender,Address,PlaceOfBirth,Status,LegacySetupPending) VALUES(N'QR-AUDIT-TEST',@D,1,N'Pending Student',N'Pending Father',N'Male',N'Fictional address',N'Peshawar',N'Pending',0); SELECT CONVERT(int,SCOPE_IDENTITY()) AdmissionId;"
$pendingId=[int]$quickSeed.Tables[0].Rows[0].AdmissionId
Refused "EXEC dbo.CompleteQuickAdmission @UserId=2,@AdmissionId=$pendingId;" 50003 'Quick admission confirmation retains permission checks.'
Refused 'EXEC dbo.CompleteQuickAdmission @UserId=1,@AdmissionId=1;' 59701 'Quick admission rejects already active students.'
Refused "EXEC dbo.CompleteQuickAdmission @UserId=1,@AdmissionId=$pendingId;" 59703 'Quick admission requires a pending placement.'
$quickPlacement=Query "DECLARE @D date=CONVERT(date,GETDATE()); INSERT dbo.Enrollments(AdmissionId,AcademicYearId,ClassSectionId,EnrollmentDate,BillingStartDate,Status,BillingReady) VALUES($pendingId,1,1,@D,@D,N'Pending',0); SELECT CONVERT(int,SCOPE_IDENTITY()) EnrollmentId;"
$pendingEnrollment=[int]$quickPlacement.Tables[0].Rows[0].EnrollmentId
[void](Query "UPDATE dbo.Students SET Address=NULL WHERE AdmissionId=$pendingId;")
Refused "EXEC dbo.CompleteQuickAdmission @UserId=1,@AdmissionId=$pendingId;" 59702 'Quick admission retains required-details validation.'
[void](Query "UPDATE dbo.Students SET Address=N'Fictional address' WHERE AdmissionId=$pendingId;")
$result=Query "EXEC dbo.CompleteQuickAdmission @UserId=1,@AdmissionId=$pendingId;"
Check ($result.Tables[0].Rows[0].AdmissionNumber -eq '2002' -and (Number "SELECT COUNT(*) FROM dbo.Students WHERE AdmissionId=$pendingId AND AdmissionNumber='2002' AND RegistrationNumber IS NOT NULL AND Status=N'Active';") -eq 1) 'Quick confirmation allocates official numbers and activates the student.'
Check ((Number "SELECT COUNT(*) FROM dbo.Enrollments WHERE EnrollmentId=$pendingEnrollment AND Status=N'Active' AND PlacementConfirmedAt IS NOT NULL AND BillingReady=0;") -eq 1) 'Quick confirmation retains the original placement and billing state.'
Check ((Number "SELECT COUNT(*) FROM dbo.EnrollmentHistory WHERE EnrollmentId=$pendingEnrollment AND ActionType=N'ADMISSION' AND OldStatus=N'Pending' AND NewStatus=N'Active';") -eq 1 -and (Number "SELECT COUNT(*) FROM dbo.StudentStatusHistory WHERE AdmissionId=$pendingId AND OldStatus=N'Pending' AND NewStatus=N'Active';") -eq 1) 'Quick confirmation retains both placement and student status history.'
Refused "EXEC dbo.CompleteQuickAdmission @UserId=1,@AdmissionId=$pendingId;" 59701 'Quick confirmation cannot allocate numbers twice.'

# Removing a fee assignment cancels unpaid charges; paid charges remain protected.
Refused 'EXEC dbo.DeleteStudentFeeAssignment @UserId=2,@StudentFeeAssignmentId=1;' 50003 'Fee assignment removal retains permission checks.'
Refused 'EXEC dbo.DeleteStudentFeeAssignment @UserId=1,@StudentFeeAssignmentId=999999;' 53120 'Fee assignment removal rejects unknown assignment.'
[void](Query "DECLARE @M date=DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1); INSERT dbo.BillingCharges(StudentFeeAssignmentId,EnrollmentId,AdmissionId,FeeTypeId,BillingMonth,ChargeDate,GrossAmount,DiscountAmount,ChargeKind) VALUES(1,1,1,1,@M,@M,10000,0,N'Monthly'),(2,1,1,2,@M,@M,2000,0,N'Monthly'); INSERT dbo.FeePayments(AdmissionId,EnrollmentId,PaymentDate,ReceiptNumber,TotalAmount,PaymentMethod,Status,CreatedByUserId) VALUES(1,1,GETDATE(),N'TEST-PAID-PROTECTED',100,N'Cash',N'Completed',1); DECLARE @P int=SCOPE_IDENTITY(); INSERT dbo.FeePaymentAllocations(FeePaymentId,BillingChargeId,AdmissionId,Amount) SELECT @P,BillingChargeId,1,100 FROM dbo.BillingCharges WHERE StudentFeeAssignmentId=2;")
Refused 'EXEC dbo.DeleteStudentFeeAssignment @UserId=1,@StudentFeeAssignmentId=2;' 53121 'Paid fee assignment cannot be removed.'
Check ((Number "SELECT COUNT(*) FROM dbo.StudentFeeAssignments WHERE StudentFeeAssignmentId=2 AND CancelledAt IS NULL;") -eq 1 -and (Number 'SELECT COUNT(*) FROM dbo.FeePaymentAllocations;') -eq 1) 'Rejected paid-fee removal preserves assignment and payment allocation.'
[void](Query 'EXEC dbo.DeleteStudentFeeAssignment @UserId=1,@StudentFeeAssignmentId=1;')
Check ((Number 'SELECT COUNT(*) FROM dbo.StudentFeeAssignments WHERE StudentFeeAssignmentId=1 AND CancelledAt IS NOT NULL AND UpdatedAt IS NOT NULL;') -eq 1 -and (Number "SELECT COUNT(*) FROM dbo.BillingCharges WHERE StudentFeeAssignmentId=1 AND Status=N'Cancelled' AND Remarks LIKE N'%Cancelled with its fee assignment.%';") -eq 1) 'Unpaid fee removal cancels charges and timestamps the assignment without deleting either record.'
Refused 'EXEC dbo.DeleteStudentFeeAssignment @UserId=1,@StudentFeeAssignmentId=1;' 53120 'Already cancelled assignment cannot be removed twice.'
