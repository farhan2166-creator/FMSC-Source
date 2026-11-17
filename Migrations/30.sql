-- Record the actual attempt actor and authenticate the worker at reservation.
SET NOCOUNT ON;SET XACT_ABORT ON;
IF EXISTS(SELECT 1 FROM dbo.DatabaseVersion WHERE TRY_CONVERT(int,VersionNumber)=30) RETURN;
IF ISNULL((SELECT TOP(1) TRY_CONVERT(int,VersionNumber) FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0)<>29
 THROW 51000,'Communication actor evidence requires the preceding migration.',1;
BEGIN TRY
 BEGIN TRANSACTION;
 ALTER TABLE msg.Attempt ADD ClaimedBy int NULL,ClaimKind varchar(12) NULL,WorkerEnrollmentId uniqueidentifier NULL;
 ALTER TABLE msg.BackgroundWorkerEnrollment ADD RestoreBoundaryHash binary(32) NULL;
 EXEC(N'ALTER TABLE msg.Attempt ADD CONSTRAINT CK_MsgAttemptActor
 CHECK((ClaimedBy IS NULL AND ClaimKind IS NULL AND WorkerEnrollmentId IS NULL)
  OR (ClaimedBy IS NOT NULL AND ClaimedBy>0 AND ClaimKind IS NOT NULL
   AND ((ClaimKind=''Interactive'' AND WorkerEnrollmentId IS NULL)
      OR (ClaimKind=''Worker'' AND WorkerEnrollmentId IS NOT NULL))))');
 ALTER TABLE msg.Attempt ADD CONSTRAINT FK_MsgAttemptWorkerEnrollment FOREIGN KEY(WorkerEnrollmentId)
  REFERENCES msg.BackgroundWorkerEnrollment(EnrollmentId);
 -- Leave earlier actors unknown; the original approver is not proof of who
 -- reserved a provider attempt. New claims record identity atomically.
EXEC(N'CREATE OR ALTER PROCEDURE msg.RegisterBackgroundWorker
 @UserId int,@EnrollmentId uniqueidentifier,@WindowsSid nvarchar(185),@SecretHash varbinary(32)
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 IF NOT EXISTS(SELECT 1 FROM dbo.Users U JOIN dbo.Roles O ON O.RoleId=U.RoleId
   WHERE U.UserId=@UserId AND O.RoleName=N''Administrator'')
 AND NOT EXISTS(SELECT 1 FROM dbo.UserRoles UR JOIN dbo.Roles O ON O.RoleId=UR.RoleId
   WHERE UR.UserId=@UserId AND O.RoleName=N''Administrator'')
   THROW 51260,''An active school administrator must authorize background sending.'',1;
 IF @EnrollmentId IS NULL OR @EnrollmentId=''00000000-0000-0000-0000-000000000000''
    OR @WindowsSid NOT LIKE N''S-1-%'' OR LEN(@WindowsSid)>185
    OR DATALENGTH(@SecretHash)<>32
    THROW 51260,''Invalid background processor enrollment.'',1;
 BEGIN TRANSACTION;
 UPDATE msg.BackgroundWorkerEnrollment
 SET Enabled=0,RevokedUtc=SYSUTCDATETIME()
 WHERE WindowsSid=@WindowsSid AND Enabled=1;
 INSERT msg.BackgroundWorkerEnrollment(EnrollmentId,UserId,WindowsSid,SecretHash,RestoreBoundaryHash)
 VALUES(@EnrollmentId,@UserId,@WindowsSid,@SecretHash,msg.RestoreBoundary());
 -- Existing schedules may originate before the verified enrollment (including
 -- after a previous restore). Never start dispatching them silently.
 UPDATE S SET HoldForReview=1,UpdatedBy=@UserId,UpdatedUtc=SYSUTCDATETIME()
 FROM msg.BatchSchedule S JOIN msg.Batch B ON B.BatchId=S.BatchId
 WHERE B.CreatedUtc<SYSUTCDATETIME() AND
       EXISTS(SELECT 1 FROM msg.Recipient R WHERE R.BatchId=S.BatchId AND R.State=''Pending'');
 UPDATE msg.DispatchControl SET UnattendedEnabled=0,UpdatedBy=@UserId,
  UpdatedUtc=SYSUTCDATETIME() WHERE ControlId=1;
 COMMIT;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.VerifyBackgroundWorker
 @UserId int,@EnrollmentId uniqueidentifier,@WindowsSid nvarchar(185),@SecretHash varbinary(32),@ReturnState bit=1
AS
BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 DECLARE @Created datetime2(3),@EnrolledBoundary binary(32),@CurrentBoundary binary(32);
 SELECT @Created=EnrolledUtc,@EnrolledBoundary=RestoreBoundaryHash FROM msg.BackgroundWorkerEnrollment WITH(HOLDLOCK)
 WHERE EnrollmentId=@EnrollmentId AND UserId=@UserId AND WindowsSid=@WindowsSid
 AND SecretHash=@SecretHash AND Enabled=1;
 IF @Created IS NULL
  THROW 51261,''Background worker authorization expired or was revoked.'',1;
 IF NOT EXISTS(SELECT 1 FROM dbo.Users U JOIN dbo.Roles O ON O.RoleId=U.RoleId
     WHERE U.UserId=@UserId AND O.RoleName=N''Administrator'')
 AND NOT EXISTS(SELECT 1 FROM dbo.UserRoles UR JOIN dbo.Roles O ON O.RoleId=UR.RoleId
   WHERE UR.UserId=@UserId AND O.RoleName=N''Administrator'')
  THROW 51261,''Background worker school administrator access was revoked.'',1;
 IF NOT EXISTS(SELECT 1 FROM msg.DispatchControl
  WHERE ControlId=1 AND UnattendedEnabled=1 AND MasterPaused=0)
  THROW 51261,''Unattended sending is not currently enabled.'',1;
 -- A restore changes the independently retained event identity. No local
 -- timezone conversion or re-enrollment can silently revive old approvals.
 BEGIN TRY
  SET @CurrentBoundary=msg.RestoreBoundary();
 END TRY
 BEGIN CATCH
  THROW 51262,''Database recovery history could not be verified. Unattended sending is blocked.'',1;
 END CATCH;
 IF @EnrolledBoundary<>@CurrentBoundary
  OR (@EnrolledBoundary IS NULL AND @CurrentBoundary IS NOT NULL)
  OR (@EnrolledBoundary IS NOT NULL AND @CurrentBoundary IS NULL)
  THROW 51262,''This school database was restored after worker enrollment. Reauthorize sending after review.'',1;
 IF @ReturnState IS NULL THROW 51261,''Invalid worker authorization request.'',1;
 IF @ReturnState=1
 SELECT CONVERT(bit,1) Authorized,
  ISNULL((SELECT MAX(AttemptId) FROM msg.Attempt),CONVERT(bigint,0)) MaxAttemptId;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ClaimRecipient
    @UserId int,
    @RecipientId bigint,
    @AttemptKey uniqueidentifier,
    @ExpectedProviderCode varchar(40),@IsWorker bit=0,
    @EnrollmentId uniqueidentifier=NULL,@WindowsSid nvarchar(185)=NULL,
    @SecretHash varbinary(32)=NULL,@LeaseNonce uniqueidentifier=NULL
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
    IF @RecipientId IS NULL OR @RecipientId<=0
       OR @AttemptKey IS NULL OR @AttemptKey=''00000000-0000-0000-0000-000000000000''
       OR NULLIF(LTRIM(RTRIM(@ExpectedProviderCode)),'''') IS NULL
        THROW 51090,''A valid recipient, attempt key and approved provider are required.'',1;

    BEGIN TRANSACTION;

    IF @IsWorker IS NULL OR (@IsWorker=0 AND (@EnrollmentId IS NOT NULL
      OR @WindowsSid IS NOT NULL OR @SecretHash IS NOT NULL OR @LeaseNonce IS NOT NULL))
      THROW 51261,''Choose one authenticated interactive or worker claim.'',1;
    IF @IsWorker=1
    BEGIN
      IF @EnrollmentId IS NULL OR @WindowsSid IS NULL OR @SecretHash IS NULL OR @LeaseNonce IS NULL
        THROW 51261,''Complete background processor proof is required at claim.'',1;
      EXEC msg.VerifyBackgroundWorker @UserId,@EnrollmentId,@WindowsSid,@SecretHash,@ReturnState=0;
      IF NOT EXISTS(SELECT 1 FROM msg.BackgroundWorkerLease WITH(UPDLOCK,HOLDLOCK)
        WHERE LeaseId=1 AND EnrollmentId=@EnrollmentId AND LeaseNonce=@LeaseNonce AND LeaseUntilUtc>SYSUTCDATETIME())
        THROW 51263,''The background processor lease expired or belongs to another invocation.'',1;
    END;

    -- The global stop and reviewed due date are authoritative at the same
    -- transaction boundary that reserves an external provider attempt.
    IF EXISTS(SELECT 1 FROM msg.DispatchControl WITH(UPDLOCK,HOLDLOCK)
              WHERE ControlId=1 AND MasterPaused=1)
        THROW 51240,''Communication sending is paused by the administrator.'',1;
    IF EXISTS(
        SELECT 1 FROM msg.BatchSchedule S WITH(UPDLOCK,HOLDLOCK)
        JOIN msg.Recipient R WITH(HOLDLOCK) ON R.BatchId=S.BatchId
        WHERE R.RecipientId=@RecipientId AND
             (S.DueUtc>SYSUTCDATETIME()
              OR S.HoldForReview=1
              OR (S.ExpiresUtc IS NOT NULL AND S.ExpiresUtc<=SYSUTCDATETIME()))
    )
        THROW 51241,''This batch is scheduled, expired or awaiting review.'',1;

    DECLARE @BatchId uniqueidentifier,@Kind varchar(8),@AdmissionId int,@StaffId int,
        @SnapshotPhone varchar(16),@RecipientState varchar(12),@BatchState varchar(14),
        @Channel varchar(10),@Purpose varchar(20),@Body nvarchar(1600),
        @Audience varchar(20),@CampusId int,@ClassId int,@StudentId int,@TargetStaffId int,
        @ProviderCode varchar(40),@CurrentPhone varchar(16),@Exclusion varchar(32),
        @AttemptId bigint;

    -- This UPDLOCK serializes competing workers before any attempt is created.
    SELECT @BatchId=R.BatchId,@Kind=R.RecipientKind,@AdmissionId=R.AdmissionId,
        @StaffId=R.StaffId,@SnapshotPhone=R.PhoneE164,@RecipientState=R.State,
        @BatchState=B.State,@Channel=B.Channel,@Purpose=B.Purpose,@Body=B.Body,
        @Audience=B.Audience,@CampusId=B.CampusId,@ClassId=B.ClassId,
        @StudentId=CASE WHEN B.Audience=''OutstandingFees'' AND B.SingleStudentScope=0 THEN 0 ELSE B.StudentId END,@TargetStaffId=B.StaffId
    FROM msg.Recipient R WITH(UPDLOCK,HOLDLOCK)
    JOIN msg.Batch B WITH(HOLDLOCK) ON B.BatchId=R.BatchId
    WHERE R.RecipientId=@RecipientId;
    IF @BatchId IS NULL OR @BatchState NOT IN (''Confirmed'',''Sending'')
       OR @RecipientState<>''Pending''
        THROW 51093,''Recipient is not pending. Never resend an uncertain attempt.'',1;


    -- Scheduled work cannot outlive the originating sender''s authority.
    -- Explicit administrator takeover remains separate from original approval.
    IF EXISTS(SELECT 1 FROM msg.BatchSchedule WHERE BatchId=@BatchId)
    BEGIN
        DECLARE @OriginalSenderId int,@StartHour time(0),@EndHour time(0),
                @Zone sysname,@LocalTime datetimeoffset,@LocalDate date,@IsoDay tinyint;
        SELECT @OriginalSenderId=COALESCE(Takeover.NewOwnerId,B.CreatedBy)
        FROM msg.Batch B
        OUTER APPLY (SELECT TOP(1) T.NewOwnerId FROM msg.BatchTakeover T
           WHERE T.BatchId=B.BatchId ORDER BY T.TakeoverId DESC) Takeover
        WHERE B.BatchId=@BatchId;
        EXEC dbo.RequirePermission @OriginalSenderId,N''Communication.Send'';
        IF @Kind=''Staff''
            EXEC dbo.RequirePermission @OriginalSenderId,N''Staff.View'';
        ELSE
            EXEC dbo.RequirePermission @OriginalSenderId,N''Students.View'';
        IF @Audience IN (''OutstandingFees'',''FeeStudent'',''FeeFamily'')
            EXEC dbo.RequirePermission @OriginalSenderId,N''Fees.View'';
        SELECT @StartHour=WindowStart,@EndHour=WindowEnd,@Zone=TimeZoneId
        FROM msg.DispatchControl WITH(HOLDLOCK) WHERE ControlId=1;
        SET @LocalTime=(SYSUTCDATETIME() AT TIME ZONE ''UTC'') AT TIME ZONE @Zone;
        SET @LocalDate=CONVERT(date,@LocalTime);
        SET @IsoDay=CONVERT(tinyint,((DATEDIFF(day,''19000101'',@LocalDate)%7)+7)%7+1);
        IF CONVERT(time(0),@LocalTime)<@StartHour OR CONVERT(time(0),@LocalTime)>=@EndHour
          OR EXISTS(SELECT 1 FROM msg.NonWorkingWeekday WHERE IsoWeekday=@IsoDay AND Enabled=0)
          OR EXISTS(SELECT 1 FROM msg.SchoolHoliday WHERE HolidayDate=@LocalDate)
          THROW 51251,''Scheduled sending is outside approved school hours or working days.'',1;
    END;

    IF @Kind=''Staff''
        EXEC dbo.RequirePermission @UserId,N''Staff.View'';
    ELSE
        EXEC dbo.RequirePermission @UserId,N''Students.View'';
    IF @Audience IN (''OutstandingFees'',''FeeStudent'',''FeeFamily'')
        EXEC dbo.RequirePermission @UserId,N''Fees.View'';

    SELECT @ProviderCode=C.ProviderCode FROM msg.ChannelSettings C WITH(HOLDLOCK)
    WHERE C.Channel=@Channel AND C.Enabled=1;
    IF @ProviderCode IS NULL OR @ProviderCode<>@ExpectedProviderCode
        THROW 51092,''Channel is paused or runtime provider does not match the approved profile.'',1;

    -- Re-read live records while locks protect the verification transaction.
    -- The persisted scope prevents a campus/class/fee/family target from silently
    -- becoming eligible just because an old batch retained a valid phone.
    IF @Kind=''Staff''
    BEGIN
        IF @Audience=''Staff'' AND @TargetStaffId=@StaffId
            SELECT @CurrentPhone=msg.NormalizeMobile(S.ContactNumber)
            FROM dbo.Staff S WITH(HOLDLOCK)
            WHERE S.StaffId=@StaffId AND S.Status=N''Active'';
    END
    ELSE IF @Audience IN (''Student'',''Family'',''FeeStudent'',''FeeFamily'')
    BEGIN
        SELECT @CurrentPhone=G.PhoneE164 FROM msg.RecordGuardian(@StudentId) G
        WHERE G.AdmissionId=@AdmissionId AND G.HasConsent=1;
    END
    ELSE
    BEGIN
        SELECT @CurrentPhone=Guardian.PhoneE164
        FROM dbo.Students S WITH(HOLDLOCK)
        OUTER APPLY msg.RecordGuardian(S.AdmissionId) Guardian
        OUTER APPLY (
            SELECT TOP(1) E.ClassSectionId,E.Status
            FROM dbo.Enrollments E WITH(HOLDLOCK)
            WHERE E.AdmissionId=S.AdmissionId
            ORDER BY CASE WHEN E.Status=N''Active'' THEN 0 ELSE 1 END,E.EnrollmentId DESC
        ) E
        LEFT JOIN dbo.CampusClassSections CCS WITH(HOLDLOCK)
            ON CCS.ClassSectionId=E.ClassSectionId
        OUTER APPLY (
            SELECT SUM(dbo.GetFeeChargeBalance(BC.BillingChargeId)) Outstanding
            FROM dbo.BillingCharges BC WITH(HOLDLOCK)
            WHERE @Audience=''OutstandingFees''
              AND BC.AdmissionId=S.AdmissionId AND BC.Status<>N''Cancelled''
        ) F
        WHERE S.AdmissionId=@AdmissionId AND S.Status=N''Active''
          AND Guardian.HasConsent=1
          AND (@Audience=''School''
            OR (@Audience=''Campus'' AND E.Status=N''Active'' AND CCS.CampusId=@CampusId)
            OR (@Audience=''Class'' AND E.Status=N''Active''
                AND CCS.CampusId=@CampusId AND CCS.ClassId=@ClassId)
            OR (@Audience=''OutstandingFees'' AND E.Status=N''Active''
                AND (@CampusId=0 OR CCS.CampusId=@CampusId)
                AND (@ClassId=0 OR CCS.ClassId=@ClassId)
                AND (@StudentId=0 OR S.AdmissionId=@StudentId) AND F.Outstanding>0));
    END;

    SET @Exclusion=CASE
        WHEN NOT EXISTS(SELECT 1 FROM msg.PreparedEventEligible(@BatchId)) THEN ''SourceChanged''
        WHEN @CurrentPhone IS NULL THEN ''NoLongerEligible''
        WHEN @CurrentPhone<>@SnapshotPhone THEN ''DestinationChanged''
        WHEN EXISTS(SELECT 1 FROM msg.Suppression O WITH(HOLDLOCK)
                    WHERE O.Channel=@Channel AND O.PhoneE164=@CurrentPhone)
            THEN ''ChannelOptOut''
        ELSE NULL END;
    IF @Exclusion IS NOT NULL
    BEGIN
        UPDATE msg.Recipient
        SET State=''Excluded'',ExclusionCode=@Exclusion,UpdatedUtc=SYSUTCDATETIME()
        WHERE RecipientId=@RecipientId;
        -- No provider call may occur after an excluded result.
        SELECT CONVERT(bit,0) Claimed,CONVERT(bigint,NULL) AttemptId,
               CONVERT(uniqueidentifier,NULL) AttemptKey,
               CONVERT(varchar(16),NULL) PhoneE164,
               CONVERT(varchar(10),NULL) Channel,
               CONVERT(varchar(40),NULL) ProviderCode,
               CONVERT(nvarchar(1600),NULL) Body,@Exclusion ExclusionCode;
        COMMIT TRANSACTION; RETURN;
    END;

    -- All routine student/family fee and announcement messages respect the
    -- same school-day ceiling, even when different staff or devices reserve
    -- different siblings simultaneously. The local app lock is transaction-
    -- owned; a crash cannot release it before the SQL attempt is persisted.
    IF @Kind=''Student'' AND @Purpose IN (''Announcement'',''FeeReminder'')
    BEGIN
       DECLARE @FamilyForRate int;
       SELECT TOP(1) @FamilyForRate=FM.FamilyId
       FROM dbo.FamilyMembers FM WITH(HOLDLOCK)
       WHERE FM.AdmissionId=@AdmissionId ORDER BY FM.FamilyId;
       DECLARE @RateLock int,@RateResource nvarchar(255)=
          CASE WHEN @FamilyForRate IS NULL
             THEN CONCAT(N''FMSC_msg_daily_student_'',@AdmissionId)
             ELSE CONCAT(N''FMSC_msg_daily_family_'',@FamilyForRate) END;
       EXEC @RateLock=sys.sp_getapplock @Resource=@RateResource,
          @LockMode=''Exclusive'',@LockOwner=''Transaction'',@LockTimeout=10000;
       IF @RateLock<0
          THROW 51293,''Family routine sending is busy. No provider request was created.'',1;
       IF NOT EXISTS(SELECT 1 FROM msg.RoutineFamilyDailyEligible(
           @AdmissionId,SYSUTCDATETIME()))
          THROW 51294,''This family reached the daily routine message limit. Try the next school day after review.'',1;
    END;

    DECLARE @Ordinal int;
    SELECT @Ordinal=COUNT(*)+1 FROM msg.Attempt WITH(HOLDLOCK) WHERE RecipientId=@RecipientId;
    IF @Ordinal>3 OR EXISTS(
        SELECT 1 FROM msg.Attempt WITH(HOLDLOCK)
        WHERE RecipientId=@RecipientId AND (Outcome<>''Failed'' OR RetryApprovedBy IS NULL))
        THROW 51093,''The prior attempt requires definitive failure and explicit retry approval.'',1;

    -- Quota rows serialize every reservation through the same provider.
    -- A reserved attempt consumes capacity even if its result later becomes
    -- Unknown: never reuse that allowance to create a duplicate request.
    DECLARE @Limit int,@Reserve int,@MinuteLimit int,@Priority varchar(10),
            @NowUtc datetime2(3)=SYSUTCDATETIME();
    SELECT @Limit=DailyLimit,@Reserve=ReservedImportant
      FROM msg.ProviderQuota WITH(UPDLOCK,HOLDLOCK)
      WHERE ProviderCode=@ExpectedProviderCode;
    IF @Limit IS NULL AND EXISTS(
        SELECT 1 FROM msg.Recipient R JOIN msg.BatchSchedule S ON S.BatchId=R.BatchId
        WHERE R.RecipientId=@RecipientId)
        THROW 51248,''A scheduled API batch requires a configured daily provider limit.'',1;
    SELECT @MinuteLimit=MaxPerMinute FROM msg.DispatchControl WITH(HOLDLOCK) WHERE ControlId=1;
    SELECT @Priority=COALESCE(S.Priority,''Normal'')
      FROM msg.Recipient R JOIN msg.Batch B ON B.BatchId=R.BatchId
      LEFT JOIN msg.BatchSchedule S ON S.BatchId=B.BatchId
      WHERE R.RecipientId=@RecipientId;
    IF @Limit IS NOT NULL
    BEGIN
    DECLARE @TodayUtc datetime2(3)=CONVERT(datetime2(3),CONVERT(date,@NowUtc));
    DECLARE @Used bigint=(
      SELECT COUNT_BIG(*) FROM msg.Attempt T WITH(HOLDLOCK)
      WHERE T.ProviderCode=@ExpectedProviderCode AND
       T.RequestUtc>=@TodayUtc AND T.RequestUtc<DATEADD(day,1,@TodayUtc));
    IF @Used>=@Limit OR (@Priority<>''High'' AND @Used>=@Limit-@Reserve)
        THROW 51249,''The provider daily allowance is exhausted or reserved for priority messages.'',1;
    IF (SELECT COUNT_BIG(*) FROM msg.Attempt T WITH(HOLDLOCK)
        WHERE T.ProviderCode=@ExpectedProviderCode AND
          T.RequestUtc>DATEADD(minute,-1,@NowUtc)) >= @MinuteLimit
        THROW 51250,''Provider sending speed limit reached. Retry a pending recipient later.'',1;
    END;

    -- Reserve the attempt BEFORE HTTP. Unknown is deliberately conservative:
    -- a crash/timeout must never make this recipient eligible for blind replay.
    INSERT msg.Attempt(RecipientId,AttemptNumber,AttemptKey,Channel,ProviderCode,Outcome,ClaimedBy,ClaimKind,WorkerEnrollmentId)
    VALUES(@RecipientId,@Ordinal,@AttemptKey,@Channel,@ProviderCode,''Unknown'',@UserId,CASE WHEN @IsWorker=1 THEN ''Worker'' ELSE ''Interactive'' END,@EnrollmentId);
    SET @AttemptId=CONVERT(bigint,SCOPE_IDENTITY());
    UPDATE msg.Recipient
    SET State=''Unknown'',UpdatedUtc=SYSUTCDATETIME()
    WHERE RecipientId=@RecipientId;
    UPDATE msg.Batch SET State=''Sending''
    WHERE BatchId=@BatchId AND State=''Confirmed'';
    SELECT CONVERT(bit,1) Claimed,@AttemptId AttemptId,@AttemptKey AttemptKey,
           @CurrentPhone PhoneE164,@Channel Channel,@ProviderCode ProviderCode,
           @Body Body,CONVERT(varchar(32),NULL) ExclusionCode;
    COMMIT TRANSACTION;
END');


 EXEC(N'CREATE OR ALTER TRIGGER msg.TR_Attempt_ActorImmutable ON msg.Attempt AFTER UPDATE
 AS BEGIN
  SET NOCOUNT ON;
  IF EXISTS(SELECT AttemptId,ClaimedBy,ClaimKind,WorkerEnrollmentId FROM inserted
    EXCEPT SELECT AttemptId,ClaimedBy,ClaimKind,WorkerEnrollmentId FROM deleted)
   THROW 51344,''An attempt actor cannot be rewritten by later status updates.'',1;
 END;');
 EXEC(N'CREATE OR ALTER TRIGGER msg.TR_Worker_RestoreBoundaryImmutable ON msg.BackgroundWorkerEnrollment AFTER UPDATE
 AS BEGIN
  SET NOCOUNT ON;
  IF EXISTS(SELECT EnrollmentId,RestoreBoundaryHash FROM inserted
    EXCEPT SELECT EnrollmentId,RestoreBoundaryHash FROM deleted)
   THROW 51344,''A processor enrollment cannot be reassigned to another recovery boundary.'',1;
 END;');
 EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadDispatchEvidence
 @UserId int,@RecipientId bigint
 AS BEGIN
  SET NOCOUNT ON;
  EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
  DECLARE @Audience varchar(20),@Owner int,@Scope uniqueidentifier;
  SELECT @Audience=B.Audience,@Owner=B.CreatedBy FROM msg.Batch B
    JOIN msg.Recipient R ON R.BatchId=B.BatchId WHERE R.RecipientId=@RecipientId;
  IF @Audience IS NULL THROW 51132,''The communication recipient does not exist.'',1;
  IF @Owner<>@UserId EXEC dbo.RequirePermission @UserId,N''Communication.View'';
  IF @Audience=''Staff'' EXEC dbo.RequirePermission @UserId,N''Staff.View'';
  ELSE EXEC dbo.RequirePermission @UserId,N''Students.View'';
  IF @Audience IN (''OutstandingFees'',''FeeStudent'',''FeeFamily'') EXEC dbo.RequirePermission @UserId,N''Fees.View'';
  SELECT @Scope=TRY_CONVERT(uniqueidentifier,value) FROM sys.extended_properties
    WHERE class=0 AND name=N''FMSC_QuickRegistrationScope'';
  IF @Scope IS NULL OR @Scope=''00000000-0000-0000-0000-000000000000''
    THROW 51340,''The school recovery identity is missing. Do not send before database verification.'',1;
  SELECT @Scope SchoolScope,B.ClientRequestId,R.RecipientKind,COALESCE(R.AdmissionId,R.StaffId) RecordId,
    R.PhoneE164,B.Channel,A.ProviderCode,A.ProfileHash
  FROM msg.Batch B JOIN msg.ApiBatch A ON A.BatchId=B.BatchId
  JOIN msg.Recipient R ON R.BatchId=B.BatchId WHERE R.RecipientId=@RecipientId;
  SELECT AttemptId,AttemptKey,Channel,ProviderCode,ClaimedBy,ClaimKind,WorkerEnrollmentId
  FROM msg.Attempt WHERE RecipientId=@RecipientId ORDER BY AttemptNumber;
 END;');

 INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
 VALUES(N'30',SYSDATETIME(),SUSER_SNAME(),N'Immutable communication attempt actor and enrolled worker lease authorization');
 COMMIT;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK;
 THROW;
END CATCH;
