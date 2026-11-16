-- Communication Centre upgrade: fail-closed queue scheduling and configuration foundation.
-- No background process is authorized by this migration. All automation begins Off.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF EXISTS(SELECT 1 FROM dbo.DatabaseVersion WHERE TRY_CONVERT(int,VersionNumber)=23) RETURN;
IF ISNULL((SELECT TOP(1) TRY_CONVERT(int,VersionNumber)
 FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0)<>22
 THROW 51200,'Communication upgrade requires schema 22.',1;

CREATE TABLE msg.DispatchControl(
 ControlId tinyint NOT NULL CONSTRAINT PK_MsgDispatchControl PRIMARY KEY,
 MasterPaused bit NOT NULL CONSTRAINT DF_MsgMasterPaused DEFAULT(0),
 UnattendedEnabled bit NOT NULL CONSTRAINT DF_MsgUnattendedEnabled DEFAULT(0),
 WindowStart time(0) NOT NULL CONSTRAINT DF_MsgWindowStart DEFAULT('08:00'),
 WindowEnd time(0) NOT NULL CONSTRAINT DF_MsgWindowEnd DEFAULT('18:00'),
 TimeZoneId sysname NOT NULL CONSTRAINT DF_MsgTimeZone DEFAULT(N'Pakistan Standard Time'),
 MaxPerMinute int NOT NULL CONSTRAINT DF_MsgMaxPerMinute DEFAULT(60),
 UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_MsgControlUpdated DEFAULT SYSUTCDATETIME(),
 UpdatedBy int NULL,
 CONSTRAINT CK_MsgDispatchControlId CHECK(ControlId=1),
 CONSTRAINT CK_MsgDispatchControlHours CHECK(WindowStart<WindowEnd),
 CONSTRAINT CK_MsgDispatchControlSpeed CHECK(MaxPerMinute BETWEEN 1 AND 600)
);
INSERT msg.DispatchControl(ControlId) VALUES(1);

CREATE TABLE msg.ProviderQuota(
 Channel varchar(10) NOT NULL,
 ProviderCode varchar(40) NOT NULL,
 DailyLimit int NOT NULL,
 ReservedImportant int NOT NULL CONSTRAINT DF_MsgProviderReserve DEFAULT(0),
 UpdatedBy int NOT NULL,
 UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_MsgProviderQuotaUpdated DEFAULT SYSUTCDATETIME(),
 CONSTRAINT PK_MsgProviderQuota PRIMARY KEY(Channel,ProviderCode),
 CONSTRAINT CK_MsgProviderQuotaChannel CHECK((Channel='Sms' AND ProviderCode='Twilio')
  OR (Channel='WhatsApp' AND ProviderCode='MetaCloud')),
 CONSTRAINT CK_MsgProviderQuotaRange CHECK(DailyLimit BETWEEN 1 AND 1000000 AND
     ReservedImportant>=0 AND ReservedImportant<DailyLimit)
);
-- No default daily allowance: unattended sending needs explicit admin configuration.

CREATE TABLE msg.BatchSchedule(
 BatchId uniqueidentifier NOT NULL CONSTRAINT PK_MsgBatchSchedule PRIMARY KEY
   CONSTRAINT FK_MsgBatchScheduleBatch REFERENCES msg.Batch(BatchId),
 DueUtc datetime2(3) NOT NULL,
 ExpiresUtc datetime2(3) NULL,
 Priority varchar(10) NOT NULL CONSTRAINT DF_MsgSchedulePriority DEFAULT('Normal'),
 HoldForReview bit NOT NULL CONSTRAINT DF_MsgScheduleHold DEFAULT(0),
 UpdatedBy int NOT NULL,
 UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_MsgScheduleUpdated DEFAULT SYSUTCDATETIME(),
 CONSTRAINT CK_MsgScheduleExpires CHECK(ExpiresUtc IS NULL OR ExpiresUtc>DueUtc),
 CONSTRAINT CK_MsgSchedulePriority CHECK(Priority IN ('High','Normal','Low'))
);
CREATE INDEX IX_MsgScheduleDue ON msg.BatchSchedule(HoldForReview,DueUtc)
 INCLUDE(ExpiresUtc,Priority);

CREATE TABLE msg.AutomationRule(
 EventCode varchar(32) NOT NULL CONSTRAINT PK_MsgAutomationRule PRIMARY KEY,
 Mode varchar(10) NOT NULL CONSTRAINT DF_MsgAutomationMode DEFAULT('Off'),
 CampusId int NOT NULL CONSTRAINT DF_MsgAutomationCampus DEFAULT(0),
 ClassId int NOT NULL CONSTRAINT DF_MsgAutomationClass DEFAULT(0),
 ActivationBaselineUtc datetime2(3) NULL,
 UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_MsgAutomationUpdated DEFAULT SYSUTCDATETIME(),
 UpdatedBy int NULL,
 CONSTRAINT CK_MsgAutomationMode CHECK(Mode IN ('Off','Review','Automatic')),
 CONSTRAINT CK_MsgAutomationScope CHECK(CampusId>=0 AND ClassId>=0 AND (ClassId=0 OR CampusId>0))
);
INSERT msg.AutomationRule(EventCode) VALUES
 ('Admission'),('PaymentReceived'),('OutstandingFees'),('UpcomingDue'),
 ('OverdueFollowUp'),('Announcement'),('ExaminationResult'),('Birthday');

CREATE TABLE msg.AutomationException(
 ExceptionId bigint IDENTITY(1,1) CONSTRAINT PK_MsgAutomationException PRIMARY KEY,
 FamilyId int NOT NULL,
 EventCode varchar(32) NOT NULL CONSTRAINT FK_MsgExceptionRule
  REFERENCES msg.AutomationRule(EventCode),
 UntilUtc datetime2(3) NULL,
 Reason nvarchar(200) NOT NULL,
 CreatedBy int NOT NULL,
 CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_MsgExceptionCreated DEFAULT SYSUTCDATETIME(),
 RevokedBy int NULL,RevokedUtc datetime2(3) NULL,
 CONSTRAINT CK_MsgAutomationExceptionFamily CHECK(FamilyId>0),
 CONSTRAINT CK_MsgAutomationExceptionReason CHECK(LEN(LTRIM(RTRIM(Reason)))>0)
);
CREATE INDEX IX_MsgAutomationExceptionActive ON msg.AutomationException(FamilyId,EventCode,RevokedUtc)
 INCLUDE(UntilUtc);

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadDispatchControl @UserId int
AS
BEGIN
  SET NOCOUNT ON;
  EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
  SELECT ControlId,MasterPaused,UnattendedEnabled,WindowStart,WindowEnd,
         TimeZoneId,MaxPerMinute,UpdatedUtc,UpdatedBy
  FROM msg.DispatchControl WHERE ControlId=1;
  SELECT Channel,ProviderCode,DailyLimit,ReservedImportant,UpdatedUtc
  FROM msg.ProviderQuota ORDER BY Channel,ProviderCode;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.SaveDispatchControl
  @UserId int,@UnattendedEnabled bit,@WindowStart time(0),@WindowEnd time(0),
  @TimeZoneId sysname,@MaxPerMinute int
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 IF @WindowStart IS NULL OR @WindowEnd IS NULL OR @WindowStart>=@WindowEnd
    OR @MaxPerMinute NOT BETWEEN 1 AND 600
    OR NOT EXISTS(SELECT 1 FROM sys.time_zone_info WHERE name=@TimeZoneId)
    THROW 51242,''Invalid communication sending window or maximum speed.'',1;
 UPDATE msg.DispatchControl
 SET UnattendedEnabled=@UnattendedEnabled,WindowStart=@WindowStart,WindowEnd=@WindowEnd,
     TimeZoneId=@TimeZoneId,MaxPerMinute=@MaxPerMinute,UpdatedBy=@UserId,
     UpdatedUtc=SYSUTCDATETIME()
 WHERE ControlId=1;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.SetMasterPause @UserId int,@Paused bit
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 IF @Paused IS NULL THROW 51243,''Choose pause or resume.'',1;
 UPDATE msg.DispatchControl SET MasterPaused=@Paused,
  UpdatedBy=@UserId,UpdatedUtc=SYSUTCDATETIME() WHERE ControlId=1;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.SaveProviderQuota @UserId int,@Channel varchar(10),
 @ProviderCode varchar(40),@DailyLimit int,@ReservedImportant int
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 IF NOT ((@Channel=''Sms'' AND @ProviderCode=''Twilio'')
         OR (@Channel=''WhatsApp'' AND @ProviderCode=''MetaCloud''))
    OR @DailyLimit NOT BETWEEN 1 AND 1000000
    OR @ReservedImportant<0 OR @ReservedImportant>=@DailyLimit
    THROW 51244,''Invalid provider daily message allowance.'',1;
 UPDATE msg.ProviderQuota SET DailyLimit=@DailyLimit,
   ReservedImportant=@ReservedImportant,UpdatedUtc=SYSUTCDATETIME(),UpdatedBy=@UserId
 WHERE ProviderCode=@ProviderCode AND Channel=@Channel;
 IF @@ROWCOUNT=0
  INSERT msg.ProviderQuota(Channel,ProviderCode,DailyLimit,ReservedImportant,UpdatedBy)
  VALUES(@Channel,@ProviderCode,@DailyLimit,@ReservedImportant,@UserId);
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ScheduleReviewedBatch
 @UserId int,@BatchId uniqueidentifier,@DueUtc datetime2(3),
 @ExpiresUtc datetime2(3)=NULL,@Priority varchar(10)=''Normal''
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 IF @BatchId IS NULL OR @DueUtc IS NULL OR @DueUtc<=SYSUTCDATETIME()
    OR (@ExpiresUtc IS NOT NULL AND @ExpiresUtc<=@DueUtc)
    OR @Priority NOT IN (''High'',''Normal'',''Low'')
    THROW 51245,''Invalid schedule, expiry, or priority.'',1;
 BEGIN TRANSACTION;
 DECLARE @Owner int,@Audience varchar(20);
 SELECT @Owner=B.CreatedBy,@Audience=B.Audience
 FROM msg.Batch B WITH(UPDLOCK,HOLDLOCK)
 JOIN msg.ApiBatch A ON A.BatchId=B.BatchId
 WHERE B.BatchId=@BatchId AND B.State=''Confirmed'';
 IF @Owner IS NULL THROW 51245,''Only reviewed, confirmed API batches can be scheduled.'',1;
 IF @Owner<>@UserId EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 IF @Audience=''Staff'' EXEC dbo.RequirePermission @UserId,N''Staff.View'';
 ELSE EXEC dbo.RequirePermission @UserId,N''Students.View'';
 IF @Audience IN (''OutstandingFees'',''FeeStudent'',''FeeFamily'')
    EXEC dbo.RequirePermission @UserId,N''Fees.View'';
 IF EXISTS(SELECT 1 FROM msg.Attempt T JOIN msg.Recipient R
           ON R.RecipientId=T.RecipientId WHERE R.BatchId=@BatchId)
     THROW 51245,''A batch with previous attempts cannot be newly scheduled.'',1;
 IF EXISTS(SELECT 1 FROM msg.BatchSchedule WITH(UPDLOCK,HOLDLOCK)
           WHERE BatchId=@BatchId)
     UPDATE msg.BatchSchedule SET DueUtc=@DueUtc,ExpiresUtc=@ExpiresUtc,
        Priority=@Priority,HoldForReview=0,UpdatedUtc=SYSUTCDATETIME(),
        UpdatedBy=@UserId WHERE BatchId=@BatchId;
 ELSE
     INSERT msg.BatchSchedule(BatchId,DueUtc,ExpiresUtc,Priority,UpdatedBy)
     VALUES(@BatchId,@DueUtc,@ExpiresUtc,@Priority,@UserId);
 COMMIT;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadScheduledBatches @UserId int
AS
BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 SELECT B.BatchId,B.Audience,B.Channel,B.Purpose,B.CreatedUtc,B.CreatedBy,
  A.ProviderCode,S.DueUtc,S.ExpiresUtc,S.Priority,S.HoldForReview,
  SUM(CASE WHEN R.State=''Pending'' THEN 1 ELSE 0 END) Pending,
  SUM(CASE WHEN R.State=''Unknown'' THEN 1 ELSE 0 END) Unknown,
  SUM(CASE WHEN R.State IN (''Submitted'',''Accepted'',''Delivered'') THEN 1 ELSE 0 END) Submitted
 FROM msg.BatchSchedule S
 JOIN msg.Batch B ON B.BatchId=S.BatchId
 JOIN msg.ApiBatch A ON A.BatchId=B.BatchId
 JOIN msg.Recipient R ON R.BatchId=B.BatchId
 WHERE B.CreatedBy=@UserId
 GROUP BY B.BatchId,B.Audience,B.Channel,B.Purpose,B.CreatedUtc,B.CreatedBy,
  A.ProviderCode,S.DueUtc,S.ExpiresUtc,S.Priority,S.HoldForReview
 ORDER BY S.DueUtc,B.BatchId;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.SaveAutomationMode @UserId int,
 @EventCode varchar(32),@Mode varchar(10),@CampusId int=0,@ClassId int=0
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 IF @Mode NOT IN (''Off'',''Review'',''Automatic'') OR @CampusId<0 OR @ClassId<0
   OR (@ClassId>0 AND @CampusId=0)
   THROW 51246,''Invalid automation mode or audience.'',1;
 UPDATE msg.AutomationRule SET Mode=@Mode,CampusId=@CampusId,ClassId=@ClassId,
  ActivationBaselineUtc=CASE WHEN Mode=''Off'' AND @Mode<>''Off''
    THEN SYSUTCDATETIME() ELSE ActivationBaselineUtc END,
  UpdatedUtc=SYSUTCDATETIME(),UpdatedBy=@UserId
 WHERE EventCode=@EventCode;
 IF @@ROWCOUNT<>1 THROW 51246,''Unknown automation event.'',1;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadAutomationRules @UserId int
AS
BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 SELECT EventCode,Mode,CampusId,ClassId,ActivationBaselineUtc,UpdatedUtc
 FROM msg.AutomationRule ORDER BY EventCode;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadProviderQuota @UserId int
AS
BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 SELECT Channel,ProviderCode,DailyLimit,ReservedImportant,UpdatedUtc
 FROM msg.ProviderQuota ORDER BY ProviderCode;
END');

-- Replace the existing claim body in place so no executable legacy bypass remains.
-- Holds SQL locks until a provider attempt is reserved. Unscheduled reviewed API
-- batches remain eligible for the existing explicitly confirmed Send Now path.
EXEC(N'CREATE OR ALTER PROCEDURE msg.ClaimRecipient
    @UserId int,
    @RecipientId bigint,
    @AttemptKey uniqueidentifier,
    @ExpectedProviderCode varchar(40)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
    IF @RecipientId IS NULL OR @RecipientId<=0
       OR @AttemptKey IS NULL OR @AttemptKey=''00000000-0000-0000-0000-000000000000''
       OR NULLIF(LTRIM(RTRIM(@ExpectedProviderCode)),'''') IS NULL
        THROW 51090,''A valid recipient, attempt key and approved provider are required.'',1;

    BEGIN TRANSACTION;

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
        SELECT @CurrentPhone=msg.NormalizeMobile(S.ContactNumber)
        FROM dbo.Students S WITH(HOLDLOCK)
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
          AND S.SchoolMessageConsent=1
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
    INSERT msg.Attempt(RecipientId,AttemptNumber,AttemptKey,Channel,ProviderCode,Outcome)
    VALUES(@RecipientId,@Ordinal,@AttemptKey,@Channel,@ProviderCode,''Unknown'');
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

INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
VALUES(N'23',SYSDATETIME(),SUSER_SNAME(),
 N'Communication schedule, automation configuration, master pause and claim gating');
