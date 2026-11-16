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
    RowCount int NOT NULL CONSTRAINT CK_MsgHistoryExportRowCount CHECK(RowCount>0),
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
 INSERT msg.HistoryExportAudit(RequestedBy,RowCount,FileType)
 VALUES(@UserId,@RowCount,@FileType);
END');

INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
VALUES(N'23',SYSDATETIME(),SUSER_SNAME(),N'Administrator API master pause and action-focused queue projection');
