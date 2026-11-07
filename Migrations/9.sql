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
ALTER TABLE dbo.Students ADD CONSTRAINT CK_Students_SchoolMessageConsentEvidence
    CHECK ((SchoolMessageConsent IS NULL AND SchoolMessageConsentUtc IS NULL AND SchoolMessageConsentBy IS NULL)
        OR (SchoolMessageConsent IS NOT NULL AND SchoolMessageConsentUtc IS NOT NULL AND SchoolMessageConsentBy IS NOT NULL));

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

-- No new permission grants or provider activation in a schema-only migration.
-- Application permissions and transactional send procedures must be reviewed in
-- the same replacement PR before the Communication Center can be exposed.
INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
VALUES(N'9',SYSDATETIME(),SUSER_SNAME(),
       N'Create disabled messaging foundation and authoritative student admission consent');
