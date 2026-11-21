-- Schema 38: a returning student may only be readmitted in the current academic year.
-- Reapply the schema-3 readmission procedure with one narrowly scoped date/year guard;
-- migrations 4-37 do not modify this procedure. Existing enrollment and finance data are untouched.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF EXISTS(SELECT 1 FROM dbo.DatabaseVersion WHERE TRY_CONVERT(int,VersionNumber)=38) RETURN;
IF ISNULL((SELECT TOP(1) TRY_CONVERT(int,VersionNumber) FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0)<>37
 THROW 51000,'Readmission current-year safeguard requires schema 37.',1;
BEGIN TRY
 BEGIN TRANSACTION;
 EXEC(N'CREATE OR ALTER PROCEDURE dbo.ReAdmitStudent
@UserId INT, @AdmissionId INT, @CampusId INT, @AcademicYearId INT, @ClassSectionId INT, @EnrollmentDate DATE, @Reason NVARCHAR (500), @UsesTransport BIT, @MonthlyStartMonth date=NULL, @ChargeYearlyFees bit=NULL, @ReviewedRates nvarchar(max)=NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    EXECUTE dbo.RequirePermission @UserId, N''Students.Administer'';
    IF @AdmissionId <= 0
        THROW 56510, ''A valid student is required.'', 1;
    IF @CampusId <= 0
        THROW 56511, ''A valid campus is required.'', 1;
    IF @AcademicYearId <= 0
        THROW 56512, ''A valid academic year is required.'', 1;
    IF @ClassSectionId <= 0
        THROW 56513, ''A valid class section is required.'', 1;
    IF @EnrollmentDate IS NULL
        THROW 56514, ''Enrollment date is required.'', 1;
    IF @EnrollmentDate > CONVERT (DATE, SYSDATETIME())
        THROW 56515, ''Enrollment date cannot be in the future.'', 1;

    -- Readmission changes the student''s operational status and can post fees.
    -- Historical placement corrections must use the separate history workflow.
    IF NOT EXISTS (SELECT 1 FROM dbo.AcademicYears Y
                   WHERE Y.AcademicYearId=@AcademicYearId AND Y.IsCurrent=1
                     AND CONVERT(date,SYSDATETIME()) BETWEEN Y.StartDate AND Y.EndDate
                     AND @EnrollmentDate BETWEEN Y.StartDate AND Y.EndDate)
        THROW 57109, ''Readmission is only permitted in the current academic year. Record earlier years through historical placement correction.'',1;

    IF @MonthlyStartMonth IS NULL OR @ChargeYearlyFees IS NULL
        THROW 57100, ''Choose the monthly start month and yearly-fee decision before readmission.'',1;
    BEGIN TRANSACTION;
    DECLARE @OldStudentStatus AS NVARCHAR (30), @OldWithdrawalDate AS DATE, @OldEnrollmentId AS INT = NULL, @ClassId AS INT, @SectionCampusId AS INT, @MaxPriorLeavingDate AS DATE = NULL, @YearStartDate AS DATE, @YearEndDate AS DATE;
    SELECT @OldStudentStatus = S.Status,
           @OldWithdrawalDate = S.WithdrawalDate
    FROM   dbo.Students AS S WITH (UPDLOCK, HOLDLOCK)
    WHERE  S.AdmissionId = @AdmissionId;
    IF @OldStudentStatus IS NULL
        THROW 56516, ''The student was not found.'', 1;
    SELECT TOP (1) @OldEnrollmentId = E.EnrollmentId
    FROM   dbo.Enrollments AS E WITH (UPDLOCK, HOLDLOCK)
    WHERE  E.AdmissionId = @AdmissionId
           AND E.Status = N''Active'';
    IF @OldEnrollmentId IS NOT NULL
        THROW 56517, ''The student already has an active enrollment.'', 1;
    IF @OldStudentStatus = N''Active''
        THROW 56524, ''The student is already active.'', 1;

    -- Left includes leaving certificates, recorded departures, and final-class completion.
    -- Withdrawn is accepted as a legacy synonym, not a new departure status.
    IF @OldStudentStatus NOT IN (N''Left'',N''Withdrawn'',N''Inactive'')
       OR NOT EXISTS(SELECT 1 FROM dbo.Students S WHERE S.AdmissionId=@AdmissionId
          AND NULLIF(LTRIM(RTRIM(S.AdmissionNumber)),N'''') IS NOT NULL)
        THROW 56529, ''Only officially admitted former students who left or became inactive can be readmitted. Cancelled pending registrations must use Reopen Registration.'',1;
    IF EXISTS(SELECT 1 FROM dbo.StudentPostingHolds H WITH(UPDLOCK,HOLDLOCK)
              WHERE H.AdmissionId=@AdmissionId AND H.ResumeDate IS NULL)
        THROW 56530, ''This student is on hold. Use Lift Hold / Resume rather than readmission.'',1;

    SELECT @MaxPriorLeavingDate = MAX(E2.LeavingDate)
    FROM   dbo.Enrollments AS E2
    WHERE  E2.AdmissionId = @AdmissionId
           AND E2.LeavingDate IS NOT NULL;
    IF @MaxPriorLeavingDate IS NOT NULL
       AND @EnrollmentDate <= @MaxPriorLeavingDate
        BEGIN
            ROLLBACK;
            THROW 56527, ''The re-admission enrollment date must be after the student''''s most recent leaving date on file. Correct the prior enrollment''''s leaving date first if this re-admission is genuinely meant to start on or before it.'', 1;
        END
    IF NOT EXISTS (SELECT 1
                   FROM   dbo.Campuses
                   WHERE  CampusId = @CampusId)
        THROW 56518, ''The selected campus is not active.'', 1;
    IF NOT EXISTS (SELECT 1
                   FROM   dbo.AcademicYears
                   WHERE  AcademicYearId = @AcademicYearId)
        THROW 56519, ''The selected academic year is not active.'', 1;
    SELECT @YearStartDate = StartDate,
           @YearEndDate = EndDate
    FROM   dbo.AcademicYears
    WHERE  AcademicYearId = @AcademicYearId;
    IF @EnrollmentDate < @YearStartDate
       OR @EnrollmentDate > @YearEndDate
        BEGIN
            ROLLBACK;
            THROW 56528, ''The re-admission enrollment date must fall within the selected academic year''''s date range.'', 1;
        END
    SELECT @SectionCampusId = CCS.CampusId,
           @ClassId = CCS.ClassId
    FROM   dbo.CampusClassSections AS CCS
    WHERE  CCS.ClassSectionId = @ClassSectionId;
    IF @SectionCampusId IS NULL
        THROW 56521, ''The selected class section is invalid.'', 1;
    IF @SectionCampusId <> @CampusId
        THROW 56522, ''The selected class section does not belong to the selected campus.'', 1;
    DECLARE @MissingMandatoryFees AS NVARCHAR (1000);
    SELECT @MissingMandatoryFees = STRING_AGG(FT.FeeTypeName, N'', '')
    FROM   dbo.FeeTypes AS FT
    WHERE  FT.IsMandatory = 1
           AND NOT EXISTS (SELECT 1
                           FROM   dbo.FeeConfigurations AS FC
                           WHERE  FC.CampusId = @CampusId
                                  AND FC.ClassId = @ClassId
                                  AND FC.FeeTypeId = FT.FeeTypeId);
    IF @MissingMandatoryFees IS NOT NULL
        BEGIN
            DECLARE @MandatoryFeeErrorMsg AS NVARCHAR (1100) = N''Required fee configuration is missing for the selected class: '' + @MissingMandatoryFees + N''.'';
            ROLLBACK;
            THROW 56525, @MandatoryFeeErrorMsg, 1;
        END

    IF @ReviewedRates IS NULL OR ISJSON(@ReviewedRates)<>1
      THROW 57107,''Review the readmission rates before confirming.'',1;
    DECLARE @Rates TABLE(FeeTypeId int,Amount decimal(12,2),Frequency nvarchar(40));
    INSERT @Rates SELECT FeeTypeId,Amount,Frequency FROM OPENJSON(@ReviewedRates)
       WITH(FeeTypeId int,Amount decimal(12,2),Frequency nvarchar(40));
    IF EXISTS(SELECT FC.FeeTypeId,FC.Amount,FC.Frequency FROM dbo.FeeConfigurations FC WITH(UPDLOCK,HOLDLOCK)
       JOIN dbo.FeeTypes FT ON FT.FeeTypeId=FC.FeeTypeId WHERE FC.CampusId=@CampusId AND FC.ClassId=@ClassId
       AND (@UsesTransport=1 OR FT.IsTransportLinked=0) EXCEPT SELECT * FROM @Rates)
       OR EXISTS(SELECT * FROM @Rates EXCEPT SELECT FC.FeeTypeId,FC.Amount,FC.Frequency FROM dbo.FeeConfigurations FC
       JOIN dbo.FeeTypes FT ON FT.FeeTypeId=FC.FeeTypeId WHERE FC.CampusId=@CampusId AND FC.ClassId=@ClassId
       AND (@UsesTransport=1 OR FT.IsTransportLinked=0))
      THROW 57108,''Readmission rates changed. Review them again; nothing was saved.'',1;
    DECLARE @BillingStartDate date;
    IF DAY(@MonthlyStartMonth)<>1 OR @MonthlyStartMonth<DATEFROMPARTS(YEAR(@EnrollmentDate),MONTH(@EnrollmentDate),1)
       OR @MonthlyStartMonth>DATEFROMPARTS(YEAR(@YearEndDate),MONTH(@YearEndDate),1)
        THROW 57101,''Choose a monthly start month within the remaining academic year.'',1;
    SET @BillingStartDate=@MonthlyStartMonth;
    INSERT  dbo.Enrollments (AdmissionId, AcademicYearId, ClassSectionId, EnrollmentDate, BillingStartDate, Status, Remarks, UsesTransport, BillingReady, PlacementConfirmedAt)
    VALUES                 (@AdmissionId, @AcademicYearId, @ClassSectionId, @EnrollmentDate, @BillingStartDate, N''Active'', @Reason, @UsesTransport, 1, SYSDATETIME());
    DECLARE @EnrollmentId AS INT;
    SET @EnrollmentId = SCOPE_IDENTITY();
    INSERT dbo.StudentFeeAssignments (EnrollmentId, AdmissionId, FeeTypeId, Amount, DiscountAmount, Frequency, Remarks)
    SELECT @EnrollmentId,
           @AdmissionId,
           FC.FeeTypeId,
           FC.Amount,
           CASE WHEN FC.Frequency=N''Yearly'' AND @ChargeYearlyFees=0 THEN FC.Amount ELSE 0 END,
           FC.Frequency,
           CASE WHEN FC.Frequency=N''Yearly'' AND @ChargeYearlyFees=0 THEN N''Yearly fee explicitly waived at re-admission'' ELSE N''Created by re-admission'' END
    FROM   dbo.FeeConfigurations AS FC
           INNER JOIN
           dbo.FeeTypes AS FT
           ON FT.FeeTypeId = FC.FeeTypeId
    WHERE  FC.CampusId = @CampusId
           AND FC.ClassId = @ClassId
           AND (@UsesTransport = 1
                OR FT.IsTransportLinked = 0);
    UPDATE dbo.Students
    SET    Status         = N''Active'',
           WithdrawalDate = NULL,
           LegacySetupPending = 0,
           UpdatedAt      = SYSDATETIME()
    WHERE  AdmissionId = @AdmissionId;
    IF @@ROWCOUNT <> 1
        THROW 56523, ''The student status could not be updated.'', 1;
    EXECUTE dbo.PostEnrollmentOpeningCharges @EnrollmentId = @EnrollmentId,@UserId=@UserId;
    INSERT  dbo.EnrollmentHistory (EnrollmentId, AdmissionId, ActionType, ActionDate, OldClassSectionId, NewClassSectionId, OldStatus, NewStatus, Remarks, UserId)
    VALUES                       (@EnrollmentId, @AdmissionId, N''RE-ADMIT'', @EnrollmentDate, NULL, @ClassSectionId, NULL, N''Active'', @Reason, @UserId);
    INSERT  dbo.StudentStatusHistory (AdmissionId, OldStatus, NewStatus, Reason, EffectiveDate, Remarks, CreatedBy)
    VALUES                          (@AdmissionId, @OldStudentStatus, N''Active'', N''Re-admission'', @EnrollmentDate, @Reason, @UserId);

COMMIT TRANSACTION;
    SELECT @AdmissionId AS AdmissionId,
           @EnrollmentId AS EnrollmentId,
           @EnrollmentDate AS EnrollmentDate;
END');
 INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
 VALUES(N'38',SYSDATETIME(),SUSER_SNAME(),N'Current-year-only readmission safeguard');
 COMMIT;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK;
 THROW;
END CATCH;
