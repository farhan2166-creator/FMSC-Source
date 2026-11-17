-- Use the authoritative family consent/contact for every audience. Placement
-- filters stay on the selected student; no sibling outside that scope is added.
SET NOCOUNT ON; SET XACT_ABORT ON;
IF EXISTS(SELECT 1 FROM dbo.DatabaseVersion WHERE TRY_CONVERT(int,VersionNumber)=27) RETURN;
IF ISNULL((SELECT TOP(1) TRY_CONVERT(int,VersionNumber)
 FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0)<>26
 THROW 51000,'Communication contact consistency requires the preceding migration.',1;

BEGIN TRY
BEGIN TRANSACTION;

CREATE TABLE msg.ManualOpenEvidence(
 RecipientId bigint NOT NULL CONSTRAINT FK_MsgManualOpenEvidenceRecipient REFERENCES msg.ManualHandoff(RecipientId),
 OpenCount int NOT NULL,
 RenderedBody nvarchar(1600) NOT NULL,
 PreparedBy int NOT NULL,
 PreparedUtc datetime2(3) NOT NULL CONSTRAINT DF_MsgManualOpenEvidenceUtc DEFAULT SYSUTCDATETIME(),
 LaunchedBy int NULL,
 LaunchedUtc datetime2(3) NULL,
 CONSTRAINT PK_MsgManualOpenEvidence PRIMARY KEY(RecipientId,OpenCount),
 CONSTRAINT CK_MsgManualOpenEvidenceLaunch CHECK((LaunchedBy IS NULL AND LaunchedUtc IS NULL) OR (LaunchedBy IS NOT NULL AND LaunchedUtc IS NOT NULL)),
 CONSTRAINT CK_MsgManualOpenEvidenceCount CHECK(OpenCount>0),
 CONSTRAINT CK_MsgManualOpenEvidenceBody CHECK(LEN(LTRIM(RTRIM(RenderedBody)))>0)
);

EXEC(N'CREATE OR ALTER FUNCTION msg.ActiveFamilyFeeMembers(@AdmissionId int)
RETURNS TABLE AS RETURN
(
 SELECT S.AdmissionId,S.StudentName,S.AdmissionNumber,S.AdmissionDate,
   ISNULL(F.Outstanding,CONVERT(decimal(19,2),0)) Outstanding
 FROM dbo.Students S WITH(HOLDLOCK)
 OUTER APPLY(SELECT SUM(dbo.GetFeeChargeBalance(C.BillingChargeId)) Outstanding
   FROM dbo.BillingCharges C WITH(HOLDLOCK)
   WHERE C.AdmissionId=S.AdmissionId AND C.Status<>N''Cancelled'') F
 WHERE S.Status=N''Active'' AND (S.AdmissionId=@AdmissionId OR EXISTS(
   SELECT 1 FROM dbo.FamilyMembers Anchor WITH(HOLDLOCK)
   JOIN dbo.FamilyMembers Related WITH(HOLDLOCK) ON Related.FamilyId=Anchor.FamilyId
   WHERE Anchor.AdmissionId=@AdmissionId AND Related.AdmissionId=S.AdmissionId))
);
');

-- Recheck the source after review: a refund, opt-out or expired birthday
-- must invalidate an unattempted acknowledgment without changing its evidence.
EXEC(N'CREATE OR ALTER FUNCTION msg.PreparedEventEligible(@BatchId uniqueidentifier)
RETURNS TABLE AS RETURN
(
 SELECT CONVERT(bit,1) Eligible
 WHERE NOT EXISTS(
  SELECT 1 FROM msg.AutomationCandidate C WITH(HOLDLOCK)
  LEFT JOIN dbo.Students S WITH(HOLDLOCK) ON S.AdmissionId=C.AdmissionId
  LEFT JOIN dbo.FamilyMembers FM WITH(HOLDLOCK) ON FM.AdmissionId=C.AdmissionId
  CROSS APPLY(SELECT CONVERT(date,(SYSUTCDATETIME() AT TIME ZONE ''UTC'') AT TIME ZONE
    (SELECT TimeZoneId FROM msg.DispatchControl WHERE ControlId=1)) SchoolDate) Today
  WHERE C.BatchId=@BatchId AND C.State=''Prepared'' AND
   (S.AdmissionId IS NULL OR S.Status<>N''Active''
    OR EXISTS(SELECT 1 FROM msg.AutomationException X WITH(HOLDLOCK)
      WHERE X.EventCode=C.EventCode AND (X.AdmissionId=C.AdmissionId OR X.FamilyId=FM.FamilyId)
       AND X.RevokedUtc IS NULL AND (X.UntilUtc IS NULL OR X.UntilUtc>SYSUTCDATETIME()))
    OR (C.EventCode=''PaymentReceived'' AND NOT EXISTS(
      SELECT 1 FROM dbo.FeePayments P WITH(HOLDLOCK)
      WHERE P.FeePaymentId=C.SourceRecordId AND P.AdmissionId=C.AdmissionId
       AND P.Status=N''Completed'' AND P.RelatedFeePaymentId IS NULL
       AND NOT EXISTS(SELECT 1 FROM dbo.FeePayments A WITH(HOLDLOCK)
         WHERE A.RelatedFeePaymentId=P.FeePaymentId)))
    OR (C.EventCode=''Birthday'' AND
       (S.DateOfBirth IS NULL OR MONTH(S.DateOfBirth)<>MONTH(Today.SchoolDate)
        OR DAY(S.DateOfBirth)<>DAY(Today.SchoolDate)
        OR C.SourceKey<>CONCAT(''BD:'',YEAR(Today.SchoolDate),'':'',C.AdmissionId)))
    OR (C.EventCode IN (''OutstandingFees'',''UpcomingDue'',''OverdueFollowUp'')
       AND NOT EXISTS(SELECT 1 FROM msg.LiveFeeEventEligible(C.AdmissionId,C.EventCode,Today.SchoolDate)))))
);');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ConfirmBatch
    @UserId int,
    @ClientRequestId uniqueidentifier,
    @Channel varchar(10),
    @Purpose varchar(20),
    @Body nvarchar(1600),
    @Audience varchar(20),
    @CampusId int=0,
    @ClassId int=0,
    @StudentId int=0,
    @StaffId int=0,
    @Recipients msg.RecipientSelection READONLY
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
    IF @Audience=N''Staff''
        EXEC dbo.RequirePermission @UserId,N''Staff.View'';
    ELSE
        EXEC dbo.RequirePermission @UserId,N''Students.View'';
    IF @Audience IN (''OutstandingFees'',''FeeStudent'',''FeeFamily'')
        EXEC dbo.RequirePermission @UserId,N''Fees.View'';
    IF @ClientRequestId IS NULL OR @Channel IS NULL OR @Purpose IS NULL OR @Audience IS NULL
       OR @Body IS NULL OR @Channel NOT IN (''Sms'',''WhatsApp'')
       OR @Purpose NOT IN (''Announcement'',''FeeReminder'',''Individual'',''Staff'')
       OR @Audience NOT IN (''School'',''Campus'',''Class'',''OutstandingFees'',''Student'',''Family'',''Staff'',''FeeStudent'',''FeeFamily'')
       OR LEN(LTRIM(RTRIM(ISNULL(@Body,N''''))))=0 OR LEN(@Body)>1600
       OR @Body LIKE N''%{{%'' OR @Body LIKE N''%}}%''
       OR (@Audience=''Campus'' AND @CampusId<=0)
       OR (@Audience=''Class'' AND (@CampusId<=0 OR @ClassId<=0))
       OR (@Audience IN (''Student'',''Family'',''FeeStudent'',''FeeFamily'') AND @StudentId<=0)
       OR (@Audience=''Staff'' AND @StaffId<=0)
       OR (@Audience IN (''OutstandingFees'',''FeeStudent'',''FeeFamily'') AND @Purpose<>''FeeReminder'')
       OR (@Audience=''Staff'' AND @Purpose<>''Staff'')
       OR (@Audience IN (''School'',''Campus'',''Class'') AND @Purpose<>''Announcement'')
       OR (@Audience IN (''Student'',''Family'') AND @Purpose<>''Individual'')
       THROW 51080,''Invalid communication target, purpose or message.'',1;
    IF NOT EXISTS(SELECT 1 FROM @Recipients)
       THROW 51081,''No eligible destinations remain. Review recipients again.'',1;
    IF EXISTS (SELECT 1 FROM @Recipients WHERE RecordId<=0 OR PhoneE164 NOT LIKE ''+923[0-6]%''
       OR LEN(PhoneE164)<>13)
       THROW 51082,''Invalid destination snapshot.'',1;
    IF EXISTS(SELECT PhoneE164 FROM @Recipients GROUP BY PhoneE164 HAVING COUNT(*)>1)
       THROW 51082,''Duplicate destinations are not permitted.'',1;

    BEGIN TRANSACTION;
    DECLARE @LockResult int, @Key nvarchar(100)=N''FMSC_msg_''+CONVERT(nvarchar(36),@ClientRequestId);
    EXEC @LockResult=sys.sp_getapplock
        @Resource=@Key,@LockMode=''Exclusive'',@LockOwner=''Transaction'',@LockTimeout=10000;
    IF @LockResult<0 THROW 51083,''Communication confirmation is busy. Retry the same request.'',1;
    DECLARE @OldBatch uniqueidentifier;
    SELECT @OldBatch=BatchId FROM msg.Batch WITH(UPDLOCK,HOLDLOCK)
    WHERE ClientRequestId=@ClientRequestId;
    IF @OldBatch IS NOT NULL
    BEGIN
        IF NOT EXISTS(SELECT 1 FROM msg.Batch WHERE BatchId=@OldBatch AND CreatedBy=@UserId
          AND Channel=@Channel AND Purpose=@Purpose AND Body=@Body
          AND Audience=@Audience AND CampusId=@CampusId AND ClassId=@ClassId
          AND StudentId=@StudentId AND StaffId=@StaffId)
            THROW 51084,''Confirmation key already belongs to a different request.'',1;
        SELECT @OldBatch BatchId,COUNT_BIG(*) RecipientCount,CONVERT(bit,1) AlreadyConfirmed
        FROM msg.Recipient WHERE BatchId=@OldBatch;
        COMMIT TRANSACTION; RETURN;
    END;

    IF NOT EXISTS(SELECT 1 FROM msg.ChannelSettings WITH(HOLDLOCK)
      WHERE Channel=@Channel AND Enabled=1 AND NULLIF(LTRIM(RTRIM(ProviderCode)),'''') IS NOT NULL)
        THROW 51085,''This channel is paused or has no approved provider.'',1;

    DECLARE @Live TABLE(
        RecipientKind varchar(8) NOT NULL, RecordId int NOT NULL,
        PhoneE164 varchar(16) NULL, RecordName nvarchar(250) NULL
    );
    IF @Audience=''Staff''
    BEGIN
        INSERT @Live(RecipientKind,RecordId,PhoneE164,RecordName)
        SELECT ''Staff'',S.StaffId,msg.NormalizeMobile(S.ContactNumber),S.StaffName
        FROM dbo.Staff S WITH(HOLDLOCK)
        WHERE S.StaffId=@StaffId AND S.Status=N''Active'';
    END
    ELSE IF @Audience IN (''Student'',''Family'',''FeeStudent'',''FeeFamily'')
    BEGIN
        INSERT @Live(RecipientKind,RecordId,PhoneE164,RecordName)
        SELECT ''Student'',G.AdmissionId,G.PhoneE164,S.StudentName
        FROM msg.RecordGuardian(@StudentId) G
        JOIN dbo.Students S WITH(HOLDLOCK) ON S.AdmissionId=G.AdmissionId
        WHERE G.HasConsent=1;
    END
    ELSE
    BEGIN
        INSERT @Live(RecipientKind,RecordId,PhoneE164,RecordName)
        SELECT ''Student'',S.AdmissionId,Guardian.PhoneE164,S.StudentName
        FROM dbo.Students S WITH(HOLDLOCK)
        OUTER APPLY msg.RecordGuardian(S.AdmissionId) Guardian
        OUTER APPLY (
            SELECT TOP(1) E.ClassSectionId,E.Status
            FROM dbo.Enrollments E WHERE E.AdmissionId=S.AdmissionId
            ORDER BY CASE WHEN E.Status=N''Active'' THEN 0 ELSE 1 END,E.EnrollmentId DESC
        ) E
        LEFT JOIN dbo.CampusClassSections CCS ON CCS.ClassSectionId=E.ClassSectionId
        OUTER APPLY (
            SELECT SUM(dbo.GetFeeChargeBalance(BC.BillingChargeId)) Outstanding
            FROM dbo.BillingCharges BC
            WHERE @Audience=''OutstandingFees''
              AND BC.AdmissionId=S.AdmissionId AND BC.Status<>N''Cancelled''
        ) B
        WHERE S.Status=N''Active'' AND Guardian.HasConsent=1
          AND (
            @Audience=''School''
            OR (@Audience=''Campus'' AND E.Status=N''Active'' AND CCS.CampusId=@CampusId)
            OR (@Audience=''Class'' AND E.Status=N''Active'' AND CCS.CampusId=@CampusId AND CCS.ClassId=@ClassId)
            OR (@Audience=''OutstandingFees'' AND E.Status=N''Active''
                AND (@CampusId=0 OR CCS.CampusId=@CampusId)
                AND (@ClassId=0 OR CCS.ClassId=@ClassId)
                AND (@StudentId=0 OR S.AdmissionId=@StudentId) AND B.Outstanding>0)
          );
    END;
    DECLARE @Eligible TABLE(
        RecipientKind varchar(8) NOT NULL, RecordId int NOT NULL,
        PhoneE164 varchar(16) NOT NULL,
        PRIMARY KEY(RecipientKind,RecordId)
    );
    ;WITH Ranked AS (
        SELECT L.RecipientKind,L.RecordId,L.PhoneE164,
            ROW_NUMBER() OVER (PARTITION BY L.PhoneE164
               ORDER BY L.RecordName,L.RecordId) RankWithinPhone
        FROM @Live L
        WHERE L.PhoneE164 IS NOT NULL AND NOT EXISTS(
            SELECT 1 FROM msg.Suppression O WITH(HOLDLOCK)
            WHERE O.Channel=@Channel AND O.PhoneE164=L.PhoneE164)
    )
    INSERT @Eligible(RecipientKind,RecordId,PhoneE164)
    SELECT RecipientKind,RecordId,PhoneE164 FROM Ranked WHERE RankWithinPhone=1;
    -- Never send a partial or expanded audience when records changed after Preview.
    IF EXISTS(SELECT RecipientKind,RecordId,PhoneE164 FROM @Eligible
              EXCEPT SELECT RecipientKind,RecordId,PhoneE164 FROM @Recipients)
       OR EXISTS(SELECT RecipientKind,RecordId,PhoneE164 FROM @Recipients
                 EXCEPT SELECT RecipientKind,RecordId,PhoneE164 FROM @Eligible)
       THROW 51086,''Recipients changed since preview. Refresh before confirming.'',1;

    DECLARE @BatchId uniqueidentifier=NEWID();
    INSERT msg.Batch(BatchId,ClientRequestId,Channel,Purpose,Body,
        Audience,CampusId,ClassId,StudentId,StaffId,SingleStudentScope,State,
        CreatedBy,ConfirmedBy,ConfirmedUtc)
    VALUES(@BatchId,@ClientRequestId,@Channel,@Purpose,@Body,
        @Audience,@CampusId,@ClassId,@StudentId,@StaffId,CASE WHEN @Audience=''OutstandingFees'' AND @StudentId>0 THEN 1 ELSE 0 END,''Confirmed'',
        @UserId,@UserId,SYSUTCDATETIME());
    INSERT msg.Recipient(BatchId,RecipientKind,AdmissionId,StaffId,PhoneE164)
    SELECT @BatchId,RecipientKind,
        CASE WHEN RecipientKind=''Student'' THEN RecordId END,
        CASE WHEN RecipientKind=''Staff'' THEN RecordId END,PhoneE164
    FROM @Eligible;

    SELECT @BatchId BatchId,COUNT_BIG(*) RecipientCount,CONVERT(bit,0) AlreadyConfirmed
    FROM msg.Recipient WHERE BatchId=@BatchId;
    COMMIT TRANSACTION;
END;');

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


    -- Scheduled work cannot outlive the originating sender''s authority.
    -- An administrator takeover will need its own explicit audit action.
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

EXEC(N'CREATE OR ALTER PROCEDURE msg.OpenManualHandoff @UserId int,@RecipientId bigint
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
    IF @RecipientId IS NULL OR @RecipientId<=0 THROW 51114,''Invalid manual recipient.'',1;
    BEGIN TRANSACTION;
    DECLARE @BatchId uniqueidentifier,@State varchar(16),@Kind varchar(8),
        @AdmissionId int,@StaffId int,@Snapshot varchar(16),@Live varchar(16),
        @Audience varchar(20),@CampusId int,@ClassId int,@StudentId int,@TargetStaff int,
        @Body nvarchar(1600),@Excluded varchar(32),@OpenCount int;
    SELECT @BatchId=R.BatchId,@State=H.State,@Kind=R.RecipientKind,
        @AdmissionId=R.AdmissionId,@StaffId=R.StaffId,@OpenCount=H.OpenCount,
        @Snapshot=R.PhoneE164,@Body=B.Body,@Audience=B.Audience,
        @CampusId=B.CampusId,@ClassId=B.ClassId,@StudentId=CASE WHEN B.Audience=''OutstandingFees'' AND B.SingleStudentScope=0 THEN 0 ELSE B.StudentId END,@TargetStaff=B.StaffId
    FROM msg.ManualHandoff H WITH(UPDLOCK,HOLDLOCK)
    JOIN msg.Recipient R WITH(UPDLOCK,HOLDLOCK) ON R.RecipientId=H.RecipientId
    JOIN msg.ManualBatch M ON M.BatchId=R.BatchId
    JOIN msg.Batch B ON B.BatchId=R.BatchId
    WHERE H.RecipientId=@RecipientId AND B.Channel=''WhatsApp''
      AND R.State=''Pending'' AND B.State IN (''Confirmed'',''Sending'');
    IF @BatchId IS NULL OR @State NOT IN (''Pending'',''Opened'')
        THROW 51114,''This handoff was already processed, cancelled or excluded.'',1;
    IF NOT EXISTS(SELECT 1 FROM msg.ManualBatch WHERE BatchId=@BatchId AND CreatedBy=@UserId)
        EXEC dbo.RequirePermission @UserId,N''Communication.View'';
    IF NOT EXISTS(SELECT 1 FROM msg.ChannelSettings WITH(HOLDLOCK)
       WHERE Channel=''WhatsApp'' AND ProviderCode=''WhatsAppBusinessManual'' AND Enabled=1)
        THROW 51114,''Free WhatsApp handoff is paused or no longer configured.'',1;
    IF @Kind=''Staff'' EXEC dbo.RequirePermission @UserId,N''Staff.View'';
    ELSE EXEC dbo.RequirePermission @UserId,N''Students.View'';
    IF @Audience IN (''OutstandingFees'',''FeeStudent'',''FeeFamily'') EXEC dbo.RequirePermission @UserId,N''Fees.View'';

    -- Source and scope checks mirror msg.ClaimRecipient. No snapshot is
    -- usable when admission consent, placement, phone or opt-out changes.
    IF @Kind=''Staff''
    BEGIN
        IF @Audience=''Staff'' AND @TargetStaff=@StaffId
            SELECT @Live=msg.NormalizeMobile(T.ContactNumber)
            FROM dbo.Staff T WITH(HOLDLOCK)
            WHERE T.StaffId=@StaffId AND T.Status=N''Active'';
    END
    ELSE IF @Audience IN (''Student'',''Family'',''FeeStudent'',''FeeFamily'')
    BEGIN
        SELECT @Live=G.PhoneE164 FROM msg.RecordGuardian(@StudentId) G
        WHERE G.AdmissionId=@AdmissionId AND G.HasConsent=1;
    END
    ELSE
    BEGIN
        SELECT @Live=Guardian.PhoneE164
        FROM dbo.Students S WITH(HOLDLOCK)
        OUTER APPLY msg.RecordGuardian(S.AdmissionId) Guardian
        OUTER APPLY (
          SELECT TOP(1) E.ClassSectionId,E.Status
          FROM dbo.Enrollments E WITH(HOLDLOCK) WHERE E.AdmissionId=S.AdmissionId
          ORDER BY CASE WHEN E.Status=N''Active'' THEN 0 ELSE 1 END,E.EnrollmentId DESC
        ) E
        LEFT JOIN dbo.CampusClassSections CCS WITH(HOLDLOCK)
          ON CCS.ClassSectionId=E.ClassSectionId
        OUTER APPLY (
          SELECT SUM(dbo.GetFeeChargeBalance(BC.BillingChargeId)) Outstanding
          FROM dbo.BillingCharges BC WITH(HOLDLOCK)
          WHERE @Audience=''OutstandingFees'' AND BC.AdmissionId=S.AdmissionId
            AND BC.Status<>N''Cancelled''
        ) F
        WHERE S.AdmissionId=@AdmissionId AND S.Status=N''Active''
          AND Guardian.HasConsent=1 AND
          (@Audience=''School''
            OR (@Audience=''Campus'' AND E.Status=N''Active'' AND CCS.CampusId=@CampusId)
            OR (@Audience=''Class'' AND E.Status=N''Active'' AND CCS.CampusId=@CampusId
                AND CCS.ClassId=@ClassId)
            OR (@Audience=''OutstandingFees'' AND E.Status=N''Active''
                AND (@CampusId=0 OR CCS.CampusId=@CampusId)
                AND (@ClassId=0 OR CCS.ClassId=@ClassId)
                AND (@StudentId=0 OR S.AdmissionId=@StudentId) AND F.Outstanding>0));
    END;
    SET @Excluded=CASE
      WHEN NOT EXISTS(SELECT 1 FROM msg.PreparedEventEligible(@BatchId)) THEN ''SourceChanged''
      WHEN @Live IS NULL THEN ''NoLongerEligible''
      WHEN @Live<>@Snapshot THEN ''DestinationChanged''
      WHEN EXISTS(SELECT 1 FROM msg.Suppression O WITH(HOLDLOCK)
                  WHERE O.Channel=''WhatsApp'' AND O.PhoneE164=@Live)
           THEN ''ChannelOptOut''
      WHEN @Audience=''FeeFamily'' AND
        (EXISTS(SELECT AdmissionId FROM msg.ActiveFamilyFeeMembers(@StudentId)
           EXCEPT SELECT AdmissionId FROM msg.ReviewedFeeStudent WHERE RecipientId=@RecipientId)
         OR EXISTS(SELECT AdmissionId FROM msg.ReviewedFeeStudent WHERE RecipientId=@RecipientId
           EXCEPT SELECT AdmissionId FROM msg.ActiveFamilyFeeMembers(@StudentId))) THEN ''FamilyChanged''
      ELSE NULL END;
    IF @Excluded IS NOT NULL
    BEGIN
        UPDATE msg.ManualHandoff SET State=''Excluded'',UpdatedUtc=SYSUTCDATETIME(),
            ChangedBy=@UserId WHERE RecipientId=@RecipientId;
        UPDATE msg.Recipient SET State=''Excluded'',ExclusionCode=@Excluded,
            UpdatedUtc=SYSUTCDATETIME() WHERE RecipientId=@RecipientId;
        -- Auto-excluding the final target completes the manual session too.
        -- This records no send or delivery evidence.
        IF NOT EXISTS(SELECT 1 FROM msg.ManualHandoff H
            JOIN msg.Recipient R ON R.RecipientId=H.RecipientId
            WHERE R.BatchId=@BatchId AND H.State IN (''Pending'',''Opened''))
            UPDATE msg.Batch SET State=''Completed'' WHERE BatchId=@BatchId AND State=''Confirmed'';
        SELECT CONVERT(bit,0) Opened,CONVERT(varchar(16),NULL) PhoneE164,
          CONVERT(nvarchar(1600),NULL) Body,@Excluded ExclusionCode,@Audience Audience;
        COMMIT TRANSACTION; RETURN;
    END;
    UPDATE msg.ManualHandoff SET State=''Opened'',ShellHandoffUtc=NULL,OpenCount=OpenCount+1,
        LastOpenedUtc=SYSUTCDATETIME(),ChangedBy=@UserId,UpdatedUtc=SYSUTCDATETIME()
    WHERE RecipientId=@RecipientId;
    SELECT CONVERT(bit,1) Opened,@Live PhoneE164,@Body Body,
        CONVERT(varchar(32),NULL) ExclusionCode,@Audience Audience,@OpenCount+1 OpenCount;
    COMMIT TRANSACTION;
END;');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ConfirmReviewedManualBatch
    @UserId int,@ClientRequestId uniqueidentifier,@Channel varchar(10),
    @Purpose varchar(20),@Body nvarchar(1600),@Audience varchar(20),
    @CampusId int,@ClassId int,@StudentId int,@StaffId int,
    @Recipients msg.RecipientSelection READONLY,@ReviewedFees msg.RecipientSelection READONLY
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    BEGIN TRANSACTION;
    EXEC msg.ConfirmManualBatch @UserId=@UserId,@ClientRequestId=@ClientRequestId,@Channel=@Channel,
        @Purpose=@Purpose,@Body=@Body,@Audience=@Audience,@CampusId=@CampusId,
        @ClassId=@ClassId,@StudentId=@StudentId,@StaffId=@StaffId,@Recipients=@Recipients;
    IF @Audience IN (''OutstandingFees'',''FeeFamily'')
    BEGIN
        DECLARE @BatchId uniqueidentifier=(SELECT BatchId FROM msg.Batch WHERE ClientRequestId=@ClientRequestId);
        IF EXISTS(SELECT 1 FROM @ReviewedFees F WHERE F.RecipientKind<>''Student'' OR F.RecordId<=0
            OR NOT EXISTS(SELECT 1 FROM msg.Recipient R WHERE R.BatchId=@BatchId AND R.PhoneE164=F.PhoneE164))
            OR EXISTS(SELECT RecipientKind,RecordId,PhoneE164 FROM @Recipients
                EXCEPT SELECT RecipientKind,RecordId,PhoneE164 FROM @ReviewedFees)
            THROW 51120,''Fee reminder membership is incomplete. Review the recipients again.'',1;
        IF EXISTS(SELECT 1 FROM msg.ReviewedFeeStudent F JOIN msg.Recipient R ON R.RecipientId=F.RecipientId
            WHERE R.BatchId=@BatchId)
        BEGIN
            IF EXISTS(SELECT R.PhoneE164,F.AdmissionId FROM msg.ReviewedFeeStudent F
                JOIN msg.Recipient R ON R.RecipientId=F.RecipientId WHERE R.BatchId=@BatchId
                EXCEPT SELECT PhoneE164,RecordId FROM @ReviewedFees)
                OR EXISTS(SELECT PhoneE164,RecordId FROM @ReviewedFees
                EXCEPT SELECT R.PhoneE164,F.AdmissionId FROM msg.ReviewedFeeStudent F
                JOIN msg.Recipient R ON R.RecipientId=F.RecipientId WHERE R.BatchId=@BatchId)
                THROW 51120,''This confirmation already has a different fee review. Start a new review.'',1;
        END
        ELSE
        BEGIN
            DECLARE @LiveFees TABLE(PhoneE164 varchar(16),AdmissionId int PRIMARY KEY);
            INSERT @LiveFees(PhoneE164,AdmissionId)
            SELECT R.PhoneE164,S.AdmissionId
            FROM msg.Recipient R
            CROSS JOIN dbo.Students S WITH(HOLDLOCK)
            CROSS APPLY msg.RecordGuardian(S.AdmissionId) Guardian
            OUTER APPLY (
                SELECT TOP(1) E.ClassSectionId,E.Status
                FROM dbo.Enrollments E WITH(HOLDLOCK) WHERE E.AdmissionId=S.AdmissionId
                ORDER BY CASE WHEN E.Status=N''Active'' THEN 0 ELSE 1 END,E.EnrollmentId DESC
            ) E
            LEFT JOIN dbo.CampusClassSections CCS WITH(HOLDLOCK) ON CCS.ClassSectionId=E.ClassSectionId
            OUTER APPLY (
                SELECT SUM(dbo.GetFeeChargeBalance(C.BillingChargeId)) Outstanding
                FROM dbo.BillingCharges C WITH(HOLDLOCK)
                WHERE C.AdmissionId=S.AdmissionId AND C.Status<>N''Cancelled''
            ) F
            WHERE @Audience=''OutstandingFees'' AND Guardian.PhoneE164=R.PhoneE164 AND R.BatchId=@BatchId AND S.Status=N''Active'' AND Guardian.HasConsent=1
                AND E.Status=N''Active'' AND F.Outstanding>0
                AND (@CampusId=0 OR CCS.CampusId=@CampusId)
                AND (@ClassId=0 OR CCS.ClassId=@ClassId)
                AND (@StudentId=0 OR S.AdmissionId=@StudentId)
                AND NOT EXISTS(SELECT 1 FROM msg.Suppression O WITH(HOLDLOCK)
                    WHERE O.Channel=''WhatsApp'' AND O.PhoneE164=R.PhoneE164);
            IF @Audience=''FeeFamily''
                INSERT @LiveFees(PhoneE164,AdmissionId)
                SELECT R.PhoneE164,F.AdmissionId FROM msg.Recipient R
                CROSS APPLY msg.ActiveFamilyFeeMembers(@StudentId) F
                WHERE R.BatchId=@BatchId;
            IF EXISTS(SELECT PhoneE164,AdmissionId FROM @LiveFees EXCEPT SELECT PhoneE164,RecordId FROM @ReviewedFees)
                OR EXISTS(SELECT PhoneE164,RecordId FROM @ReviewedFees EXCEPT SELECT PhoneE164,AdmissionId FROM @LiveFees)
                THROW 51120,''Fee reminder membership changed. Preview the recipients again.'',1;
            INSERT msg.ReviewedFeeStudent(RecipientId,AdmissionId)
            SELECT R.RecipientId,F.RecordId FROM @ReviewedFees F
            JOIN msg.Recipient R ON R.BatchId=@BatchId AND R.PhoneE164=F.PhoneE164;
        END;
    END;
    COMMIT TRANSACTION;
END;');

EXEC(N'CREATE OR ALTER TRIGGER msg.TR_Attempt_RejectManualProvider ON msg.Attempt AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted I JOIN msg.Recipient R ON R.RecipientId=I.RecipientId
   LEFT JOIN msg.ApiBatch A ON A.BatchId=R.BatchId
   WHERE I.ProviderCode IN (''WhatsAppBusinessManual'',''EmailClientManual'')
    OR EXISTS(SELECT 1 FROM msg.ManualBatch M WHERE M.BatchId=R.BatchId)
    OR (A.BatchId IS NOT NULL AND A.ProviderCode<>I.ProviderCode)
    OR (I.ProviderCode=''MetaCloud'' AND A.BatchId IS NULL))
  THROW 51116,''The reviewed sending method does not permit this API attempt.'',1;

 -- Shared guardian fee amounts and consent are checked under the same claim
 -- transaction as the representative. No stale sibling can slip through while
 -- the desktop awaits provider-template validation.
 DECLARE @Expected TABLE(RecipientId bigint,AdmissionId int,Outstanding decimal(19,2));
 INSERT @Expected
 SELECT I.RecipientId,F.AdmissionId,F.Outstanding
 FROM inserted I JOIN msg.Recipient R ON R.RecipientId=I.RecipientId
 JOIN msg.ApiBatch A ON A.BatchId=R.BatchId JOIN msg.Batch B ON B.BatchId=A.BatchId
 CROSS APPLY OPENJSON(A.ReviewJson) WITH(Phone varchar(16),FeeMembers nvarchar(max) AS JSON) J
 CROSS APPLY OPENJSON(J.FeeMembers) WITH(AdmissionId int,Outstanding decimal(19,2)) F
 WHERE B.Audience IN (''OutstandingFees'',''FeeFamily'') AND J.Phone=R.PhoneE164;
 DECLARE @Live TABLE(RecipientId bigint,AdmissionId int,Outstanding decimal(19,2));
 INSERT @Live
 SELECT I.RecipientId,S.AdmissionId,F.Outstanding
 FROM inserted I JOIN msg.Recipient R ON R.RecipientId=I.RecipientId
 JOIN msg.Batch B ON B.BatchId=R.BatchId JOIN msg.ApiBatch A ON A.BatchId=B.BatchId
 CROSS JOIN dbo.Students S WITH(HOLDLOCK)
            CROSS APPLY msg.RecordGuardian(S.AdmissionId) Guardian
 OUTER APPLY(SELECT TOP(1) E.ClassSectionId,E.Status FROM dbo.Enrollments E WITH(HOLDLOCK)
  WHERE E.AdmissionId=S.AdmissionId ORDER BY CASE WHEN E.Status=N''Active'' THEN 0 ELSE 1 END,E.EnrollmentId DESC) E
 LEFT JOIN dbo.CampusClassSections CCS WITH(HOLDLOCK) ON CCS.ClassSectionId=E.ClassSectionId
 OUTER APPLY(SELECT SUM(dbo.GetFeeChargeBalance(C.BillingChargeId)) Outstanding FROM dbo.BillingCharges C WITH(HOLDLOCK)
  WHERE C.AdmissionId=S.AdmissionId AND C.Status<>N''Cancelled'') F
 WHERE Guardian.PhoneE164=R.PhoneE164 AND B.Audience=''OutstandingFees'' AND S.Status=N''Active'' AND Guardian.HasConsent=1 AND E.Status=N''Active''
  AND (B.CampusId=0 OR CCS.CampusId=B.CampusId) AND (B.ClassId=0 OR CCS.ClassId=B.ClassId)
  AND (B.StudentId=0 OR S.AdmissionId=B.StudentId) AND F.Outstanding>0;
 INSERT @Live
 SELECT I.RecipientId,F.AdmissionId,F.Outstanding FROM inserted I
 JOIN msg.Recipient R ON R.RecipientId=I.RecipientId
 JOIN msg.Batch B ON B.BatchId=R.BatchId JOIN msg.ApiBatch A ON A.BatchId=B.BatchId
 CROSS APPLY msg.ActiveFamilyFeeMembers(B.StudentId) F
 WHERE B.Audience=''FeeFamily'';
 IF EXISTS(SELECT * FROM @Expected EXCEPT SELECT * FROM @Live)
  OR EXISTS(SELECT * FROM @Live EXCEPT SELECT * FROM @Expected)
  THROW 51133,''The reviewed family fee membership or balances changed. Review a new message.'',1;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadSendingTimeZone @UserId int
AS BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 SELECT TimeZoneId FROM msg.DispatchControl WHERE ControlId=1;
END');

-- A blocked or throttled channel must not starve independently healthy work.
EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadNextScheduledRecipient
 @UserId int,@EnrollmentId uniqueidentifier,@LeaseNonce uniqueidentifier,
 @SkipSms bit=0,@SkipWhatsApp bit=0,@DeferredBatches nvarchar(4000)=N''[]''
AS
BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 IF ISNULL(ISJSON(@DeferredBatches),0)<>1
   THROW 51246,''Invalid deferred queue selection.'',1;
 IF NOT EXISTS(SELECT 1 FROM msg.BackgroundWorkerLease WITH(HOLDLOCK)
    WHERE LeaseId=1 AND EnrollmentId=@EnrollmentId AND LeaseNonce=@LeaseNonce
      AND LeaseUntilUtc>SYSUTCDATETIME())
  THROW 51263,''Background worker lease is not owned by this process.'',1;
 IF NOT EXISTS(SELECT 1 FROM msg.DispatchControl
   WHERE ControlId=1 AND UnattendedEnabled=1 AND MasterPaused=0)
  RETURN;
 DECLARE @Start time(0),@End time(0),@Zone sysname,@Local datetimeoffset;
 SELECT @Start=WindowStart,@End=WindowEnd,@Zone=TimeZoneId
 FROM msg.DispatchControl WHERE ControlId=1;
 SET @Local=(SYSUTCDATETIME() AT TIME ZONE ''UTC'') AT TIME ZONE @Zone;
 IF CONVERT(time(0),@Local)<@Start OR CONVERT(time(0),@Local)>=@End RETURN;
 DECLARE @SchoolDate date=CONVERT(date,@Local);
 DECLARE @Day tinyint=CONVERT(tinyint,((DATEDIFF(day,''19000101'',@SchoolDate)%7)+7)%7+1);
 IF EXISTS(SELECT 1 FROM msg.NonWorkingWeekday WHERE IsoWeekday=@Day AND Enabled=0) RETURN;
 IF EXISTS(SELECT 1 FROM msg.SchoolHoliday WHERE HolidayDate=@SchoolDate) RETURN;
 SELECT TOP(1) R.RecipientId,B.BatchId,B.Channel,B.Audience,
  COALESCE(Takeover.NewOwnerId,B.CreatedBy) CreatedBy,S.Priority
 FROM msg.BatchSchedule S
 JOIN msg.Batch B ON B.BatchId=S.BatchId
 JOIN msg.ApiBatch A ON A.BatchId=B.BatchId
 JOIN msg.ChannelSettings Channel ON Channel.Channel=B.Channel
   AND Channel.Enabled=1 AND Channel.ProviderCode=A.ProviderCode
 JOIN msg.Recipient R ON R.BatchId=B.BatchId
 OUTER APPLY (SELECT TOP(1) T.NewOwnerId FROM msg.BatchTakeover T
   WHERE T.BatchId=B.BatchId ORDER BY T.TakeoverId DESC) Takeover
 WHERE NOT EXISTS(SELECT 1 FROM OPENJSON(@DeferredBatches) D
     WHERE TRY_CONVERT(uniqueidentifier,D.value)=B.BatchId)
   AND (B.Channel<>''Sms'' OR @SkipSms=0)
   AND (B.Channel<>''WhatsApp'' OR @SkipWhatsApp=0)
   AND S.HoldForReview=0 AND S.DueUtc<=SYSUTCDATETIME()
   AND (S.ExpiresUtc IS NULL OR S.ExpiresUtc>SYSUTCDATETIME())
   AND B.State IN (''Confirmed'',''Sending'') AND R.State=''Pending''
 ORDER BY CASE S.Priority WHEN ''High'' THEN 0 WHEN ''Normal'' THEN 1 ELSE 2 END,
          S.DueUtc,R.RecipientId;
END');

-- Store the actual handed-off text, not a later re-rendering of school data.
EXEC(N'CREATE OR ALTER PROCEDURE msg.RecordShellHandoff
 @UserId int,@RecipientId bigint,@OpenCount int,@RenderedBody nvarchar(1600),@ShellOpened bit=0
AS BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 IF @ShellOpened IS NULL OR @OpenCount IS NULL OR @OpenCount<=0 OR @RenderedBody IS NULL
   OR LEN(LTRIM(RTRIM(@RenderedBody)))=0
  THROW 51114,''The exact opened message and handoff reference are required.'',1;
 BEGIN TRANSACTION;
 DECLARE @Audience varchar(20),@EvidenceBatchId uniqueidentifier;
 SELECT @Audience=B.Audience,@EvidenceBatchId=B.BatchId FROM msg.ManualHandoff H WITH(UPDLOCK,HOLDLOCK)
 JOIN msg.Recipient R WITH(HOLDLOCK) ON R.RecipientId=H.RecipientId
 JOIN msg.Batch B WITH(HOLDLOCK) ON B.BatchId=R.BatchId
 WHERE H.RecipientId=@RecipientId AND H.OpenCount=@OpenCount
   AND H.State=''Opened'' AND H.ChangedBy=@UserId AND R.State=''Pending'';
 IF @Audience IS NULL THROW 51114,''The WhatsApp handoff changed. Review the current session.'',1;
 IF @ShellOpened=0 AND NOT EXISTS(SELECT 1 FROM msg.PreparedEventEligible(@EvidenceBatchId))
   THROW 51114,''The source event changed. Review the message again.'',1;
 IF @Audience=''Staff'' EXEC dbo.RequirePermission @UserId,N''Staff.View'';
 ELSE EXEC dbo.RequirePermission @UserId,N''Students.View'';
 IF @Audience IN (''OutstandingFees'',''FeeStudent'',''FeeFamily'') EXEC dbo.RequirePermission @UserId,N''Fees.View'';
 IF EXISTS(SELECT 1 FROM msg.ManualOpenEvidence WHERE RecipientId=@RecipientId AND OpenCount=@OpenCount)
 BEGIN
   IF NOT EXISTS(SELECT 1 FROM msg.ManualOpenEvidence
     WHERE RecipientId=@RecipientId AND OpenCount=@OpenCount AND PreparedBy=@UserId
      AND RenderedBody COLLATE Latin1_General_100_BIN2=@RenderedBody COLLATE Latin1_General_100_BIN2)
    THROW 51114,''This handoff already records different content. Review the current session.'',1;
 END
 ELSE
   INSERT msg.ManualOpenEvidence(RecipientId,OpenCount,RenderedBody,PreparedBy)
   VALUES(@RecipientId,@OpenCount,@RenderedBody,@UserId);
 IF @ShellOpened=1
 BEGIN
   UPDATE msg.ManualOpenEvidence
   SET LaunchedBy=COALESCE(LaunchedBy,@UserId),LaunchedUtc=COALESCE(LaunchedUtc,SYSUTCDATETIME())
   WHERE RecipientId=@RecipientId AND OpenCount=@OpenCount;
   UPDATE msg.ManualHandoff SET ShellHandoffUtc=COALESCE(ShellHandoffUtc,SYSUTCDATETIME())
   WHERE RecipientId=@RecipientId AND OpenCount=@OpenCount;
 END;
 COMMIT;
END;
');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadMyUnfinishedBatches @UserId int
AS
BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 SELECT B.BatchId,B.Channel,B.Audience,B.CreatedUtc,COUNT_BIG(*) InProgress,
   CONVERT(varchar(40),CASE WHEN M.BatchId IS NOT NULL
     THEN ''WhatsApp Business (manual)'' ELSE A.ProviderCode END) Method,
   S.DueUtc,
   CONVERT(datetime2(3),S.DueUtc AT TIME ZONE ''UTC'' AT TIME ZONE
      (SELECT TimeZoneId FROM msg.DispatchControl WHERE ControlId=1)) DueSchool,
   CONVERT(varchar(20),CASE
     WHEN SUM(CASE WHEN R.State IN (''Unknown'',''Failed'') THEN 1 ELSE 0 END)>0
       OR S.HoldForReview=1 OR (S.ExpiresUtc IS NOT NULL AND S.ExpiresUtc<=SYSUTCDATETIME())
       THEN ''Needs Attention''
     WHEN S.DueUtc>SYSUTCDATETIME() THEN ''Upcoming''
     ELSE ''All Active'' END) QueueStatus
 FROM msg.Batch B JOIN msg.Recipient R ON R.BatchId=B.BatchId
 LEFT JOIN msg.ManualBatch M ON M.BatchId=B.BatchId
 LEFT JOIN msg.ManualHandoff H ON H.RecipientId=R.RecipientId
 LEFT JOIN msg.ApiBatch A ON A.BatchId=B.BatchId
 LEFT JOIN msg.BatchSchedule S ON S.BatchId=B.BatchId
 WHERE (B.CreatedBy=@UserId OR EXISTS(
   SELECT 1 FROM dbo.Users U JOIN dbo.Roles AccessRole ON AccessRole.RoleId=U.RoleId
   WHERE U.UserId=@UserId AND AccessRole.RoleName=N''Administrator'')) AND
   ((M.BatchId IS NOT NULL AND H.State IN (''Pending'',''Opened''))
   OR (A.BatchId IS NOT NULL AND
     (R.State IN (''Pending'',''Unknown'',''Submitted'',''Failed'')
      OR (R.State=''Accepted'' AND NOT
       (A.ProviderCode=''MetaCloud'' AND B.Channel=''WhatsApp'')))))
 GROUP BY B.BatchId,B.Channel,B.Audience,B.CreatedUtc,M.BatchId,A.ProviderCode,
  S.DueUtc,S.HoldForReview,S.ExpiresUtc
 ORDER BY B.CreatedUtc DESC,B.BatchId;
END');

INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
VALUES(N'27',SYSDATETIME(),SUSER_SNAME(),N'Consistent family guardian routing for preview, confirmation and dispatch');

COMMIT TRANSACTION;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
 THROW;
END CATCH;
