-- Schema 10: additive, fail-closed multi-provider groundwork.
-- The installer owns the enclosing SQL transaction and verified recovery backup.
-- Email, Meta Cloud API and SMTP are NOT enabled for production sending.
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF ISNULL((SELECT TOP(1) TRY_CONVERT(int,VersionNumber)
           FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0)<>9
    THROW 51000,'Multi-provider groundwork requires schema 9.',1;
IF SCHEMA_ID(N'msg') IS NULL
   OR OBJECT_ID(N'msg.ChannelSettings',N'U') IS NULL
   OR OBJECT_ID(N'msg.Template',N'U') IS NULL
   OR OBJECT_ID(N'dbo.Students',N'U') IS NULL
   OR OBJECT_ID(N'dbo.Staff',N'U') IS NULL
   OR OBJECT_ID(N'msg.ConfigureChannel',N'P') IS NULL
   OR OBJECT_ID(N'msg.PauseChannel',N'P') IS NULL
    THROW 51000,'Required schema-9 communication objects are missing.',1;
IF COL_LENGTH(N'dbo.Students',N'GuardianEmail') IS NOT NULL
   OR COL_LENGTH(N'dbo.Staff',N'CommunicationEmail') IS NOT NULL
   OR OBJECT_ID(N'msg.EmailSuppression',N'U') IS NOT NULL
   OR EXISTS(SELECT 1 FROM msg.ChannelSettings WHERE Channel='Email')
   OR OBJECT_ID(N'msg.ReadDispatchProvider',N'P') IS NOT NULL
    THROW 51000,'Multi-provider groundwork already exists; inspect before retry.',1;

-- The old guardian communication table is never restored or imported.
-- These optional school-record email fields start empty; no consent is inferred.
ALTER TABLE dbo.Students ADD GuardianEmail nvarchar(254) NULL;
ALTER TABLE dbo.Staff ADD CommunicationEmail nvarchar(254) NULL;

-- Opt-out-only evidence, not a second contact directory.
CREATE TABLE msg.EmailSuppression
(
    EmailAddress nvarchar(254) NOT NULL
        CONSTRAINT PK_MsgEmailSuppression PRIMARY KEY,
    RecordedUtc datetime2(3) NOT NULL
        CONSTRAINT DF_MsgEmailSuppressionUtc DEFAULT SYSUTCDATETIME(),
    Source varchar(16) NOT NULL,
    Reason nvarchar(200) NULL,
    CONSTRAINT CK_MsgEmailSuppressionSource
        CHECK(Source IN ('OptOut','Administrator','Provider')),
    CONSTRAINT CK_MsgEmailSuppressionAddress
        CHECK(LEN(LTRIM(RTRIM(EmailAddress))) BETWEEN 3 AND 254
              AND EmailAddress NOT LIKE N'% %'
              AND EmailAddress NOT LIKE N'%' + NCHAR(10) + N'%'
              AND EmailAddress NOT LIKE N'%' + NCHAR(13) + N'%')
);

-- Keep the existing delivery tables phone-specific until a reviewed email
-- recipient resolver and transactional email claim path exist.
ALTER TABLE msg.ChannelSettings DROP CONSTRAINT CK_MsgChannelName;
ALTER TABLE msg.ChannelSettings ADD CONSTRAINT CK_MsgChannelName
    CHECK(Channel IN ('Sms','WhatsApp','Email'));
INSERT msg.ChannelSettings(Channel,ProviderCode,Enabled)
VALUES('Email',NULL,0);

ALTER TABLE msg.Template DROP CONSTRAINT CK_MsgTemplateChannel;
ALTER TABLE msg.Template ADD CONSTRAINT CK_MsgTemplateChannel
    CHECK(Channel IN ('Sms','WhatsApp','Email'));

-- Register a provider identity without activating it. Only a reviewed
-- activation gate may change Enabled after real transport acceptance.
EXEC(N'ALTER PROCEDURE msg.ConfigureChannel
    @UserId int,@Channel varchar(10),@ProviderCode varchar(40)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
    IF NOT ((@Channel=''Sms'' AND @ProviderCode=''Twilio'')
        OR (@Channel=''WhatsApp'' AND @ProviderCode IN
            (''Twilio'',''MetaCloud'',''WhatsAppBusinessManual''))
        OR (@Channel=''Email'' AND @ProviderCode IN
            (''Smtp'',''EmailClientManual'')))
        THROW 51098,''Unsupported approved messaging provider profile.'',1;
    UPDATE msg.ChannelSettings
      SET ProviderCode=@ProviderCode,Enabled=0,
          UpdatedBy=@UserId,UpdatedUtc=SYSUTCDATETIME()
    WHERE Channel=@Channel;
    IF @@ROWCOUNT<>1 THROW 51098,''Channel configuration was not found.'',1;
END;');

EXEC(N'ALTER PROCEDURE msg.PauseChannel
    @UserId int,@Channel varchar(10)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
    IF @Channel NOT IN (''Sms'',''WhatsApp'',''Email'')
        THROW 51088,''Unknown channel.'',1;
    UPDATE msg.ChannelSettings
       SET Enabled=0,UpdatedBy=@UserId,UpdatedUtc=SYSUTCDATETIME()
    WHERE Channel=@Channel;
    IF @@ROWCOUNT<>1 THROW 51088,''Channel setting was not found.'',1;
END;');

-- Senders can read the currently enabled provider identity without
-- receiving administrator-only settings or provider credentials.
-- Unknown/unimplemented providers fail before the SQL claim in C#.
EXEC(N'CREATE PROCEDURE msg.ReadDispatchProvider
    @UserId int,@Channel varchar(10)
AS
BEGIN
    SET NOCOUNT ON;
    EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
    IF @Channel NOT IN (''Sms'',''WhatsApp'')
        THROW 51092,''This channel has no approved live dispatcher.'',1;
    SELECT Channel,ProviderCode FROM msg.ChannelSettings
    WHERE Channel=@Channel AND Enabled=1
      AND NULLIF(LTRIM(RTRIM(ProviderCode)),'''') IS NOT NULL;
    IF @@ROWCOUNT<>1
        THROW 51092,''Channel is paused or has no approved provider.'',1;
END;');

-- SQL can store future email templates; the current desktop does not expose
-- Email in Compose, ConfirmBatch, ClaimRecipient or the transport registry.
EXEC(N'ALTER PROCEDURE msg.SaveTemplate
 @UserId int,@TemplateId int=0,@Name nvarchar(100),
 @Channel varchar(10),@Purpose varchar(20),@Body nvarchar(1600),@Active bit=1
AS
BEGIN
 SET NOCOUNT ON;SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 IF @TemplateId<0 OR LEN(LTRIM(RTRIM(ISNULL(@Name,N''''))))=0
    OR LEN(@Name)>100 OR @Channel NOT IN (''Sms'',''WhatsApp'',''Email'')
    OR @Purpose NOT IN (''Announcement'',''FeeReminder'',''Individual'',''Staff'')
    OR LEN(LTRIM(RTRIM(ISNULL(@Body,N''''))))=0 OR LEN(@Body)>1600
    OR @Body LIKE N''%{{%'' OR @Body LIKE N''%}}%''
    THROW 51089,''Invalid message template.'',1;
 IF @TemplateId=0
 BEGIN
     INSERT msg.Template(Name,Channel,Purpose,Body,Active,UpdatedBy)
     VALUES(@Name,@Channel,@Purpose,@Body,@Active,@UserId);
     SELECT CONVERT(int,SCOPE_IDENTITY()) TemplateId;
 END
 ELSE
 BEGIN
     UPDATE msg.Template SET Name=@Name,Channel=@Channel,
         Purpose=@Purpose,Body=@Body,Active=@Active,
         UpdatedBy=@UserId,UpdatedUtc=SYSUTCDATETIME()
     WHERE TemplateId=@TemplateId;
     IF @@ROWCOUNT<>1 THROW 51089,''The template no longer exists.'',1;
     SELECT @TemplateId TemplateId;
 END
END;');


-- Free WhatsApp Business: the administrator must explicitly enable this
-- OPERATOR handoff mode. It does not invoke an API, schedule jobs or send
-- anything automatically.
CREATE TABLE msg.ManualBatch
(
    BatchId uniqueidentifier NOT NULL CONSTRAINT PK_MsgManualBatch PRIMARY KEY,
    CreatedBy int NOT NULL,
    CreatedUtc datetime2(3) NOT NULL
        CONSTRAINT DF_MsgManualBatchCreated DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_MsgManualBatchBatch FOREIGN KEY(BatchId) REFERENCES msg.Batch(BatchId)
);
CREATE TABLE msg.ManualHandoff
(
    RecipientId bigint NOT NULL CONSTRAINT PK_MsgManualHandoff PRIMARY KEY,
    State varchar(16) NOT NULL
        CONSTRAINT DF_MsgManualHandoffPending DEFAULT('Pending'),
    OpenCount int NOT NULL CONSTRAINT DF_MsgManualOpenCount DEFAULT(0),
    LastOpenedUtc datetime2(3) NULL,
    ReportedUtc datetime2(3) NULL,
    ChangedBy int NULL,
    UpdatedUtc datetime2(3) NOT NULL
        CONSTRAINT DF_MsgManualHandoffUpdated DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_MsgManualHandoffRecipient
        FOREIGN KEY(RecipientId) REFERENCES msg.Recipient(RecipientId),
    CONSTRAINT CK_MsgManualHandoffState
        CHECK(State IN ('Pending','Opened','ReportedSent','Skipped','Excluded')),
    CONSTRAINT CK_MsgManualHandoffOpenCount CHECK(OpenCount>=0),
    CONSTRAINT CK_MsgManualHandoffReported
        CHECK(State<>'ReportedSent' OR (LastOpenedUtc IS NOT NULL AND ReportedUtc IS NOT NULL))
);
CREATE INDEX IX_MsgManualHandoffState
    ON msg.ManualHandoff(State,UpdatedUtc) INCLUDE(RecipientId);

EXEC(N'CREATE PROCEDURE msg.EnableManualWhatsApp @UserId int
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
    BEGIN TRANSACTION;
    IF EXISTS(
        SELECT 1 FROM msg.Batch B WITH(UPDLOCK,HOLDLOCK)
        JOIN msg.Recipient R WITH(HOLDLOCK) ON R.BatchId=B.BatchId
        WHERE B.Channel=''WhatsApp''
          AND R.State IN (''Pending'',''Unknown'',''Submitted'',''Accepted'')
          AND NOT EXISTS(SELECT 1 FROM msg.ManualBatch M WHERE M.BatchId=B.BatchId))
        THROW 51111,''Resolve older outstanding WhatsApp provider batches before switching delivery methods.'',1;
    UPDATE msg.ChannelSettings
       SET ProviderCode=''WhatsAppBusinessManual'',Enabled=1,
           UpdatedBy=@UserId,UpdatedUtc=SYSUTCDATETIME()
    WHERE Channel=''WhatsApp'' AND Enabled=0;
    IF @@ROWCOUNT<>1
        THROW 51111,''Pause WhatsApp before enabling the free manual method.'',1;
    COMMIT TRANSACTION;
END;');


EXEC(N'CREATE PROCEDURE msg.ReadManualWhatsAppMode @UserId int
AS
BEGIN
    SET NOCOUNT ON;
    EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
    SELECT CONVERT(bit,CASE WHEN EXISTS(
      SELECT 1 FROM msg.ChannelSettings
      WHERE Channel=''WhatsApp'' AND ProviderCode=''WhatsAppBusinessManual'' AND Enabled=1)
      THEN 1 ELSE 0 END) Enabled;
END;');


EXEC(N'CREATE PROCEDURE msg.ConfirmManualBatch
    @UserId int,@ClientRequestId uniqueidentifier,@Channel varchar(10),
    @Purpose varchar(20),@Body nvarchar(1600),@Audience varchar(20),
    @CampusId int=0,@ClassId int=0,@StudentId int=0,@StaffId int=0,
    @Recipients msg.RecipientSelection READONLY
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
    IF @Channel<>''WhatsApp''
        THROW 51112,''Only WhatsApp is supported in the free manual mode.'',1;
    BEGIN TRANSACTION;
    IF NOT EXISTS(SELECT 1 FROM msg.ChannelSettings WITH(UPDLOCK,HOLDLOCK)
       WHERE Channel=''WhatsApp'' AND ProviderCode=''WhatsAppBusinessManual'' AND Enabled=1)
        THROW 51112,''Free WhatsApp handoff must be enabled by an administrator.'',1;
    DECLARE @Result TABLE(BatchId uniqueidentifier,RecipientCount bigint,AlreadyConfirmed bit);
    INSERT @Result(BatchId,RecipientCount,AlreadyConfirmed)
    EXEC msg.ConfirmBatch @UserId=@UserId,@ClientRequestId=@ClientRequestId,
        @Channel=@Channel,@Purpose=@Purpose,@Body=@Body,@Audience=@Audience,
        @CampusId=@CampusId,@ClassId=@ClassId,@StudentId=@StudentId,@StaffId=@StaffId,
        @Recipients=@Recipients;
    IF (SELECT COUNT(*) FROM @Result)<>1
        THROW 51112,''Manual batch confirmation did not return one result.'',1;
    DECLARE @BatchId uniqueidentifier=(SELECT TOP(1) BatchId FROM @Result);
    IF EXISTS(SELECT 1 FROM msg.Attempt A
        JOIN msg.Recipient R ON R.RecipientId=A.RecipientId WHERE R.BatchId=@BatchId)
        THROW 51112,''An API batch cannot be converted into a manual handoff.'',1;
    IF NOT EXISTS(SELECT 1 FROM msg.ManualBatch WITH(UPDLOCK,HOLDLOCK) WHERE BatchId=@BatchId)
        INSERT msg.ManualBatch(BatchId,CreatedBy) VALUES(@BatchId,@UserId);
    INSERT msg.ManualHandoff(RecipientId)
    SELECT R.RecipientId FROM msg.Recipient R
    WHERE R.BatchId=@BatchId AND NOT EXISTS(
        SELECT 1 FROM msg.ManualHandoff H WITH(UPDLOCK,HOLDLOCK)
        WHERE H.RecipientId=R.RecipientId);
    SELECT BatchId,RecipientCount,AlreadyConfirmed FROM @Result;
    COMMIT TRANSACTION;
END;');


EXEC(N'CREATE PROCEDURE msg.ReadManualHandoff @UserId int,@BatchId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
    IF NOT EXISTS(SELECT 1 FROM msg.ManualBatch WHERE BatchId=@BatchId)
        THROW 51113,''This is not a manual WhatsApp batch.'',1;
    DECLARE @Audience varchar(20);
    SELECT @Audience=B.Audience FROM msg.Batch B WHERE B.BatchId=@BatchId;
    IF @Audience=''Staff'' EXEC dbo.RequirePermission @UserId,N''Staff.View'';
    ELSE EXEC dbo.RequirePermission @UserId,N''Students.View'';
    IF @Audience=''OutstandingFees'' EXEC dbo.RequirePermission @UserId,N''Fees.View'';
    SELECT R.RecipientId,H.State,
        COALESCE(S.StudentName,T.StaffName,N''School record'') RecipientName,
        RIGHT(R.PhoneE164,4) PhoneLast4,
        H.OpenCount,H.LastOpenedUtc,H.ReportedUtc
    FROM msg.ManualHandoff H
    JOIN msg.Recipient R ON R.RecipientId=H.RecipientId
    LEFT JOIN dbo.Students S ON S.AdmissionId=R.AdmissionId
    LEFT JOIN dbo.Staff T ON T.StaffId=R.StaffId
    WHERE R.BatchId=@BatchId ORDER BY R.RecipientId;
END;');


EXEC(N'CREATE PROCEDURE msg.OpenManualHandoff @UserId int,@RecipientId bigint
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
    IF @RecipientId IS NULL OR @RecipientId<=0 THROW 51114,''Invalid manual recipient.'',1;
    BEGIN TRANSACTION;
    DECLARE @BatchId uniqueidentifier,@State varchar(16),@Kind varchar(8),
        @AdmissionId int,@StaffId int,@Snapshot varchar(16),@Live varchar(16),
        @Audience varchar(20),@CampusId int,@ClassId int,@StudentId int,@TargetStaff int,
        @Body nvarchar(1600),@Excluded varchar(32);
    SELECT @BatchId=R.BatchId,@State=H.State,@Kind=R.RecipientKind,
        @AdmissionId=R.AdmissionId,@StaffId=R.StaffId,
        @Snapshot=R.PhoneE164,@Body=B.Body,@Audience=B.Audience,
        @CampusId=B.CampusId,@ClassId=B.ClassId,@StudentId=B.StudentId,@TargetStaff=B.StaffId
    FROM msg.ManualHandoff H WITH(UPDLOCK,HOLDLOCK)
    JOIN msg.Recipient R WITH(UPDLOCK,HOLDLOCK) ON R.RecipientId=H.RecipientId
    JOIN msg.ManualBatch M ON M.BatchId=R.BatchId
    JOIN msg.Batch B ON B.BatchId=R.BatchId
    WHERE H.RecipientId=@RecipientId AND B.Channel=''WhatsApp''
      AND R.State=''Pending'' AND B.State IN (''Confirmed'',''Sending'');
    IF @BatchId IS NULL OR @State NOT IN (''Pending'',''Opened'')
        THROW 51114,''This handoff was already processed, cancelled or excluded.'',1;
    IF NOT EXISTS(SELECT 1 FROM msg.ChannelSettings WITH(HOLDLOCK)
       WHERE Channel=''WhatsApp'' AND ProviderCode=''WhatsAppBusinessManual'' AND Enabled=1)
        THROW 51114,''Free WhatsApp handoff is paused or no longer configured.'',1;
    IF @Kind=''Staff'' EXEC dbo.RequirePermission @UserId,N''Staff.View'';
    ELSE EXEC dbo.RequirePermission @UserId,N''Students.View'';
    IF @Audience=''OutstandingFees'' EXEC dbo.RequirePermission @UserId,N''Fees.View'';

    -- Source and scope checks mirror msg.ClaimRecipient. No snapshot is
    -- usable when admission consent, placement, phone or opt-out changes.
    IF @Kind=''Staff''
    BEGIN
        IF @Audience=''Staff'' AND @TargetStaff=@StaffId
            SELECT @Live=msg.NormalizeMobile(T.ContactNumber)
            FROM dbo.Staff T WITH(HOLDLOCK)
            WHERE T.StaffId=@StaffId AND T.Status=N''Active'';
    END
    ELSE
    BEGIN
        SELECT @Live=msg.NormalizeMobile(S.ContactNumber)
        FROM dbo.Students S WITH(HOLDLOCK)
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
          AND S.SchoolMessageConsent=1 AND
          (@Audience=''School''
            OR (@Audience=''Campus'' AND E.Status=N''Active'' AND CCS.CampusId=@CampusId)
            OR (@Audience=''Class'' AND E.Status=N''Active'' AND CCS.CampusId=@CampusId
                AND CCS.ClassId=@ClassId)
            OR (@Audience=''OutstandingFees'' AND E.Status=N''Active''
                AND (@CampusId=0 OR CCS.CampusId=@CampusId)
                AND (@ClassId=0 OR CCS.ClassId=@ClassId) AND F.Outstanding>0)
            OR (@Audience=''Student'' AND S.AdmissionId=@StudentId)
            OR (@Audience=''Family'' AND
                (S.AdmissionId=@StudentId OR EXISTS(
                 SELECT 1 FROM dbo.FamilyMembers FM WITH(HOLDLOCK)
                 WHERE FM.AdmissionId=S.AdmissionId AND FM.FamilyId IN (
                    SELECT X.FamilyId FROM dbo.FamilyMembers X WITH(HOLDLOCK)
                    WHERE X.AdmissionId=@StudentId)))));
    END;
    SET @Excluded=CASE
      WHEN @Live IS NULL THEN ''NoLongerEligible''
      WHEN @Live<>@Snapshot THEN ''DestinationChanged''
      WHEN EXISTS(SELECT 1 FROM msg.Suppression O WITH(HOLDLOCK)
                  WHERE O.Channel=''WhatsApp'' AND O.PhoneE164=@Live)
           THEN ''ChannelOptOut''
      ELSE NULL END;
    IF @Excluded IS NOT NULL
    BEGIN
        UPDATE msg.ManualHandoff SET State=''Excluded'',UpdatedUtc=SYSUTCDATETIME(),
            ChangedBy=@UserId WHERE RecipientId=@RecipientId;
        UPDATE msg.Recipient SET State=''Excluded'',ExclusionCode=@Excluded,
            UpdatedUtc=SYSUTCDATETIME() WHERE RecipientId=@RecipientId;
        SELECT CONVERT(bit,0) Opened,CONVERT(varchar(16),NULL) PhoneE164,
          CONVERT(nvarchar(1600),NULL) Body,@Excluded ExclusionCode;
        COMMIT TRANSACTION; RETURN;
    END;
    UPDATE msg.ManualHandoff SET State=''Opened'',OpenCount=OpenCount+1,
        LastOpenedUtc=SYSUTCDATETIME(),ChangedBy=@UserId,UpdatedUtc=SYSUTCDATETIME()
    WHERE RecipientId=@RecipientId;
    SELECT CONVERT(bit,1) Opened,@Live PhoneE164,@Body Body,
        CONVERT(varchar(32),NULL) ExclusionCode;
    COMMIT TRANSACTION;
END;');


EXEC(N'CREATE PROCEDURE msg.MarkManualHandoff
    @UserId int,@RecipientId bigint,@Outcome varchar(16)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
    IF @RecipientId IS NULL OR @RecipientId<=0
       OR @Outcome NOT IN (''ReportedSent'',''Skipped'')
        THROW 51115,''Unknown manual handoff outcome.'',1;
    BEGIN TRANSACTION;
    DECLARE @BatchId uniqueidentifier,@State varchar(16),@Audience varchar(20);
    SELECT @State=H.State,@BatchId=R.BatchId,@Audience=B.Audience
    FROM msg.ManualHandoff H WITH(UPDLOCK,HOLDLOCK)
    JOIN msg.Recipient R ON R.RecipientId=H.RecipientId
    JOIN msg.ManualBatch M ON M.BatchId=R.BatchId
    JOIN msg.Batch B ON B.BatchId=R.BatchId
    WHERE H.RecipientId=@RecipientId;
    IF @BatchId IS NULL OR (@State=''Pending'' AND @Outcome=''ReportedSent'')
       OR (@State NOT IN (''Pending'',''Opened'') AND @State<>@Outcome)
        THROW 51115,''Open a chat before reporting it sent; completed outcomes cannot be overwritten.'',1;
    IF @Audience=''Staff'' EXEC dbo.RequirePermission @UserId,N''Staff.View'';
    ELSE EXEC dbo.RequirePermission @UserId,N''Students.View'';
    IF @Audience=''OutstandingFees'' EXEC dbo.RequirePermission @UserId,N''Fees.View'';
    IF @State<>@Outcome
      UPDATE msg.ManualHandoff SET State=@Outcome,ChangedBy=@UserId,
        ReportedUtc=CASE WHEN @Outcome=''ReportedSent'' THEN SYSUTCDATETIME() ELSE NULL END,
        UpdatedUtc=SYSUTCDATETIME() WHERE RecipientId=@RecipientId;
    IF NOT EXISTS(SELECT 1 FROM msg.ManualHandoff H
          JOIN msg.Recipient R ON R.RecipientId=H.RecipientId
          WHERE R.BatchId=@BatchId AND H.State IN (''Pending'',''Opened''))
      UPDATE msg.Batch SET State=''Completed'' WHERE BatchId=@BatchId AND State=''Confirmed'';
    COMMIT TRANSACTION;
END;');


EXEC(N'ALTER PROCEDURE msg.ReadHistory @UserId int,@Limit int=100
AS
BEGIN
  SET NOCOUNT ON;
  EXEC dbo.RequirePermission @UserId,N''Communication.View'';
  IF @Limit<1 OR @Limit>200 THROW 51087,''History page size must be between 1 and 200.'',1;
  SELECT TOP(@Limit) B.BatchId,B.Channel,B.Purpose,B.State,
    B.CreatedUtc,B.ConfirmedUtc,B.CreatedBy,
    COUNT(R.RecipientId) Destinations,
    SUM(CASE WHEN M.BatchId IS NULL AND R.State=''Delivered'' THEN 1 ELSE 0 END) Delivered,
    SUM(CASE WHEN M.BatchId IS NULL AND R.State=''Failed'' THEN 1 ELSE 0 END) Failed,
    SUM(CASE WHEN M.BatchId IS NULL AND R.State=''Unknown'' THEN 1 ELSE 0 END) Unknown,
    SUM(CASE WHEN M.BatchId IS NULL AND R.State IN (''Pending'',''Submitted'',''Accepted'')
       OR M.BatchId IS NOT NULL AND H.State IN (''Pending'',''Opened'') THEN 1 ELSE 0 END) InProgress,
    CASE WHEN M.BatchId IS NOT NULL THEN ''WhatsApp Business (manual)''
         ELSE ''Direct provider'' END Method,
    SUM(CASE WHEN H.State=''ReportedSent'' THEN 1 ELSE 0 END) ManualReported,
    SUM(CASE WHEN H.State=''Opened'' THEN 1 ELSE 0 END) ManualOpened,
    SUM(CASE WHEN H.State=''Skipped'' THEN 1 ELSE 0 END) ManualSkipped
  FROM msg.Batch B
  LEFT JOIN msg.Recipient R ON R.BatchId=B.BatchId
  LEFT JOIN msg.ManualBatch M ON M.BatchId=B.BatchId
  LEFT JOIN msg.ManualHandoff H ON H.RecipientId=R.RecipientId
  GROUP BY B.BatchId,B.Channel,B.Purpose,B.State,
      B.CreatedUtc,B.ConfirmedUtc,B.CreatedBy,M.BatchId
  ORDER BY B.CreatedUtc DESC;
END;');

-- Even if a caller bypasses the C# adapter registry, a free/manual provider
-- must never reserve an HTTP attempt.
EXEC(N'ALTER PROCEDURE msg.ReadDispatchProvider
    @UserId int,@Channel varchar(10)
AS
BEGIN
    SET NOCOUNT ON;
    EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
    IF @Channel NOT IN (''Sms'',''WhatsApp'')
        THROW 51092,''This channel has no approved live dispatcher.'',1;
    SELECT Channel,ProviderCode FROM msg.ChannelSettings
    WHERE Channel=@Channel AND Enabled=1
      AND ProviderCode NOT IN (''WhatsAppBusinessManual'',''EmailClientManual'')
      AND NULLIF(LTRIM(RTRIM(ProviderCode)),'''') IS NOT NULL;
    IF @@ROWCOUNT<>1
        THROW 51092,''Channel is paused, manual-only or has no approved direct provider.'',1;
END;');


-- Database-side last defense: a manually handled message can never enter
-- the provider-attempt table even through an unsafe direct SQL caller.
EXEC(N'CREATE TRIGGER msg.TR_Attempt_RejectManualProvider
ON msg.Attempt AFTER INSERT AS
BEGIN
  SET NOCOUNT ON;
  IF EXISTS(SELECT 1 FROM inserted
       WHERE ProviderCode IN (''WhatsAppBusinessManual'',''EmailClientManual''))
     THROW 51116,''Operator handoff cannot create provider API attempts.'',1;
END;');

-- Existing school-message consent remains the sole authority.
-- No email recipient is created or dispatched by this migration.
INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
VALUES(N'10',SYSDATETIME(),SUSER_SNAME(),
       N'Prepare disabled multi-provider channels and optional school-record email');
