-- One-school API master pause and action-focused queue projection.
-- All message dispatch paths must persist an Attempt before contacting a provider.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF EXISTS(SELECT 1 FROM dbo.DatabaseVersion WHERE TRY_CONVERT(int,VersionNumber)=23) RETURN;
IF ISNULL((SELECT TOP(1) TRY_CONVERT(int,VersionNumber)
    FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0)<>22
    THROW 51000,'Communication queue controls require the preceding migration.',1;
IF OBJECT_ID(N'msg.Attempt',N'U') IS NULL OR OBJECT_ID(N'msg.ApiBatch',N'U') IS NULL
    THROW 51260,'Communication API dispatch tables are missing.',1;

CREATE TABLE msg.SendControl(
    Id tinyint NOT NULL CONSTRAINT PK_MsgSendControl PRIMARY KEY,
    Paused bit NOT NULL CONSTRAINT DF_MsgSendControlPaused DEFAULT(0),
    UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_MsgSendControlUpdatedUtc DEFAULT(SYSUTCDATETIME()),
    ChangedBy int NULL,
    CONSTRAINT CK_MsgSendControlId CHECK (Id = 1)
);
INSERT msg.SendControl(Id,Paused) VALUES(1,0);
CREATE TABLE msg.HistoryExportAudit(
    ExportId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_MsgHistoryExportAudit PRIMARY KEY,
    RequestedBy int NOT NULL,
    ExportedRows int NOT NULL CONSTRAINT CK_MsgHistoryExportRowCount CHECK(ExportedRows>0),
    FileType varchar(3) NOT NULL CONSTRAINT CK_MsgHistoryExportFileType CHECK(FileType IN ('CSV','PDF')),
    RequestedUtc datetime2(3) NOT NULL CONSTRAINT DF_MsgHistoryExportDate DEFAULT(SYSUTCDATETIME())
);
CREATE TABLE msg.SendControlHistory(
    ChangeId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_MsgSendControlHistory PRIMARY KEY,
    Paused bit NOT NULL,
    ChangedBy int NOT NULL,
    ChangedUtc datetime2(3) NOT NULL CONSTRAINT DF_MsgSendControlHistoryDate DEFAULT(SYSUTCDATETIME())
);

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadQueue @UserId int
AS
BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 SELECT B.BatchId,B.Channel,B.Audience,B.CreatedUtc,COUNT_BIG(*) InProgress,
   SUM(CONVERT(bigint,CASE WHEN A.BatchId IS NOT NULL AND R.State IN (''Unknown'',''Failed'') THEN 1 ELSE 0 END)) NeedsAttention,
   SUM(CONVERT(bigint,CASE WHEN (M.BatchId IS NOT NULL AND H.State IN (''Pending'',''Opened''))
       OR (A.BatchId IS NOT NULL AND R.State=''Pending'') THEN 1 ELSE 0 END)) Upcoming,
   CONVERT(varchar(40),CASE WHEN M.BatchId IS NOT NULL THEN ''WhatsApp Business (manual)'' ELSE A.ProviderCode END) Method
 FROM msg.Batch B JOIN msg.Recipient R ON R.BatchId=B.BatchId
 LEFT JOIN msg.ManualBatch M ON M.BatchId=B.BatchId
 LEFT JOIN msg.ManualHandoff H ON H.RecipientId=R.RecipientId
 LEFT JOIN msg.ApiBatch A ON A.BatchId=B.BatchId
 WHERE B.CreatedBy=@UserId AND
  ((M.BatchId IS NOT NULL AND H.State IN (''Pending'',''Opened''))
   OR (A.BatchId IS NOT NULL AND R.State IN (''Pending'',''Unknown'',''Accepted'',''Submitted'',''Failed'')))
 GROUP BY B.BatchId,B.Channel,B.Audience,B.CreatedUtc,M.BatchId,A.ProviderCode
 ORDER BY B.CreatedUtc DESC,B.BatchId;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadSendControl @UserId int
AS
BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 SELECT Paused,UpdatedUtc,ChangedBy FROM msg.SendControl WHERE Id=1;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.SetSendPaused @UserId int,@Paused bit
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 IF @Paused IS NULL THROW 51260,''A pause or resume choice is required.'',1;
 BEGIN TRANSACTION;
 IF NOT EXISTS(SELECT 1 FROM msg.SendControl WITH(UPDLOCK,HOLDLOCK) WHERE Id=1)
    THROW 51260,''Sending control is unavailable.'',1;
 IF EXISTS(SELECT 1 FROM msg.SendControl WHERE Id=1 AND Paused<>@Paused)
 BEGIN
   UPDATE msg.SendControl SET Paused=@Paused,UpdatedUtc=SYSUTCDATETIME(),ChangedBy=@UserId WHERE Id=1;
   INSERT msg.SendControlHistory(Paused,ChangedBy) VALUES(@Paused,@UserId);
 END;
 COMMIT TRANSACTION;
END');

EXEC(N'CREATE OR ALTER TRIGGER msg.TR_Attempt_GlobalPause ON msg.Attempt AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 -- HOLDLOCK preserves ordering with an administrator''s change of pause state.
 -- A previously claimed HTTP attempt cannot be recalled, but no later claim commits.
 IF NOT EXISTS(SELECT 1 FROM msg.SendControl WITH(HOLDLOCK) WHERE Id=1 AND Paused=0)
    THROW 51260,''Communication API sending is paused. An administrator must resume it in Queue.'',1;
END');


EXEC(N'CREATE OR ALTER PROCEDURE msg.RecordHistoryExport @UserId int,@RowCount int,@FileType varchar(3)
AS
BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 IF @RowCount IS NULL OR @RowCount<1 OR @FileType NOT IN (''PDF'',''CSV'')
     THROW 51265,''Only nonempty PDF or CSV history exports are allowed.'',1;
 INSERT msg.HistoryExportAudit(RequestedBy,ExportedRows,FileType)
 VALUES(@UserId,@RowCount,@FileType);
END');

-- School language choices are optional and never translate messages automatically.
ALTER TABLE msg.Template ADD BodyUrdu nvarchar(1600) NULL;
CREATE TABLE msg.LanguageSettings(
 Id tinyint NOT NULL CONSTRAINT PK_MsgLanguageSettings PRIMARY KEY,
 DefaultLanguage varchar(2) NOT NULL CONSTRAINT DF_MsgLanguageSettingsDefault DEFAULT('en'),
 CONSTRAINT CK_MsgLanguageSettingsId CHECK(Id=1),
 CONSTRAINT CK_MsgLanguageSettingsChoice CHECK(DefaultLanguage IN ('en','ur'))
);
INSERT msg.LanguageSettings(Id,DefaultLanguage) VALUES(1,'en');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadComposeTemplates @UserId int
AS
BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 SELECT TemplateId,Name,Channel,Purpose,Body,CONVERT(varchar(2),''en'') Language
 FROM msg.Template WHERE Active=1
 UNION ALL
 SELECT TemplateId,Name,Channel,Purpose,BodyUrdu,CONVERT(varchar(2),''ur'') Language
 FROM msg.Template WHERE Active=1 AND NULLIF(LTRIM(RTRIM(BodyUrdu)),N'''') IS NOT NULL
 ORDER BY Name,Channel,Language;
END');


EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadTemplates @UserId int
AS
BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 SELECT TemplateId,Name,Channel,Purpose,Body,BodyUrdu,Active,UpdatedBy,UpdatedUtc
 FROM msg.Template ORDER BY Name,Channel;
END');


EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadDefaultTemplateLanguage @UserId int
AS
BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 SELECT DefaultLanguage FROM msg.LanguageSettings WHERE Id=1;
END');


EXEC(N'CREATE OR ALTER PROCEDURE msg.SetDefaultTemplateLanguage @UserId int,@Language varchar(2)
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 IF @Language NOT IN (''en'',''ur'') OR @Language IS NULL
  THROW 51270,''Choose English or Urdu.'',1;
 UPDATE msg.LanguageSettings SET DefaultLanguage=@Language WHERE Id=1;
END');


EXEC(N'CREATE OR ALTER PROCEDURE msg.SaveTemplateUrdu @UserId int,@TemplateId int,@BodyUrdu nvarchar(1600)=NULL
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 IF @TemplateId<=0 OR (@BodyUrdu IS NOT NULL AND
  (LEN(LTRIM(RTRIM(@BodyUrdu)))=0 OR LEN(@BodyUrdu)>1600
   OR @BodyUrdu LIKE N''%{{%'' OR @BodyUrdu LIKE N''%}}%''))
  THROW 51271,''The Urdu message is invalid.'',1;
 UPDATE msg.Template SET BodyUrdu=@BodyUrdu,UpdatedBy=@UserId,UpdatedUtc=SYSUTCDATETIME()
 WHERE TemplateId=@TemplateId;
 IF @@ROWCOUNT<>1 THROW 51271,''The selected template no longer exists.'',1;
END');


-- Sending policy is inert by default. No unattended sender or event automation
-- becomes active because of a schema upgrade.
CREATE TABLE msg.DispatchSettings(
 Id tinyint NOT NULL CONSTRAINT PK_MsgDispatchSettings PRIMARY KEY,
 UnattendedEnabled bit NOT NULL CONSTRAINT DF_MsgDispatchEnabled DEFAULT(0),
 SendFrom time(0) NOT NULL CONSTRAINT DF_MsgDispatchFrom DEFAULT('08:00'),
 SendUntil time(0) NOT NULL CONSTRAINT DF_MsgDispatchUntil DEFAULT('18:00'),
 WorkingDaysMask tinyint NOT NULL CONSTRAINT DF_MsgDispatchDays DEFAULT(127),
 MaximumPerMinute int NOT NULL CONSTRAINT DF_MsgDispatchRate DEFAULT(60),
 MaximumSmsDaily int NOT NULL CONSTRAINT DF_MsgDispatchSmsDaily DEFAULT(0),
 MaximumWhatsAppDaily int NOT NULL CONSTRAINT DF_MsgDispatchWhatsAppDaily DEFAULT(0),
 MaximumRoutineFamilyDaily tinyint NOT NULL CONSTRAINT DF_MsgDispatchFamilyDaily DEFAULT(3),
 PriorityReservePercent tinyint NOT NULL CONSTRAINT DF_MsgDispatchReserve DEFAULT(10),
 UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_MsgDispatchUpdated DEFAULT(SYSUTCDATETIME()),
 UpdatedBy int NULL,
 CONSTRAINT CK_MsgDispatchSingleton CHECK(Id=1),
 CONSTRAINT CK_MsgDispatchHours CHECK(SendFrom<SendUntil),
 CONSTRAINT CK_MsgDispatchDays CHECK(WorkingDaysMask BETWEEN 1 AND 127),
 CONSTRAINT CK_MsgDispatchRate CHECK(MaximumPerMinute BETWEEN 1 AND 600),
 CONSTRAINT CK_MsgDispatchSmsDaily CHECK(MaximumSmsDaily BETWEEN 0 AND 100000),
 CONSTRAINT CK_MsgDispatchWhatsAppDaily CHECK(MaximumWhatsAppDaily BETWEEN 0 AND 100000),
 CONSTRAINT CK_MsgDispatchFamilyDaily CHECK(MaximumRoutineFamilyDaily BETWEEN 1 AND 20),
 CONSTRAINT CK_MsgDispatchReserve CHECK(PriorityReservePercent BETWEEN 0 AND 50)
);
INSERT msg.DispatchSettings(Id) VALUES(1);
CREATE TABLE msg.AutomationRule(
 EventCode varchar(30) NOT NULL CONSTRAINT PK_MsgAutomationRule PRIMARY KEY,
 Mode varchar(10) NOT NULL CONSTRAINT DF_MsgAutomationMode DEFAULT('Off'),
 CampusId int NOT NULL CONSTRAINT DF_MsgAutomationCampus DEFAULT(0),
 ClassId int NOT NULL CONSTRAINT DF_MsgAutomationClass DEFAULT(0),
 Channel varchar(10) NULL,
 ActivatedUtc datetime2(3) NULL,
 UpdatedBy int NULL,
 UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_MsgAutomationChanged DEFAULT(SYSUTCDATETIME()),
 CONSTRAINT CK_MsgAutomationMode CHECK(Mode IN ('Off','Review','Automatic')),
 CONSTRAINT CK_MsgAutomationChannel CHECK(Channel IS NULL OR Channel IN ('Sms','WhatsApp')),
 CONSTRAINT CK_MsgAutomationScope CHECK(CampusId>=0 AND ClassId>=0 AND (ClassId=0 OR CampusId>0))
);
INSERT msg.AutomationRule(EventCode) VALUES
 ('StudentAdmitted'),('PaymentReceived'),('OutstandingFee'),('UpcomingDue'),
 ('OverdueFee'),('SchoolAnnouncement'),('ExaminationsResults'),('BirthdayGreeting');

CREATE TABLE msg.BatchSchedule(
 BatchId uniqueidentifier NOT NULL CONSTRAINT PK_MsgBatchSchedule PRIMARY KEY,
 SendAtUtc datetime2(3) NOT NULL,
 ExpiresUtc datetime2(3) NOT NULL,
 State varchar(10) NOT NULL CONSTRAINT DF_MsgBatchScheduleState DEFAULT('Ready'),
 CreatedBy int NOT NULL,
 UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_MsgBatchScheduleModified DEFAULT(SYSUTCDATETIME()),
 CONSTRAINT CK_MsgBatchScheduleRange CHECK(SendAtUtc<ExpiresUtc),
 CONSTRAINT CK_MsgBatchScheduleState CHECK(State IN ('Ready','Paused','Cancelled')),
 CONSTRAINT FK_MsgBatchScheduleBatch FOREIGN KEY(BatchId) REFERENCES msg.Batch(BatchId)
);
CREATE INDEX IX_MsgBatchScheduleUpcoming ON msg.BatchSchedule(State,SendAtUtc) INCLUDE(ExpiresUtc);

CREATE TABLE msg.FamilyAutomationException(
 AdmissionId int NOT NULL,
 EventCode varchar(30) NOT NULL CONSTRAINT FK_MsgFamilyExceptionEvent REFERENCES msg.AutomationRule(EventCode),
 Reason nvarchar(200) NOT NULL,
 ExpiresUtc datetime2(3) NULL,
 RecordedBy int NOT NULL,
 RecordedUtc datetime2(3) NOT NULL CONSTRAINT DF_MsgFamilyExceptionCreated DEFAULT(SYSUTCDATETIME()),
 CONSTRAINT PK_MsgFamilyAutomationException PRIMARY KEY(AdmissionId,EventCode),
 CONSTRAINT CK_MsgFamilyExceptionReason CHECK(LEN(LTRIM(RTRIM(Reason)))>0)
);

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadDispatchSettings @UserId int
AS
BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 SELECT UnattendedEnabled,SendFrom,SendUntil,WorkingDaysMask,MaximumPerMinute,
        MaximumSmsDaily,MaximumWhatsAppDaily,MaximumRoutineFamilyDaily,
        PriorityReservePercent FROM msg.DispatchSettings WHERE Id=1;
END');


EXEC(N'CREATE OR ALTER PROCEDURE msg.SaveDispatchSettings
 @UserId int,@SendFrom time(0),@SendUntil time(0),@WorkingDaysMask tinyint,
 @MaximumPerMinute int,@MaximumSmsDaily int,@MaximumWhatsAppDaily int,
 @MaximumRoutineFamilyDaily tinyint,@PriorityReservePercent tinyint
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 IF @SendFrom IS NULL OR @SendUntil IS NULL OR @SendFrom>=@SendUntil
    OR @WorkingDaysMask NOT BETWEEN 1 AND 127
    OR @MaximumPerMinute NOT BETWEEN 1 AND 600
    OR @MaximumSmsDaily NOT BETWEEN 0 AND 100000
    OR @MaximumWhatsAppDaily NOT BETWEEN 0 AND 100000
    OR @MaximumRoutineFamilyDaily NOT BETWEEN 1 AND 20
    OR @PriorityReservePercent NOT BETWEEN 0 AND 50
    OR @WorkingDaysMask IS NULL OR @MaximumPerMinute IS NULL
    OR @MaximumSmsDaily IS NULL OR @MaximumWhatsAppDaily IS NULL
    OR @MaximumRoutineFamilyDaily IS NULL OR @PriorityReservePercent IS NULL
    THROW 51275,''Invalid communication schedule or daily capacity.'',1;
 UPDATE msg.DispatchSettings
 SET SendFrom=@SendFrom,SendUntil=@SendUntil,WorkingDaysMask=@WorkingDaysMask,
 MaximumPerMinute=@MaximumPerMinute,MaximumSmsDaily=@MaximumSmsDaily,
 MaximumWhatsAppDaily=@MaximumWhatsAppDaily,
 MaximumRoutineFamilyDaily=@MaximumRoutineFamilyDaily,PriorityReservePercent=@PriorityReservePercent,
 UpdatedBy=@UserId,UpdatedUtc=SYSUTCDATETIME()
 WHERE Id=1;
END');


EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadAutomationRules @UserId int
AS
BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 SELECT EventCode,Mode,CampusId,ClassId,Channel,ActivatedUtc FROM msg.AutomationRule ORDER BY
 CASE EventCode WHEN ''StudentAdmitted'' THEN 1 WHEN ''PaymentReceived'' THEN 2
 WHEN ''OutstandingFee'' THEN 3 WHEN ''UpcomingDue'' THEN 4 WHEN ''OverdueFee'' THEN 5
 WHEN ''SchoolAnnouncement'' THEN 6 WHEN ''ExaminationsResults'' THEN 7 ELSE 8 END;
END');


EXEC(N'CREATE OR ALTER PROCEDURE msg.SaveAutomationRule
 @UserId int,@EventCode varchar(30),@Mode varchar(10),@CampusId int=0,@ClassId int=0,@Channel varchar(10)=NULL
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 IF @Mode NOT IN (''Off'',''Review'',''Automatic'') OR @Mode IS NULL
 OR @CampusId<0 OR @ClassId<0 OR (@ClassId>0 AND @CampusId=0)
 OR @Channel NOT IN (''Sms'',''WhatsApp'') AND @Channel IS NOT NULL
 OR NOT EXISTS(SELECT 1 FROM msg.AutomationRule WHERE EventCode=@EventCode)
   THROW 51276,''Invalid school automation rule.'',1;
 -- Until an audited unattended processor has been authorized, no sending
 -- rule may silently transition out of Off.
 IF @Mode<>''Off'' AND NOT EXISTS(
 SELECT 1 FROM msg.DispatchSettings WHERE Id=1 AND UnattendedEnabled=1)
   THROW 51276,''Configure and authorize background sending before enabling automation.'',1;
 UPDATE msg.AutomationRule
 SET Mode=@Mode,CampusId=@CampusId,ClassId=@ClassId,Channel=@Channel,
 ActivatedUtc=CASE WHEN Mode=''Off'' AND @Mode<>''Off'' THEN SYSUTCDATETIME()
     WHEN @Mode=''Off'' THEN NULL ELSE ActivatedUtc END,
 UpdatedBy=@UserId,UpdatedUtc=SYSUTCDATETIME()
 WHERE EventCode=@EventCode;
END');


EXEC(N'CREATE OR ALTER PROCEDURE msg.SaveBatchSchedule
 @UserId int,@BatchId uniqueidentifier,@SendAtUtc datetime2(3),@ExpiresUtc datetime2(3)
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 IF @BatchId IS NULL OR @SendAtUtc IS NULL OR @ExpiresUtc IS NULL
    OR @SendAtUtc>=@ExpiresUtc OR @ExpiresUtc<=SYSUTCDATETIME()
    THROW 51277,''A valid future schedule and expiry are required.'',1;
 BEGIN TRANSACTION;
 IF NOT EXISTS(SELECT 1 FROM msg.Batch B WITH(UPDLOCK,HOLDLOCK)
     WHERE B.BatchId=@BatchId AND B.CreatedBy=@UserId AND B.ConfirmedUtc IS NOT NULL
       AND B.State NOT IN (''Cancelled'',''Completed''))
 OR EXISTS(SELECT 1 FROM msg.Attempt A JOIN msg.Recipient R ON R.RecipientId=A.RecipientId
    WHERE R.BatchId=@BatchId)
 OR EXISTS(SELECT 1 FROM msg.ManualHandoff H JOIN msg.Recipient R ON R.RecipientId=H.RecipientId
    WHERE R.BatchId=@BatchId AND H.State NOT IN (''Pending''))
     THROW 51277,''Only untouched confirmed batches can be scheduled.'',1;
 IF EXISTS(SELECT 1 FROM msg.BatchSchedule WITH(UPDLOCK,HOLDLOCK)
    WHERE BatchId=@BatchId AND State<>''Ready'')
     THROW 51277,''Paused or cancelled schedules require administrator review.'',1;
 IF EXISTS(SELECT 1 FROM msg.BatchSchedule WHERE BatchId=@BatchId)
 UPDATE msg.BatchSchedule SET SendAtUtc=@SendAtUtc,ExpiresUtc=@ExpiresUtc,UpdatedUtc=SYSUTCDATETIME()
 WHERE BatchId=@BatchId;
 ELSE
 INSERT msg.BatchSchedule(BatchId,SendAtUtc,ExpiresUtc,CreatedBy)
 VALUES(@BatchId,@SendAtUtc,@ExpiresUtc,@UserId);
 COMMIT TRANSACTION;
END');


EXEC(N'CREATE OR ALTER TRIGGER msg.TR_Attempt_ScheduleGuard ON msg.Attempt AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(
   SELECT 1 FROM inserted I
   JOIN msg.Recipient R ON R.RecipientId=I.RecipientId
   JOIN msg.BatchSchedule S WITH(HOLDLOCK) ON S.BatchId=R.BatchId
   WHERE S.State<>''Ready'' OR S.SendAtUtc>SYSUTCDATETIME() OR S.ExpiresUtc<=SYSUTCDATETIME())
   THROW 51277,''The reviewed batch schedule is paused, not yet due, or expired.'',1;
END');


EXEC(N'CREATE OR ALTER TRIGGER msg.TR_ManualHandoff_ScheduleGuard ON msg.ManualHandoff AFTER UPDATE AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(
   SELECT 1 FROM inserted I
   JOIN deleted D ON D.RecipientId=I.RecipientId
   JOIN msg.Recipient R ON R.RecipientId=I.RecipientId
   JOIN msg.BatchSchedule S WITH(HOLDLOCK) ON S.BatchId=R.BatchId
   WHERE I.State=''Opened'' AND D.State<>''Opened''
     AND (S.State<>''Ready'' OR S.SendAtUtc>SYSUTCDATETIME() OR S.ExpiresUtc<=SYSUTCDATETIME()))
   THROW 51277,''This manual WhatsApp batch is not ready to open.'',1;
END');


INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
VALUES(N'23',SYSDATETIME(),SUSER_SNAME(),N'Administrator API master pause and action-focused queue projection');
