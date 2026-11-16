-- Admission is the correction workspace. This batch previews and applies the
-- same plan under locks; it never deletes enrollment, charge or payment history.
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRY
 BEGIN TRANSACTION;
 EXEC dbo.RequirePermission @UserId,N'Students.Administer';
 IF NULLIF(LTRIM(RTRIM(@Reason)),N'') IS NULL THROW 59601,'Enter a reason for the correction.',1;
 DECLARE @Locked int,@Current nvarchar(max),@Snapshot varchar(64),@StudentStatus nvarchar(30),@AdmissionDate date;
 SELECT @StudentStatus=Status,@AdmissionDate=AdmissionDate FROM dbo.Students WITH(UPDLOCK,HOLDLOCK) WHERE AdmissionId=@AdmissionId;
 IF @StudentStatus IS NULL THROW 59602,'The student no longer exists.',1;
 SELECT @Locked=COUNT(*) FROM dbo.Enrollments WITH(UPDLOCK,HOLDLOCK) WHERE AdmissionId=@AdmissionId;
 SELECT @Locked=COUNT(*) FROM dbo.StudentFeeAssignments WITH(UPDLOCK,HOLDLOCK) WHERE AdmissionId=@AdmissionId;
 SELECT @Locked=COUNT(*) FROM dbo.BillingCharges WITH(UPDLOCK,HOLDLOCK) WHERE AdmissionId=@AdmissionId;
 SELECT @Locked=COUNT(*) FROM dbo.FeePayments WITH(UPDLOCK,HOLDLOCK) WHERE AdmissionId=@AdmissionId;
 SELECT @Locked=COUNT(*) FROM dbo.FeePaymentAllocations WITH(UPDLOCK,HOLDLOCK) WHERE AdmissionId=@AdmissionId;
 SELECT @Locked=COUNT(*) FROM dbo.LeaveCertificates WITH(UPDLOCK,HOLDLOCK) WHERE AdmissionId=@AdmissionId;
 -- MISSING_PLACEMENT_POLICY
 IF @EnrollmentId=0 AND @BillingStartDate IS NOT NULL
  THROW 59623,'Missing historical placements do not have a billing start date.',1;
 IF @EnrollmentId=0 AND NOT EXISTS
  (SELECT 1 FROM @MissingWindows W JOIN @MissingPlacements P ON P.WindowId=W.WindowId
   WHERE AcademicYearId=@YearId AND P.ClassSectionId=@PlacementId AND @StartDate>=GapStart AND @StartDate<=GapEnd
   AND ((@Status=N'Left' AND @LeaveDate BETWEEN @StartDate AND GapEnd)
    OR (@Status=N'Promoted' AND P.CanPromote=1 AND @LeaveDate=PromotionDate)))
  THROW 59622,'The dates or class sequence do not fit an available historical gap. Reload Placement History. Use Re-admission, Transfer or Promotion for a new placement.',1;
 DECLARE @YearStart date,@YearEnd date;
 SELECT @YearStart=StartDate,@YearEnd=EndDate FROM dbo.AcademicYears WITH(HOLDLOCK) WHERE AcademicYearId=@YearId;
 IF @YearStart IS NULL THROW 59603,'Choose a valid academic year.',1;
 IF NOT EXISTS(SELECT 1 FROM dbo.CampusClassSections WITH(HOLDLOCK) WHERE ClassSectionId=@PlacementId)
  THROW 59604,'Choose a valid campus, class and section.',1;
 IF @StartDate<@YearStart OR (@YearEnd IS NOT NULL AND @StartDate>@YearEnd)
  THROW 59605,'Enrollment start must be within the chosen academic year. Correct the year or start date.',1;
 IF @StartDate>CONVERT(date,GETDATE()) OR @LeaveDate>CONVERT(date,GETDATE())
  THROW 59606,'Recorded enrollment dates cannot be in the future.',1;
 IF @StartDate<@AdmissionDate AND @Legacy=0
  THROW 59607,'This starts before First Admission. Verify the dates, or acknowledge a verified legacy record.',1;
 IF @LeaveDate<@StartDate THROW 59608,'Leaving date cannot be before enrollment start.',1;
 IF @BillingStartDate IS NOT NULL AND (@BillingStartDate<@StartDate OR (@YearEnd IS NOT NULL AND @BillingStartDate>@YearEnd)
  OR (@LeaveDate IS NOT NULL AND @BillingStartDate>@LeaveDate))
  THROW 59620,'Billing start must fit the corrected enrollment dates and academic year. Adjust it or leave it unset.',1;
 IF @Status NOT IN(N'Active',N'Left',N'Promoted') THROW 59609,'Choose Active, Left or Promoted.',1;
 IF @Status=N'Active' AND @LeaveDate IS NOT NULL THROW 59610,'An active enrollment cannot have a leaving date.',1;
 IF @Status<>N'Active' AND @LeaveDate IS NULL THROW 59611,'Enter the leaving or promotion date for a closed enrollment.',1;
 -- A gap alone does not establish that the student left the school.
 DECLARE @CanRecordDeparture bit=0;
 IF @EnrollmentId=0 AND @Status=N'Left' AND @StudentStatus=N'Active'
  AND EXISTS(SELECT 1 FROM dbo.Students WHERE AdmissionId=@AdmissionId AND WithdrawalDate IS NULL
   AND NULLIF(LTRIM(RTRIM(AdmissionNumber)),N'') IS NOT NULL)
  AND NOT EXISTS(SELECT 1 FROM dbo.Enrollments WHERE AdmissionId=@AdmissionId
   AND (Status IN(N'Active',N'Pending') OR (Status<>N'Cancelled' AND (EnrollmentDate>=@StartDate OR LeavingDate>@LeaveDate))))
  AND NOT EXISTS(SELECT 1 FROM dbo.StudentPostingHolds WITH(UPDLOCK,HOLDLOCK) WHERE AdmissionId=@AdmissionId AND ResumeDate IS NULL)
  SET @CanRecordDeparture=1;
 IF @RecordHistoricalDeparture=1 AND @CanRecordDeparture=0
  THROW 59624,'This placement cannot record the school departure. Reload Placement History and check for a later placement, an open placement or an active hold.',1;
 IF @Status=N'Active' AND EXISTS(SELECT 1 FROM dbo.Enrollments WHERE AdmissionId=@AdmissionId AND EnrollmentId<>@EnrollmentId AND Status=N'Active')
  THROW 59612,'An active enrollment already exists. Correct that record, or close it with its verified leaving date first.',1;
 IF @EnrollmentId>0 AND @Status=N'Promoted' AND NOT EXISTS(SELECT 1 FROM dbo.Enrollments WHERE AdmissionId=@AdmissionId AND EnrollmentId<>@EnrollmentId AND Status=N'Active')
  THROW 59613,'Promoted requires an active destination enrollment. Use Promotion for an actual move to the next year.',1;
 IF EXISTS(SELECT 1 FROM dbo.Enrollments WHERE AdmissionId=@AdmissionId AND EnrollmentId<>@EnrollmentId AND AcademicYearId=@YearId
  AND ClassSectionId=@PlacementId AND EnrollmentDate=@StartDate)
  THROW 59614,'This placement and start date are already recorded. Select that record; do not create a duplicate.',1;
 IF EXISTS(SELECT 1 FROM dbo.Enrollments WHERE AdmissionId=@AdmissionId AND EnrollmentId<>@EnrollmentId
  AND EnrollmentDate<COALESCE(@LeaveDate,CONVERT(date,'99991231')) AND @StartDate<COALESCE(LeavingDate,CONVERT(date,'99991231')))
  AND @Legacy=0 THROW 59615,'These dates overlap another enrollment. Correct the dates, or acknowledge verified legacy history.',1;
 DECLARE @CreatingHistory bit=CASE WHEN @EnrollmentId=0 THEN 1 ELSE 0 END;
 DECLARE @OldYear int,@OldPlacement int,@OldStatus nvarchar(30),@OldStart date,@OldLeave date,@OldBilling date,@Ready bit;
 IF @EnrollmentId>0
 BEGIN
  SELECT @OldYear=AcademicYearId,@OldPlacement=ClassSectionId,@OldStatus=Status,@OldStart=EnrollmentDate,
   @OldLeave=LeavingDate,@OldBilling=BillingStartDate,@Ready=BillingReady
  FROM dbo.Enrollments WHERE EnrollmentId=@EnrollmentId AND AdmissionId=@AdmissionId;
  IF @OldYear IS NULL THROW 59616,'The selected enrollment no longer exists. Reload Admission.',1;
  IF EXISTS(SELECT 1 FROM dbo.Enrollments WHERE EnrollmentId=@EnrollmentId AND PlacementConfirmedAt IS NOT NULL) AND NOT EXISTS(SELECT 1 FROM dbo.Students WHERE AdmissionId=@AdmissionId AND LegacySetupPending=1)
   THROW 59621,'This placement is confirmed. Use Promotion, Transfer or Re-admission for movement; use the date correction action for a genuine date error.',1;
  IF EXISTS(SELECT 1 FROM dbo.LeaveCertificates WHERE EnrollmentId=@EnrollmentId)
   AND (@OldYear<>@YearId OR @OldPlacement<>@PlacementId OR @OldStart<>@StartDate OR @OldStatus<>@Status OR ISNULL(@OldLeave,'19000101')<>ISNULL(@LeaveDate,'19000101'))
   THROW 59617,'This record has a leaving certificate. Use Correct Leaving Date for its departure date; correct or reissue the certificate before changing its placement.',1;
 END;
 -- Bind the reviewed details and departure eligibility. The operator selects
 -- whether to record the departure after seeing the eligible preview.
 SET @Current=CONCAT(
  (SELECT @UserId UserId,@EnrollmentId EnrollmentId,@YearId YearId,@PlacementId PlacementId,
   @StartDate StartDate,@LeaveDate LeaveDate,@BillingStartDate BillingStartDate,@Status Status,@Reason Reason,@Legacy Legacy,
   @CanRecordDeparture CanRecordDeparture FOR JSON PATH,INCLUDE_NULL_VALUES,WITHOUT_ARRAY_WRAPPER),
  (SELECT * FROM dbo.Enrollments WHERE AdmissionId=@AdmissionId ORDER BY EnrollmentId FOR JSON PATH,INCLUDE_NULL_VALUES),
  (SELECT * FROM dbo.StudentFeeAssignments WHERE AdmissionId=@AdmissionId ORDER BY StudentFeeAssignmentId FOR JSON PATH,INCLUDE_NULL_VALUES),
  (SELECT * FROM dbo.BillingCharges WHERE AdmissionId=@AdmissionId ORDER BY BillingChargeId FOR JSON PATH,INCLUDE_NULL_VALUES),
  (SELECT * FROM dbo.FeePayments WHERE AdmissionId=@AdmissionId ORDER BY FeePaymentId FOR JSON PATH,INCLUDE_NULL_VALUES),
  (SELECT * FROM dbo.FeePaymentAllocations WHERE AdmissionId=@AdmissionId ORDER BY FeePaymentAllocationId FOR JSON PATH,INCLUDE_NULL_VALUES),
  (SELECT * FROM dbo.LeaveCertificates WHERE AdmissionId=@AdmissionId ORDER BY LeaveRecordId FOR JSON PATH,INCLUDE_NULL_VALUES),
  (SELECT * FROM dbo.Students WHERE AdmissionId=@AdmissionId FOR JSON PATH,INCLUDE_NULL_VALUES),
  (SELECT * FROM dbo.AcademicYears WHERE AcademicYearId=@YearId FOR JSON PATH,INCLUDE_NULL_VALUES),
  (SELECT * FROM dbo.CampusClassSections WHERE ClassSectionId=@PlacementId FOR JSON PATH,INCLUDE_NULL_VALUES),
  (SELECT * FROM dbo.Classes ORDER BY ClassId FOR JSON PATH,INCLUDE_NULL_VALUES),
  (SELECT * FROM dbo.AcademicPrograms ORDER BY AcademicProgramId FOR JSON PATH,INCLUDE_NULL_VALUES));
 SET @Snapshot=CONVERT(varchar(64),HASHBYTES('SHA2_256',@Current),2);
 DECLARE @BillingFrom date=COALESCE(@BillingStartDate,@StartDate);
 DECLARE @FromMonth date=DATEFROMPARTS(YEAR(@BillingFrom),MONTH(@BillingFrom),1),@ThroughMonth date;
 DECLARE @LastDate date=CASE WHEN @YearEnd IS NULL THEN @LeaveDate WHEN @LeaveDate IS NULL THEN @YearEnd WHEN @LeaveDate<@YearEnd THEN @LeaveDate ELSE @YearEnd END;
 -- Promotion closes the previous enrollment at the start of the next one.
 IF @Status=N'Promoted' AND @LeaveDate IS NOT NULL AND (@LastDate IS NULL OR @LeaveDate<=@LastDate) SET @LastDate=DATEADD(day,-1,@LeaveDate);
 IF @LastDate IS NOT NULL SET @ThroughMonth=DATEFROMPARTS(YEAR(@LastDate),MONTH(@LastDate),1);
 DECLARE @Affected TABLE(Id bigint PRIMARY KEY,Amount decimal(18,2),Blocked bit);
 INSERT @Affected
 SELECT B.BillingChargeId,B.NetAmount,CASE WHEN B.Status<>N'Open' OR EXISTS(
  SELECT 1 FROM dbo.FeePaymentAllocations A JOIN dbo.FeePayments P ON P.FeePaymentId=A.FeePaymentId
  WHERE A.BillingChargeId=B.BillingChargeId AND P.Status=N'Completed' AND A.Amount>0) THEN 1 ELSE 0 END
 FROM dbo.BillingCharges B WHERE B.EnrollmentId=@EnrollmentId AND B.Status<>N'Cancelled'
  AND (B.BillingMonth<@FromMonth OR (@ThroughMonth IS NOT NULL AND B.BillingMonth>@ThroughMonth));
 DECLARE @Cancelled int=(SELECT COUNT(*) FROM @Affected WHERE Blocked=0),@Blocked int=(SELECT COUNT(*) FROM @Affected WHERE Blocked=1),
  @Amount decimal(18,2)=(SELECT COALESCE(SUM(Amount),0) FROM @Affected WHERE Blocked=0);
 IF @Preview=1
 BEGIN
  SELECT @Snapshot Snapshot,@Cancelled CancelCount,@Amount CancelledAmount,@Blocked BlockedCount,
   (SELECT COUNT(*) FROM dbo.BillingCharges WHERE EnrollmentId=@EnrollmentId AND Status<>N'Cancelled')-@Cancelled-@Blocked RetainedCharges,
   (SELECT COUNT(*) FROM dbo.StudentFeeAssignments WHERE EnrollmentId=@EnrollmentId) FeeAssignments,
   @CanRecordDeparture CanRecordDeparture;
  COMMIT; RETURN;
 END;
 IF @ExpectedSnapshot IS NULL OR @ExpectedSnapshot<>@Snapshot THROW 59618,'Records changed after review. Review the correction again; nothing was applied.',1;
 IF @Blocked>0 THROW 59619,'Affected charges have payments. Resolve them in Student Fee Account > Payment History, then review this correction again.',1;
 IF @Cancelled>0 EXEC dbo.RequirePermission @UserId,N'Fees.Collect';
 DECLARE @Charge bigint;
 DECLARE charges CURSOR LOCAL FAST_FORWARD FOR SELECT Id FROM @Affected WHERE Blocked=0 ORDER BY Id;
 OPEN charges; FETCH NEXT FROM charges INTO @Charge;
 WHILE @@FETCH_STATUS=0
 BEGIN
  DECLARE @Reversed TABLE(BillingChargeId bigint,AdmissionId int,FeeTypeName nvarchar(200),BillingMonth date,AmountReversed decimal(18,2),Reason nvarchar(500),Status nvarchar(40));
  INSERT @Reversed EXEC dbo.ReverseBillingCharge @UserId,@Charge,@Reason;
  FETCH NEXT FROM charges INTO @Charge;
 END;
 CLOSE charges; DEALLOCATE charges;
 IF @EnrollmentId=0
 BEGIN
  INSERT dbo.Enrollments(AdmissionId,AcademicYearId,ClassSectionId,EnrollmentDate,LeavingDate,BillingStartDate,Status,BillingReady,UsesTransport,Remarks)
   VALUES(@AdmissionId,@YearId,@PlacementId,@StartDate,@LeaveDate,@BillingStartDate,@Status,0,0,@Reason);
  SET @EnrollmentId=CONVERT(int,SCOPE_IDENTITY());
 END
 ELSE
 BEGIN
  UPDATE dbo.Enrollments SET AcademicYearId=@YearId,ClassSectionId=@PlacementId,EnrollmentDate=@StartDate,
   LeavingDate=@LeaveDate,BillingStartDate=@BillingStartDate,Status=@Status,UpdatedAt=SYSDATETIME(),Remarks=@Reason
  WHERE EnrollmentId=@EnrollmentId;
 END;
 INSERT dbo.EnrollmentHistory(EnrollmentId,AdmissionId,ActionType,ActionDate,OldClassSectionId,NewClassSectionId,OldStatus,NewStatus,Remarks,UserId)
 VALUES(@EnrollmentId,@AdmissionId,N'CORRECTION',@StartDate,@OldPlacement,@PlacementId,@OldStatus,@Status,@Reason,@UserId);
 IF @RecordHistoricalDeparture=1
 BEGIN
  UPDATE dbo.Students SET Status=N'Left',WithdrawalDate=@LeaveDate,UpdatedAt=SYSDATETIME() WHERE AdmissionId=@AdmissionId;
  INSERT dbo.StudentStatusHistory(AdmissionId,OldStatus,NewStatus,Reason,EffectiveDate,Remarks,CreatedBy)
  VALUES(@AdmissionId,@StudentStatus,N'Left',LEFT(@Reason,250),@LeaveDate,@Reason,@UserId);
 END;
 -- Synchronize student status only when this is their latest enrollment.
 IF @CreatingHistory=0 AND NOT EXISTS(SELECT 1 FROM dbo.Enrollments WHERE AdmissionId=@AdmissionId AND EnrollmentId<>@EnrollmentId AND EnrollmentDate>@StartDate)
  AND @Status IN(N'Active',N'Left')
 BEGIN
  UPDATE dbo.Students SET Status=@Status,WithdrawalDate=CASE WHEN @Status=N'Left' THEN @LeaveDate ELSE NULL END WHERE AdmissionId=@AdmissionId;
 END;
 COMMIT;
 SELECT @EnrollmentId EnrollmentId,@Cancelled CancelledCharges;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK;
 THROW;
END CATCH;
