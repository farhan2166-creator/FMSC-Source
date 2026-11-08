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
        OR (@Channel=''WhatsApp'' AND @ProviderCode IN (''Twilio'',''MetaCloud''))
        OR (@Channel=''Email'' AND @ProviderCode=''Smtp''))
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

-- Existing school-message consent remains the sole authority.
-- No email recipient is created or dispatched by this migration.
INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
VALUES(N'10',SYSDATETIME(),SUSER_SNAME(),
       N'Prepare disabled multi-provider channels and optional school-record email');
