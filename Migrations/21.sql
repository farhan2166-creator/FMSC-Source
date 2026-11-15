-- Meta acceptance finishes submission work, without asserting delivery or
-- changing any recipient, attempt, consent or financial record.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF EXISTS(SELECT 1 FROM dbo.DatabaseVersion WHERE TRY_CONVERT(int,VersionNumber)=21) RETURN;
IF ISNULL((SELECT TOP(1) TRY_CONVERT(int,VersionNumber)
    FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0)<>20
    THROW 51000,'Communication queue changes require the preceding migration.',1;

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadMyUnfinishedBatches @UserId int
AS
BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 SELECT B.BatchId,B.Channel,B.Audience,B.CreatedUtc,COUNT_BIG(*) InProgress,
   CONVERT(varchar(40),CASE WHEN M.BatchId IS NOT NULL THEN ''WhatsApp Business (manual)'' ELSE A.ProviderCode END) Method
 FROM msg.Batch B JOIN msg.Recipient R ON R.BatchId=B.BatchId
 LEFT JOIN msg.ManualBatch M ON M.BatchId=B.BatchId
 LEFT JOIN msg.ManualHandoff H ON H.RecipientId=R.RecipientId
 LEFT JOIN msg.ApiBatch A ON A.BatchId=B.BatchId
 WHERE B.CreatedBy=@UserId AND
  ((M.BatchId IS NOT NULL AND H.State IN (''Pending'',''Opened''))
   OR (A.BatchId IS NOT NULL AND
    (R.State IN (''Pending'',''Unknown'',''Submitted'',''Failed'')
     -- Twilio acceptance can still be polled for delivery. Meta has no GET
     -- status endpoint; accepted recipients have no outstanding desktop work.
     OR (R.State=''Accepted'' AND NOT (A.ProviderCode=''MetaCloud'' AND B.Channel=''WhatsApp'')))))
 GROUP BY B.BatchId,B.Channel,B.Audience,B.CreatedUtc,M.BatchId,A.ProviderCode
 ORDER BY B.CreatedUtc DESC,B.BatchId;
END');

INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
VALUES(N'21',SYSDATETIME(),SUSER_SNAME(),N'Keep accepted Meta submissions out of unfinished work without claiming delivery');
