SET NOCOUNT ON;
CREATE TABLE #Students(AdmissionId int,Status nvarchar(30),WithdrawalDate date,DateOfBirth date,AdmissionDate date);
CREATE TABLE #AcademicYears(AcademicYearId int,YearName nvarchar(200),StartDate date,EndDate date);
CREATE TABLE #Enrollments(EnrollmentId int,AdmissionId int,AcademicYearId int,Status nvarchar(30),EnrollmentDate date,LeavingDate date,ClassSectionId int NOT NULL DEFAULT 2);
CREATE TABLE #AcademicPrograms(AcademicProgramId int,ProgramType nvarchar(30));
CREATE TABLE #Classes(ClassId int,AcademicProgramId int,DisplayOrder int);
CREATE TABLE #CampusClassSections(ClassSectionId int,ClassId int,IsActive bit);
INSERT #AcademicPrograms VALUES(1,N'School'),(2,N'College');
INSERT #Classes VALUES(1,1,1),(2,1,2),(3,1,3),(4,2,1);
INSERT #CampusClassSections VALUES(1,1,1),(2,2,1),(3,3,1),(4,4,1);
DECLARE @Today date=CONVERT(date,GETDATE()),@Base date=DATEFROMPARTS(YEAR(GETDATE())-3,1,1);
INSERT #AcademicYears VALUES(1,N'Old year',@Base,DATEADD(day,-1,DATEADD(year,1,@Base))),
 (2,N'Previous year',DATEADD(year,1,@Base),DATEADD(day,-1,DATEADD(year,2,@Base))),
 (3,N'Active year',DATEADD(year,2,@Base),@Today),(4,N'Future',DATEADD(year,1,@Today),DATEADD(year,2,@Today));
INSERT #Students VALUES(1,N'Active',NULL,DATEADD(year,-15,@Base),@Base),
 (2,N'Withdrawn',DATEADD(month,4,@Base),DATEADD(year,-15,@Base),@Base),
 (3,N'Inactive',NULL,DATEADD(year,-15,@Base),@Base),
 (4,N'Active',NULL,DATEADD(year,-15,@Base),@Base),
 (5,N'Left',NULL,DATEADD(year,-15,@Base),@Base),
 (6,N'Active',NULL,DATEADD(year,-15,@Base),DATEADD(month,15,@Base)),
 (7,N'Active',NULL,DATEADD(year,-15,@Base),NULL);
INSERT #Enrollments(EnrollmentId,AdmissionId,AcademicYearId,Status,EnrollmentDate,LeavingDate) VALUES(1,1,3,N'Active',DATEADD(year,2,@Base),NULL),
 (2,2,1,N'Left',DATEADD(month,2,@Base),DATEADD(month,4,@Base)),
 (3,4,1,N'Left',DATEADD(month,2,@Base),DATEADD(month,3,@Base)),
 (4,4,1,N'Left',DATEADD(month,6,@Base),DATEADD(month,7,@Base)),
 (5,4,3,N'Active',DATEADD(year,2,@Base),NULL),
 (6,5,1,N'Left',DATEADD(month,2,@Base),DATEADD(month,4,@Base));
CREATE TABLE #Actual(WindowId int,AcademicYearId int,YearName nvarchar(200),GapStart date,GapEnd date,PromotionDate date);
DECLARE @AdmissionId int=1;
-- POLICY
INSERT #Actual SELECT * FROM @MissingWindows;
IF EXISTS(SELECT 1 FROM #Actual WHERE AcademicYearId IN(3,4)) THROW 51000,'Active or future year was offered.',1;
IF NOT EXISTS(SELECT 1 FROM #Actual WHERE AcademicYearId=2 AND PromotionDate=DATEADD(year,2,@Base)) THROW 51000,'Promotion to a recorded destination was not offered.',1;
IF EXISTS(SELECT 1 FROM #Actual WHERE GapEnd>@Today) THROW 51000,'Future dates were offered.',1;
PRINT 'PASS: active year excluded; previous-year promotion boundary available; future dates excluded.';
GO
DECLARE @AdmissionId int=2;
-- POLICY
IF EXISTS(SELECT 1 FROM @MissingWindows WHERE GapEnd>(SELECT WithdrawalDate FROM #Students WHERE AdmissionId=2)) THROW 51000,'Post-withdrawal history was offered.',1;
IF NOT EXISTS(SELECT 1 FROM @MissingWindows) THROW 51000,'Valid pre-withdrawal history was lost.',1;
PRINT 'PASS: withdrawn student can fill only pre-departure history.';
GO
DECLARE @AdmissionId int=3;
-- POLICY
IF EXISTS(SELECT 1 FROM @MissingWindows) THROW 51000,'Inactive student without departure evidence was offered speculative history.',1;
PRINT 'PASS: unknown departure cutoff fails closed.';
GO
DECLARE @AdmissionId int=4;
-- POLICY
IF (SELECT COUNT(*) FROM @MissingWindows WHERE AcademicYearId=1)<>3 THROW 51000,'Separate historical gaps were not retained.',1;
IF EXISTS(SELECT 1 FROM @MissingWindows W JOIN #Enrollments E ON E.AdmissionId=4
 WHERE W.GapStart<=COALESCE(E.LeavingDate,'99991231') AND W.GapEnd>=E.EnrollmentDate) THROW 51000,'A date window overlaps existing history.',1;
PRINT 'PASS: multiple gaps in a year remain separate and non-overlapping.';
-- Guard extracted from the production transactional save; legacy acknowledgement cannot bypass it.
DECLARE @EnrollmentId int=0,@YearId int=3,@PlacementId int=2,@StartDate date=(SELECT MIN(EnrollmentDate) FROM #Enrollments WHERE AdmissionId=4 AND Status=N'Active'),
 @LeaveDate date=CONVERT(date,GETDATE()),@Status nvarchar(30)=N'Active',@BillingStartDate date=CONVERT(date,GETDATE());
BEGIN TRY
 -- BILLING_GUARD
 THROW 51000,'Save guard accepted a billing date for historical placement.',1;
END TRY BEGIN CATCH IF ERROR_NUMBER()<>59623 THROW; END CATCH;
SET @BillingStartDate=NULL;
-- BILLING_GUARD
PRINT 'PASS: historical billing date is rejected; an empty billing date is accepted.';
BEGIN TRY
 -- GUARD
 THROW 51000,'Save guard accepted an active placement.',1;
END TRY BEGIN CATCH IF ERROR_NUMBER()<>59622 THROW; END CATCH;
SET @Status=N'Left'; SET @YearId=1; SELECT @StartDate=MIN(EnrollmentDate),@LeaveDate=MAX(LeavingDate) FROM #Enrollments WHERE AdmissionId=4 AND AcademicYearId=1;
BEGIN TRY
 -- GUARD
 THROW 51000,'Save guard accepted an overlapping historical record.',1;
END TRY BEGIN CATCH IF ERROR_NUMBER()<>59622 THROW; END CATCH;
SELECT TOP(1) @StartDate=GapStart,@LeaveDate=GapEnd FROM @MissingWindows WHERE AcademicYearId=1 ORDER BY GapStart;
-- GUARD
SET @Status=N'Promoted';
BEGIN TRY
 -- GUARD
 THROW 51000,'Save guard accepted promotion without its destination date.',1;
END TRY BEGIN CATCH IF ERROR_NUMBER()<>59622 THROW; END CATCH;
PRINT 'PASS: save rejects active/overlapping/invalid-promotion records and accepts a closed historical gap.';
GO
DECLARE @AdmissionId int=5;
-- POLICY
IF EXISTS(SELECT 1 FROM @MissingWindows WHERE GapEnd>(SELECT MAX(LeavingDate) FROM #Enrollments WHERE AdmissionId=5)) THROW 51000,'Departure fallback allowed post-leaving history.',1;
PRINT 'PASS: recorded leaving date supplies a safe cutoff when student withdrawal date is absent.';
GO
DECLARE @AdmissionId int=6;
-- POLICY
IF EXISTS(SELECT 1 FROM @MissingWindows WHERE AcademicYearId=1) THROW 51000,'Pre-admission academic year was offered.',1;
IF EXISTS(SELECT 1 FROM @MissingWindows WHERE GapStart<(SELECT AdmissionDate FROM #Students WHERE AdmissionId=6)) THROW 51000,'Pre-admission date was offered.',1;
IF NOT EXISTS(SELECT 1 FROM @MissingWindows WHERE AcademicYearId=2 AND GapStart=(SELECT AdmissionDate FROM #Students WHERE AdmissionId=6)) THROW 51000,'Admission-year gap did not start on the admission date.',1;
DECLARE @EnrollmentId int=0,@YearId int=2,@PlacementId int=2,@StartDate date=(SELECT DATEADD(day,-1,AdmissionDate) FROM #Students WHERE AdmissionId=6),
 @LeaveDate date=(SELECT MIN(GapEnd) FROM @MissingWindows WHERE AcademicYearId=2),@Status nvarchar(30)=N'Left',@Legacy bit=1;
BEGIN TRY
 -- GUARD
 THROW 51000,'Legacy acknowledgement bypassed first admission date.',1;
END TRY BEGIN CATCH IF ERROR_NUMBER()<>59622 THROW; END CATCH;
SET @StartDate=(SELECT AdmissionDate FROM #Students WHERE AdmissionId=6);
-- GUARD
PRINT 'PASS: years before first admission are excluded; admission-year dates are clamped; save rejects earlier dates even with legacy acknowledgement.';
GO
DECLARE @AdmissionId int=7;
-- POLICY
IF EXISTS(SELECT 1 FROM @MissingWindows) THROW 51000,'Unknown first admission date must fail closed.',1;
PRINT 'PASS: missing first admission date cannot offer speculative history.';
GO
-- Reproduce 6589: a later Play Group placement cannot have earlier Nursery/One history.
DECLARE @Base date=(SELECT StartDate FROM #AcademicYears WHERE AcademicYearId=1);
INSERT #Students VALUES(8,N'Active',NULL,DATEADD(year,-8,@Base),@Base);
INSERT #Enrollments VALUES(8,8,3,N'Active',DATEADD(year,2,@Base),NULL,1);
DECLARE @AdmissionId int=8;
-- POLICY
IF EXISTS(SELECT 1 FROM @MissingPlacements WHERE ClassSectionId IN(2,3,4)) THROW 51000,'Higher class was offered before Play Group.',1;
DECLARE @EnrollmentId int=0,@YearId int=2,@PlacementId int=2,@StartDate date=DATEADD(year,1,@Base),
 @LeaveDate date=DATEADD(day,-1,DATEADD(year,2,@Base)),@Status nvarchar(30)=N'Left';
BEGIN TRY
 -- GUARD
 THROW 51000,'Save accepted Nursery before Play Group.',1;
END TRY BEGIN CATCH IF ERROR_NUMBER()<>59622 THROW; END CATCH;
SET @PlacementId=3;
BEGIN TRY
 -- GUARD
 THROW 51000,'Save accepted One before Play Group.',1;
END TRY BEGIN CATCH IF ERROR_NUMBER()<>59622 THROW; END CATCH;
SET @PlacementId=1;
-- GUARD
SET @Status=N'Promoted'; SET @LeaveDate=DATEADD(year,2,@Base);
BEGIN TRY
 -- GUARD
 THROW 51000,'Same-class repetition was accepted as promotion.',1;
END TRY BEGIN CATCH IF ERROR_NUMBER()<>59622 THROW; END CATCH;
PRINT 'PASS: 6589 regression rejects Nursery/One before Play Group; repetition remains possible without false promotion.';
GO
DECLARE @Base date=(SELECT StartDate FROM #AcademicYears WHERE AcademicYearId=1);
INSERT #Students VALUES(9,N'Active',NULL,DATEADD(year,-8,@Base),@Base);
INSERT #Enrollments VALUES(9,9,1,N'Left',@Base,DATEADD(month,4,@Base),2),
 (10,9,3,N'Active',DATEADD(year,2,@Base),NULL,3);
DECLARE @AdmissionId int=9;
-- POLICY
IF EXISTS(SELECT 1 FROM @MissingPlacements P JOIN @MissingWindows W ON W.WindowId=P.WindowId
 WHERE W.AcademicYearId=2 AND P.ClassSectionId IN(1,4)) THROW 51000,'Class outside the preceding/following bounds was offered.',1;
IF NOT EXISTS(SELECT 1 FROM @MissingPlacements P JOIN @MissingWindows W ON W.WindowId=P.WindowId
 WHERE W.AcademicYearId=2 AND P.ClassSectionId=2 AND P.CanPromote=1) THROW 51000,'Valid progression to higher destination was lost.',1;
PRINT 'PASS: missing class fits both previous and following placements; promotion requires a higher destination.';
GO
ALTER TABLE #Students ADD AdmissionNumber nvarchar(40), UpdatedAt datetime2;
CREATE TABLE #StudentPostingHolds(AdmissionId int,ResumeDate date);
CREATE TABLE #StudentStatusHistory(AdmissionId int,OldStatus nvarchar(30),NewStatus nvarchar(30),Reason nvarchar(250),
 EffectiveDate date,Remarks nvarchar(500),CreatedBy int);
UPDATE #Students SET AdmissionNumber=N'4' WHERE AdmissionId=4;
INSERT #Students(AdmissionId,Status,AdmissionDate,AdmissionNumber) VALUES(10,N'Active','20140401',N'10');
GO
DECLARE @AdmissionId int=10,@EnrollmentId int=0,@Status nvarchar(30)=N'Left',@StudentStatus nvarchar(30)=N'Active',
 @StartDate date='20140401',@LeaveDate date='20150331',@Reason nvarchar(500)=N'Verified school departure',
 @RecordHistoricalDeparture bit=0,@UserId int=1;
-- DEPARTURE_GUARD
IF @CanRecordDeparture<>1 THROW 51000,'No departure option for an eligible final historical placement.',1;
-- DEPARTURE_WRITE
IF NOT EXISTS(SELECT 1 FROM #Students WHERE AdmissionId=10 AND Status=N'Active' AND WithdrawalDate IS NULL)
 OR EXISTS(SELECT 1 FROM #StudentStatusHistory WHERE AdmissionId=10)
 THROW 51000,'History-only save changed the student status or wrote a departure event.',1;
GO
DECLARE @AdmissionId int=10,@EnrollmentId int=0,@Status nvarchar(30)=N'Left',@StudentStatus nvarchar(30)=N'Active',
 @StartDate date='20140401',@LeaveDate date='20150331',@Reason nvarchar(500)=N'Verified school departure',
 @RecordHistoricalDeparture bit=1,@UserId int=1;
-- DEPARTURE_GUARD
-- DEPARTURE_WRITE
IF NOT EXISTS(SELECT 1 FROM #Students WHERE AdmissionId=10 AND Status=N'Left' AND WithdrawalDate=@LeaveDate)
 OR NOT EXISTS(SELECT 1 FROM #StudentStatusHistory WHERE AdmissionId=10 AND OldStatus=N'Active' AND NewStatus=N'Left'
  AND EffectiveDate=@LeaveDate AND Reason=@Reason AND CreatedBy=@UserId)
 THROW 51000,'Opted-in departure did not update the student and preserve the verified reason and date.',1;
PRINT 'PASS: historical placement offers an explicit school departure, preserving status by default.';
GO
DECLARE @AdmissionId int=4,@EnrollmentId int=0,@Status nvarchar(30)=N'Left',@StudentStatus nvarchar(30)=N'Active',
 @StartDate date='20140401',@LeaveDate date='20150331',@RecordHistoricalDeparture bit=1;
BEGIN TRY
 -- DEPARTURE_GUARD
 THROW 51000,'Historical departure accepted when a later active placement exists.',1;
END TRY BEGIN CATCH IF ERROR_NUMBER()<>59624 THROW; END CATCH;
GO
UPDATE #Students SET Status=N'Active',WithdrawalDate=NULL WHERE AdmissionId=10;
INSERT #StudentPostingHolds VALUES(10,NULL);
DECLARE @AdmissionId int=10,@EnrollmentId int=0,@Status nvarchar(30)=N'Left',@StudentStatus nvarchar(30)=N'Active',
 @StartDate date='20140401',@LeaveDate date='20150331',@RecordHistoricalDeparture bit=1;
BEGIN TRY
 -- DEPARTURE_GUARD
 THROW 51000,'Historical departure accepted during an active hold.',1;
END TRY BEGIN CATCH IF ERROR_NUMBER()<>59624 THROW; END CATCH;
PRINT 'PASS: later placements and active holds prevent backdated school departure.';
