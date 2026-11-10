# Called only inside TestAutomaticBilling's uniquely owned schema-only fixture.
[void](Query @'
INSERT dbo.SchoolProfile(SchoolProfileId,SchoolName,CreatedByUserId,UpdatedByUserId,FeeProrationCutoffDay)
 VALUES(1,N'Fictional admission school',1,1,28);
UPDATE dbo.FeeTypes SET IsMandatory=1 WHERE FeeTypeId=1;
INSERT dbo.FeeTypes(FeeTypeCode,FeeTypeName,DefaultFrequency,IsMandatory) VALUES(N'ADMISSION',N'Admission',N'Yearly',1);
INSERT dbo.FeeConfigurations(CampusId,ClassId,FeeTypeId,Amount,Frequency)
 VALUES(1,1,1,10000,N'Monthly'),(1,1,3,500,N'Yearly');
UPDATE dbo.AdmissionNumberCounter SET LastNumber=2000 WHERE CounterId=1;
DECLARE @Start date=(SELECT StartDate FROM dbo.AcademicYears WHERE AcademicYearId=1);
INSERT dbo.AcademicYears(YearName,StartDate,EndDate,IsCurrent)
 VALUES(N'Past fixture year',DATEADD(year,-1,@Start),DATEADD(day,-1,@Start),0);
'@)
function NewAdmission([int]$year=1,[bool]$quick=$false){
 return Query @'
DECLARE @Date date=(SELECT StartDate FROM dbo.AcademicYears WHERE AcademicYearId=@Year),
 @Ids dbo.FeeTypeIdList,@Discounts dbo.AdmissionFeeDiscountType;
EXEC dbo.AdmitStudent @UserId=1,@AdmissionDate=@Date,@AdmissionCampusId=1,@AcademicYearId=@Year,
 @ClassSectionId=1,@StudentName=N'Fictional admission billing example',@FatherName=N'Fictional Parent',
 @DateOfBirth=NULL,@PlaceOfBirth=N'Fictional Town',@Gender=N'Male',@BFormNumber=NULL,@Religion=NULL,
 @BloodGroup=NULL,@ContactNumber=NULL,@AlternateContactNumber=NULL,@Address=N'Fictional Street',
 @PreviousSchool=NULL,@FatherOccupation=NULL,@Disability=N'No',@UsesTransport=0,
 @SelectedFeeTypeIds=@Ids,@AdmissionDiscounts=@Discounts,@QuickRegistration=@Quick;
'@ @{'@Year'=$year;'@Quick'=$quick}
}
$admitted=NewAdmission
$id=[int]$admitted.Tables[0].Rows[0].AdmissionId
Check ((Number "SELECT COUNT(*) FROM dbo.Enrollments E JOIN dbo.Students S ON S.AdmissionId=E.AdmissionId JOIN dbo.AcademicYears Y ON Y.AcademicYearId=E.AcademicYearId WHERE E.AdmissionId=$id AND S.AdmissionDate=Y.StartDate AND E.EnrollmentDate=S.AdmissionDate;") -eq 1) 'Admission and placement retain the same earlier date.'
Check ((Number "SELECT COUNT(*) FROM dbo.Enrollments WHERE AdmissionId=$id AND BillingStartDate=dbo.GetMonthlyBillingStartDate(CONVERT(date,GETDATE())) AND BillingReady=1;") -eq 1) 'New admission billing applies the cutoff to today, independently of admission date.'
Check ((Number "SELECT COUNT(*) FROM dbo.BillingCharges WHERE AdmissionId=$id AND ChargeKind=N'Monthly' AND BillingMonth=DATEFROMPARTS(YEAR(dbo.GetMonthlyBillingStartDate(CONVERT(date,GETDATE()))),MONTH(dbo.GetMonthlyBillingStartDate(CONVERT(date,GETDATE()))),1);") -eq 1) 'Opening monthly tuition uses the current cutoff month.'
Check ((Number "SELECT COUNT(*) FROM dbo.BillingCharges WHERE AdmissionId=$id AND ChargeKind=N'Monthly' AND BillingMonth<DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1);") -eq 0) 'Earlier placement months generate no monthly arrears.'
Check ((Number "SELECT COUNT(*) FROM dbo.BillingCharges WHERE AdmissionId=$id AND ChargeKind=N'Annual';") -eq 1) 'Current academic year admission retains its one-time admission fee.'
[void](Query 'UPDATE dbo.SchoolProfile SET FeeProrationCutoffDay=1;')
$late=NewAdmission
$lateId=[int]$late.Tables[0].Rows[0].AdmissionId
Check ((Number "SELECT COUNT(*) FROM dbo.BillingCharges WHERE AdmissionId=$lateId AND ChargeKind=N'Monthly' AND BillingMonth=DATEADD(month,1,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1));") -eq 1) 'At or after the current cutoff, new monthly billing starts next month.'
$quick=NewAdmission 1 $true
$quickId=[int]$quick.Tables[0].Rows[0].AdmissionId
Check ((Number "SELECT COUNT(*) FROM dbo.Enrollments WHERE AdmissionId=$quickId AND Status=N'Pending' AND BillingStartDate=dbo.GetMonthlyBillingStartDate(CONVERT(date,GETDATE()));") -eq 1) 'New quick registrations use the same current-date billing policy.'
$beforeCharges=Number 'SELECT COUNT(*) FROM dbo.BillingCharges;'
$historical=NewAdmission 2
$oldId=[int]$historical.Tables[0].Rows[0].AdmissionId
Check ((Number "SELECT COUNT(*) FROM dbo.Enrollments WHERE AdmissionId=$oldId AND BillingStartDate IS NULL AND BillingReady=0;") -eq 1) 'A completed academic year has no billing start and is not ready for automatic billing.'
Check ((Number "SELECT COUNT(*) FROM dbo.BillingCharges WHERE AdmissionId=$oldId;") -eq 0 -and (Number "SELECT COUNT(*) FROM dbo.StudentFeeAssignments WHERE AdmissionId=$oldId;") -eq 0) 'Historical admission creates neither monthly nor one-time fees or assignments.'
Check ((Number "SELECT COUNT(*) FROM dbo.EnrollmentHistory WHERE AdmissionId=$oldId AND ActionType=N'ADMISSION';") -eq 1 -and (Number "SELECT COUNT(*) FROM dbo.StudentStatusHistory WHERE AdmissionId=$oldId;") -eq 1) 'Historical admission preserves placement and student lifecycle history.'
Check ((Number 'SELECT COUNT(*) FROM dbo.BillingCharges;') -eq $beforeCharges) 'Historical admission leaves existing charges untouched.'
[void](Query 'DELETE dbo.FeeConfigurations WHERE CampusId=1 AND ClassId=1;')
$withoutRates=NewAdmission 2
Check ($withoutRates.Tables[0].Rows.Count -eq 1) 'Historical admission does not require current class fee rates.'
Check ((Number 'SELECT COUNT(*) FROM dbo.FeePayments;') -eq 0) 'Admission never fabricates payment records.'
Check ((Number "SELECT COUNT(*) FROM dbo.fn_MonthlyBillingCandidates(DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1),1) WHERE AdmissionId=$oldId AND BlockReason IS NULL;") -eq 0) 'Historical admission remains excluded from automatic and missed-month billing.'
Check ((Number "SELECT COUNT(*) FROM dbo.DatabaseVersion WHERE VersionNumber=N'44';") -eq 1) 'The full sequential migration chain reaches schema 44.'
