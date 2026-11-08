-- Schema 9: clean Communication Center foundation, independent of retired comm objects.
-- The installer owns the outer transaction, pre-migration backup and version checks.
-- No messages, contacts, consent or provider credentials are imported from the retired system.
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF ISNULL((SELECT TOP (1) TRY_CONVERT(int,VersionNumber)
           FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0) <> 8
    THROW 51000, 'Communication foundation requires schema 8.', 1;
IF SCHEMA_ID(N'comm') IS NOT NULL OR OBJECT_ID(N'dbo.StudentGuardianCommunication',N'U') IS NOT NULL
    THROW 51000, 'Legacy communication objects remain. Review retirement before rebuilding.', 1;
IF SCHEMA_ID(N'msg') IS NOT NULL
    THROW 51000, 'The replacement messaging schema already exists; inspect before proceeding.', 1;
IF COL_LENGTH(N'dbo.Students', N'SchoolMessageConsent') IS NOT NULL
   OR COL_LENGTH(N'dbo.Students', N'SchoolMessageConsentUtc') IS NOT NULL
   OR COL_LENGTH(N'dbo.Students', N'SchoolMessageConsentBy') IS NOT NULL
    THROW 51000, 'Student consent fields already exist; inspect before proceeding.', 1;

-- One authoritative Yes/No on the existing admission record. NULL = no choice recorded.
-- Never infer consent from historical WhatsApp choices, stored phone numbers or family links.
ALTER TABLE dbo.Students ADD
    SchoolMessageConsent bit NULL,
    SchoolMessageConsentUtc datetime2(3) NULL,
    SchoolMessageConsentBy int NULL;
-- SQL Server binds all statements in a batch before executing ALTER TABLE.
-- Compile the constraint only after the new columns exist (no GO in installer).
EXEC sys.sp_executesql N'
ALTER TABLE dbo.Students ADD CONSTRAINT CK_Students_SchoolMessageConsentEvidence
    CHECK ((SchoolMessageConsent IS NULL AND SchoolMessageConsentUtc IS NULL AND SchoolMessageConsentBy IS NULL)
        OR (SchoolMessageConsent IS NOT NULL AND SchoolMessageConsentUtc IS NOT NULL AND SchoolMessageConsentBy IS NOT NULL));';

EXEC(N'CREATE SCHEMA msg AUTHORIZATION dbo;');

-- Recorded consent changes are evidence only; dbo.Students is the authority.
-- No standalone guardian/contact preference directory is created.
CREATE TABLE msg.ConsentAudit
(
    ConsentAuditId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_MsgConsentAudit PRIMARY KEY,
    AdmissionId int NOT NULL,
    Consent bit NOT NULL,
    ActorUserId int NOT NULL,
    RecordedUtc datetime2(3) NOT NULL CONSTRAINT DF_MsgConsentAuditUtc DEFAULT SYSUTCDATETIME()
);
CREATE INDEX IX_MsgConsentAudit_Student ON msg.ConsentAudit(AdmissionId,ConsentAuditId DESC);

-- Provider credentials MUST stay outside SQL in the protected local credential store.
-- The default is disabled and no background sender is installed by this migration.
CREATE TABLE msg.ChannelSettings
(
    Channel varchar(10) NOT NULL CONSTRAINT PK_MsgChannelSettings PRIMARY KEY,
    ProviderCode varchar(40) NULL,
    Enabled bit NOT NULL CONSTRAINT DF_MsgChannelDisabled DEFAULT (0),
    UpdatedBy int NULL,
    UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_MsgChannelUpdated DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_MsgChannelName CHECK (Channel IN ('Sms','WhatsApp')),
    CONSTRAINT CK_MsgChannelProvider CHECK (Enabled=0 OR (ProviderCode IS NOT NULL AND LEN(LTRIM(RTRIM(ProviderCode)))>0))
);
INSERT msg.ChannelSettings(Channel,Enabled) VALUES ('Sms',0),('WhatsApp',0);

-- A channel-specific suppression is not a contact directory. Only opt-outs are retained.
CREATE TABLE msg.Suppression
(
    Channel varchar(10) NOT NULL,
    PhoneE164 varchar(16) NOT NULL,
    RecordedUtc datetime2(3) NOT NULL CONSTRAINT DF_MsgSuppressionUtc DEFAULT SYSUTCDATETIME(),
    Source varchar(16) NOT NULL,
    Reason nvarchar(200) NULL,
    CONSTRAINT PK_MsgSuppression PRIMARY KEY(Channel,PhoneE164),
    CONSTRAINT CK_MsgSuppressionChannel CHECK (Channel IN ('Sms','WhatsApp')),
    CONSTRAINT CK_MsgSuppressionPhone CHECK (PhoneE164 LIKE '+[1-9]%' AND LEN(PhoneE164) BETWEEN 8 AND 16),
    CONSTRAINT CK_MsgSuppressionSource CHECK (Source IN ('OptOut','Administrator','Provider'))
);

CREATE TABLE msg.Template
(
    TemplateId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_MsgTemplate PRIMARY KEY,
    Name nvarchar(100) NOT NULL,
    Channel varchar(10) NOT NULL,
    Purpose varchar(20) NOT NULL,
    Body nvarchar(1600) NOT NULL,
    Active bit NOT NULL CONSTRAINT DF_MsgTemplateActive DEFAULT(1),
    UpdatedBy int NOT NULL,
    UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_MsgTemplateUpdated DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_MsgTemplateName UNIQUE(Name,Channel),
    CONSTRAINT CK_MsgTemplateChannel CHECK (Channel IN ('Sms','WhatsApp')),
    CONSTRAINT CK_MsgTemplatePurpose CHECK (Purpose IN ('Announcement','FeeReminder','Individual','Staff')),
    CONSTRAINT CK_MsgTemplateBody CHECK (LEN(LTRIM(RTRIM(Body)))>0)
);

-- ClientRequestId is a stable, unique idempotency key for the operator's Confirm click.
-- A batch never sends merely because it was inserted, confirmed or reopened.
CREATE TABLE msg.Batch
(
    BatchId uniqueidentifier NOT NULL CONSTRAINT PK_MsgBatch PRIMARY KEY,
    ClientRequestId uniqueidentifier NOT NULL CONSTRAINT UQ_MsgBatchRequest UNIQUE,
    Channel varchar(10) NOT NULL,
    Purpose varchar(20) NOT NULL,
    Body nvarchar(1600) NOT NULL,
    State varchar(14) NOT NULL CONSTRAINT DF_MsgBatchState DEFAULT('Draft'),
    CreatedBy int NOT NULL,
    CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_MsgBatchCreated DEFAULT SYSUTCDATETIME(),
    ConfirmedBy int NULL,
    ConfirmedUtc datetime2(3) NULL,
    CONSTRAINT CK_MsgBatchChannel CHECK (Channel IN ('Sms','WhatsApp')),
    CONSTRAINT CK_MsgBatchPurpose CHECK (Purpose IN ('Announcement','FeeReminder','Individual','Staff')),
    CONSTRAINT CK_MsgBatchState CHECK (State IN ('Draft','Confirmed','Sending','Completed','Partial','Cancelled')),
    CONSTRAINT CK_MsgBatchBody CHECK (LEN(LTRIM(RTRIM(Body)))>0),
    CONSTRAINT CK_MsgBatchConfirmEvidence CHECK (
        (ConfirmedBy IS NULL AND ConfirmedUtc IS NULL) OR
        (ConfirmedBy IS NOT NULL AND ConfirmedUtc IS NOT NULL))
);
CREATE INDEX IX_MsgBatchHistory ON msg.Batch(CreatedUtc DESC) INCLUDE(Channel,Purpose,State,CreatedBy);

-- Destination is a per-send snapshot for audit, NOT a reusable address book.
-- Link to school records without new FKs that could block legitimate record deletion;
-- the dispatcher must re-resolve live records and consent before every attempt.
CREATE TABLE msg.Recipient
(
    RecipientId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_MsgRecipient PRIMARY KEY,
    BatchId uniqueidentifier NOT NULL,
    RecipientKind varchar(8) NOT NULL,
    AdmissionId int NULL,
    StaffId int NULL,
    PhoneE164 varchar(16) NOT NULL,
    State varchar(12) NOT NULL CONSTRAINT DF_MsgRecipientState DEFAULT('Pending'),
    ExclusionCode varchar(32) NULL,
    UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_MsgRecipientUpdated DEFAULT SYSUTCDATETIME(),
    Concurrency rowversion,
    CONSTRAINT FK_MsgRecipientBatch FOREIGN KEY(BatchId) REFERENCES msg.Batch(BatchId),
    CONSTRAINT CK_MsgRecipientKind CHECK (
        (RecipientKind='Student' AND AdmissionId IS NOT NULL AND StaffId IS NULL) OR
        (RecipientKind='Staff' AND StaffId IS NOT NULL AND AdmissionId IS NULL)),
    CONSTRAINT CK_MsgRecipientState CHECK
        (State IN ('Pending','Submitted','Accepted','Delivered','Failed','Unknown','Excluded')),
    CONSTRAINT CK_MsgRecipientPhone CHECK (PhoneE164 LIKE '+[1-9]%' AND LEN(PhoneE164) BETWEEN 8 AND 16),
    CONSTRAINT CK_MsgRecipientExclusion CHECK (State='Excluded' OR ExclusionCode IS NULL)
);
CREATE UNIQUE INDEX UX_MsgRecipientStudent ON msg.Recipient(BatchId,AdmissionId)
    WHERE AdmissionId IS NOT NULL;
CREATE UNIQUE INDEX UX_MsgRecipientStaff ON msg.Recipient(BatchId,StaffId)
    WHERE StaffId IS NOT NULL;
CREATE INDEX IX_MsgRecipientQueue ON msg.Recipient(State,UpdatedUtc) INCLUDE(BatchId,RecipientKind,AdmissionId,StaffId);

-- Each external request receives a unique stable attempt key. An unknown outcome
-- must NOT be retried automatically: a provider may already have sent the message.
CREATE TABLE msg.Attempt
(
    AttemptId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_MsgAttempt PRIMARY KEY,
    RecipientId bigint NOT NULL,
    AttemptNumber int NOT NULL,
    AttemptKey uniqueidentifier NOT NULL,
    Channel varchar(10) NOT NULL,
    ProviderCode varchar(40) NOT NULL,
    Outcome varchar(12) NOT NULL,
    RequestUtc datetime2(3) NOT NULL CONSTRAINT DF_MsgAttemptRequested DEFAULT SYSUTCDATETIME(),
    ResponseUtc datetime2(3) NULL,
    ProviderMessageId nvarchar(150) NULL,
    FailureCode varchar(50) NULL,
    FailureDetail nvarchar(200) NULL,
    CONSTRAINT FK_MsgAttemptRecipient FOREIGN KEY(RecipientId) REFERENCES msg.Recipient(RecipientId),
    CONSTRAINT UQ_MsgAttemptOrdinal UNIQUE(RecipientId,AttemptNumber),
    CONSTRAINT UQ_MsgAttemptKey UNIQUE(AttemptKey),
    CONSTRAINT CK_MsgAttemptOrdinal CHECK (AttemptNumber>0),
    CONSTRAINT CK_MsgAttemptChannel CHECK(Channel IN ('Sms','WhatsApp')),
    CONSTRAINT CK_MsgAttemptOutcome CHECK
        (Outcome IN ('Pending','Submitted','Accepted','Delivered','Failed','Unknown'))
);
CREATE INDEX IX_MsgAttemptProviderId ON msg.Attempt(ProviderCode,ProviderMessageId)
    WHERE ProviderMessageId IS NOT NULL;

-- Callback identity deduplicates webhook replays; record only verified, sanitized
-- provider outcomes. Callback ingestion is not enabled by migration alone.
CREATE TABLE msg.Receipt
(
    ReceiptId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_MsgReceipt PRIMARY KEY,
    ProviderCode varchar(40) NOT NULL,
    EventId nvarchar(128) NOT NULL,
    ProviderMessageId nvarchar(150) NOT NULL,
    Outcome varchar(12) NOT NULL,
    ReceivedUtc datetime2(3) NOT NULL CONSTRAINT DF_MsgReceiptReceived DEFAULT SYSUTCDATETIME(),
    AppliedAttemptId bigint NULL,
    CONSTRAINT UQ_MsgReceiptReplay UNIQUE(ProviderCode,EventId),
    CONSTRAINT FK_MsgReceiptAttempt FOREIGN KEY(AppliedAttemptId) REFERENCES msg.Attempt(AttemptId),
    CONSTRAINT CK_MsgReceiptOutcome CHECK(Outcome IN ('Accepted','Delivered','Failed','Unknown'))
);
CREATE INDEX IX_MsgReceiptCorrelation ON msg.Receipt(ProviderCode,ProviderMessageId);

-- Permission-checked consent recording. This procedure is idempotent and records
-- evidence only when the authoritative Admission choice actually changes.
EXEC sys.sp_executesql N'
CREATE PROCEDURE msg.RecordAdmissionConsent
    @UserId int, @AdmissionId int, @Consent bit, @Authority nvarchar(40)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @Consent IS NULL OR @AdmissionId<=0 OR @Authority NOT IN (N''Students.Admit'',N''Students.Administer'')
        THROW 51012,''An explicit Yes/No and an authorized admission are required.'',1;
    EXEC dbo.RequirePermission @UserId,@Authority;
    BEGIN TRANSACTION;
    DECLARE @Old bit;
    SELECT @Old=SchoolMessageConsent
    FROM dbo.Students WITH (UPDLOCK,HOLDLOCK) WHERE AdmissionId=@AdmissionId;
    IF @@ROWCOUNT<>1
        THROW 51012,''The admission was not found. Consent was not recorded.'',1;
    IF @Old IS NULL OR @Old<>@Consent
    BEGIN
        UPDATE dbo.Students
        SET SchoolMessageConsent=@Consent,
            SchoolMessageConsentUtc=SYSUTCDATETIME(),
            SchoolMessageConsentBy=@UserId
        WHERE AdmissionId=@AdmissionId;
        INSERT msg.ConsentAudit(AdmissionId,Consent,ActorUserId)
        VALUES(@AdmissionId,@Consent,@UserId);
    END;
    COMMIT TRANSACTION;
END;';


-- Messaging authority is distinct from ordinary student/fee viewing.
-- No role receives a new grant implicitly; Administrator's existing
-- all-permissions policy remains the separate school owner decision.
INSERT dbo.Permissions(PermissionCode,PermissionName,Description)
SELECT V.Code,V.Name,V.Description
FROM (VALUES
 (N'Communication.View',N'View Communication History',N'Read communication history and delivery states.'),
 (N'Communication.Send',N'Confirm School Messages',N'Authorize explicit, reviewed manual communication batches.'),
 (N'Communication.Configure',N'Configure Communication',N'Manage templates, providers and pause settings.')
) V(Code,Name,Description)
WHERE NOT EXISTS(SELECT 1 FROM dbo.Permissions P WHERE P.PermissionCode=V.Code);

-- A snapshot of the exact review list. SQL must independently derive and
-- compare the complete eligible audience, not trust client-controlled IDs.
CREATE TYPE msg.RecipientSelection AS TABLE
(
    RecipientKind varchar(8) NOT NULL,
    RecordId int NOT NULL,
    PhoneE164 varchar(16) NOT NULL,
    PRIMARY KEY(RecipientKind,RecordId)
);
EXEC sys.sp_executesql N'CREATE FUNCTION msg.NormalizeMobile(@Value nvarchar(100))
RETURNS varchar(16)
AS
BEGIN
    IF @Value IS NULL RETURN NULL;
    DECLARE @Digits nvarchar(100) = LTRIM(RTRIM(@Value));
    SET @Digits=REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
        @Digits,N'' '',N''''),N''-'',N''''),N''('',N''''),N'')'',N''''),CHAR(9),N''''),CHAR(13),N'''');
    SET @Digits=REPLACE(@Digits,CHAR(10),N'''');
    IF @Digits LIKE N''%[^0-9+]%'' RETURN NULL;
    IF LEFT(@Digits,4)=N''0092'' SET @Digits=SUBSTRING(@Digits,5,100);
    ELSE IF LEFT(@Digits,3)=N''+92'' SET @Digits=SUBSTRING(@Digits,4,100);
    ELSE IF LEFT(@Digits,2)=N''92'' SET @Digits=SUBSTRING(@Digits,3,100);
    ELSE IF LEFT(@Digits,1)=N''0'' SET @Digits=SUBSTRING(@Digits,2,100);
    ELSE RETURN NULL;
    IF LEN(@Digits)<>10 OR LEFT(@Digits,1)<>N''3''
       OR SUBSTRING(@Digits,2,1) NOT BETWEEN N''0'' AND N''6''
       OR @Digits LIKE N''%[^0-9]%''
        RETURN NULL;
    RETURN CONVERT(varchar(16),N''+92''+@Digits);
END;';

EXEC sys.sp_executesql N'CREATE PROCEDURE msg.ConfirmBatch
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
    IF @Audience=N''OutstandingFees''
        EXEC dbo.RequirePermission @UserId,N''Fees.View'';
    IF @ClientRequestId IS NULL OR @Channel NOT IN (''Sms'',''WhatsApp'')
       OR @Purpose NOT IN (''Announcement'',''FeeReminder'',''Individual'',''Staff'')
       OR @Audience NOT IN (''School'',''Campus'',''Class'',''OutstandingFees'',''Student'',''Family'',''Staff'')
       OR LEN(LTRIM(RTRIM(ISNULL(@Body,N''''))))=0 OR LEN(@Body)>1600
       OR @Body LIKE N''%{{%'' OR @Body LIKE N''%}}%''
       OR (@Audience=''Campus'' AND @CampusId<=0)
       OR (@Audience=''Class'' AND (@CampusId<=0 OR @ClassId<=0))
       OR (@Audience IN (''Student'',''Family'') AND @StudentId<=0)
       OR (@Audience=''Staff'' AND @StaffId<=0)
       OR (@Audience=''OutstandingFees'' AND @Purpose<>''FeeReminder'')
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
          AND Channel=@Channel AND Purpose=@Purpose AND Body=@Body)
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
        PhoneE164 varchar(16) NOT NULL, RecordName nvarchar(250) NULL
    );
    IF @Audience=''Staff''
    BEGIN
        INSERT @Live(RecipientKind,RecordId,PhoneE164,RecordName)
        SELECT ''Staff'',S.StaffId,msg.NormalizeMobile(S.ContactNumber),S.StaffName
        FROM dbo.Staff S WITH(HOLDLOCK)
        WHERE S.StaffId=@StaffId AND S.Status=N''Active'';
    END
    ELSE
    BEGIN
        INSERT @Live(RecipientKind,RecordId,PhoneE164,RecordName)
        SELECT ''Student'',S.AdmissionId,msg.NormalizeMobile(S.ContactNumber),S.StudentName
        FROM dbo.Students S WITH(HOLDLOCK)
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
        WHERE S.Status=N''Active'' AND S.SchoolMessageConsent=1
          AND (
            @Audience=''School''
            OR (@Audience=''Campus'' AND E.Status=N''Active'' AND CCS.CampusId=@CampusId)
            OR (@Audience=''Class'' AND E.Status=N''Active'' AND CCS.CampusId=@CampusId AND CCS.ClassId=@ClassId)
            OR (@Audience=''OutstandingFees'' AND E.Status=N''Active''
                AND (@CampusId=0 OR CCS.CampusId=@CampusId)
                AND (@ClassId=0 OR CCS.ClassId=@ClassId) AND B.Outstanding>0)
            OR (@Audience=''Student'' AND S.AdmissionId=@StudentId)
            OR (@Audience=''Family'' AND
                (S.AdmissionId=@StudentId OR EXISTS (
                    SELECT 1 FROM dbo.FamilyMembers FM
                    WHERE FM.AdmissionId=S.AdmissionId AND FM.FamilyId IN
                        (SELECT FM2.FamilyId FROM dbo.FamilyMembers FM2 WHERE FM2.AdmissionId=@StudentId))))
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
    INSERT msg.Batch(BatchId,ClientRequestId,Channel,Purpose,Body,State,
        CreatedBy,ConfirmedBy,ConfirmedUtc)
    VALUES(@BatchId,@ClientRequestId,@Channel,@Purpose,@Body,''Confirmed'',
        @UserId,@UserId,SYSUTCDATETIME());
    INSERT msg.Recipient(BatchId,RecipientKind,AdmissionId,StaffId,PhoneE164)
    SELECT @BatchId,RecipientKind,
        CASE WHEN RecipientKind=''Student'' THEN RecordId END,
        CASE WHEN RecipientKind=''Staff'' THEN RecordId END,PhoneE164
    FROM @Eligible;

    SELECT @BatchId BatchId,COUNT_BIG(*) RecipientCount,CONVERT(bit,0) AlreadyConfirmed
    FROM msg.Recipient WHERE BatchId=@BatchId;
    COMMIT TRANSACTION;
END;';

EXEC sys.sp_executesql N'CREATE PROCEDURE msg.ReadHistory @UserId int,@Limit int=100
AS
BEGIN
  SET NOCOUNT ON;
  EXEC dbo.RequirePermission @UserId,N''Communication.View'';
  IF @Limit<1 OR @Limit>200 THROW 51087,''History page size must be between 1 and 200.'',1;
  SELECT TOP (@Limit) B.BatchId,B.Channel,B.Purpose,B.State,
    B.CreatedUtc,B.ConfirmedUtc,B.CreatedBy,
    COUNT(R.RecipientId) Destinations,
    SUM(CASE WHEN R.State=''Delivered'' THEN 1 ELSE 0 END) Delivered,
    SUM(CASE WHEN R.State=''Failed'' THEN 1 ELSE 0 END) Failed,
    SUM(CASE WHEN R.State=''Unknown'' THEN 1 ELSE 0 END) Unknown,
    SUM(CASE WHEN R.State IN (''Pending'',''Submitted'',''Accepted'') THEN 1 ELSE 0 END) InProgress
  FROM msg.Batch B
  LEFT JOIN msg.Recipient R ON R.BatchId=B.BatchId
  GROUP BY B.BatchId,B.Channel,B.Purpose,B.State,
           B.CreatedUtc,B.ConfirmedUtc,B.CreatedBy
  ORDER BY B.CreatedUtc DESC;
END;';

EXEC sys.sp_executesql N'CREATE PROCEDURE msg.ReadChannelSettings @UserId int
AS
BEGIN
  SET NOCOUNT ON;
  EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
  SELECT Channel,ProviderCode,Enabled,UpdatedUtc,UpdatedBy
  FROM msg.ChannelSettings ORDER BY Channel;
END;';

EXEC sys.sp_executesql N'CREATE PROCEDURE msg.PauseChannel @UserId int,@Channel varchar(10)
AS
BEGIN
  SET NOCOUNT ON; SET XACT_ABORT ON;
  EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
  IF @Channel NOT IN (''Sms'',''WhatsApp'')
     THROW 51088,''Unknown channel.'',1;
  UPDATE msg.ChannelSettings
    SET Enabled=0,UpdatedBy=@UserId,UpdatedUtc=SYSUTCDATETIME()
  WHERE Channel=@Channel;
  IF @@ROWCOUNT<>1 THROW 51088,''Channel setting was not found.'',1;
END;';

EXEC sys.sp_executesql N'CREATE PROCEDURE msg.ReadTemplates @UserId int
AS
BEGIN
  SET NOCOUNT ON;
  EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
  SELECT TemplateId,Name,Channel,Purpose,Body,Active,UpdatedBy,UpdatedUtc
  FROM msg.Template ORDER BY Name,Channel;
END;';

EXEC sys.sp_executesql N'CREATE PROCEDURE msg.SaveTemplate
 @UserId int,@TemplateId int=0,@Name nvarchar(100),
 @Channel varchar(10),@Purpose varchar(20),@Body nvarchar(1600),@Active bit=1
AS
BEGIN
  SET NOCOUNT ON;SET XACT_ABORT ON;
  EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
  IF @TemplateId<0 OR LEN(LTRIM(RTRIM(ISNULL(@Name,N''''))))=0
     OR LEN(@Name)>100 OR @Channel NOT IN (''Sms'',''WhatsApp'')
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
END;';

-- New permissions are unassigned by default. Providers remain disabled.

-- Application permissions and transactional send procedures must be reviewed in
-- the same replacement PR before the Communication Center can be exposed.
INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
VALUES(N'9',SYSDATETIME(),SUSER_SNAME(),
       N'Create disabled messaging foundation and authoritative student admission consent');
