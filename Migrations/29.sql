-- Preserve independent dispatch evidence and hold restored pending messages.
SET NOCOUNT ON; SET XACT_ABORT ON;
IF EXISTS(SELECT 1 FROM dbo.DatabaseVersion WHERE TRY_CONVERT(int,VersionNumber)=29) RETURN;
IF ISNULL((SELECT TOP(1) TRY_CONVERT(int,VersionNumber) FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0)<>28
 THROW 51000,'Communication dispatch evidence requires the preceding migration.',1;
BEGIN TRY
 BEGIN TRANSACTION;
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
  SELECT AttemptId,AttemptKey,Channel,ProviderCode
  FROM msg.Attempt WHERE RecipientId=@RecipientId ORDER BY AttemptNumber;
 END;');
 EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadAttemptForReconciliation
 @UserId int,@AttemptId bigint
 AS BEGIN
  SET NOCOUNT ON;
  EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
  DECLARE @Audience varchar(20),@Owner int;
  SELECT @Audience=B.Audience,@Owner=B.CreatedBy
  FROM msg.Attempt A JOIN msg.Recipient R ON R.RecipientId=A.RecipientId
  JOIN msg.Batch B ON B.BatchId=R.BatchId WHERE A.AttemptId=@AttemptId;
  IF @Owner<>@UserId EXEC dbo.RequirePermission @UserId,N''Communication.View'';
  IF @Audience=''Staff'' EXEC dbo.RequirePermission @UserId,N''Staff.View'';
  ELSE EXEC dbo.RequirePermission @UserId,N''Students.View'';
  IF @Audience IN (''OutstandingFees'',''FeeStudent'',''FeeFamily'') EXEC dbo.RequirePermission @UserId,N''Fees.View'';
  SELECT A.AttemptId,A.AttemptKey,A.RecipientId,A.Channel,A.ProviderCode,A.ProviderMessageId,A.Outcome
  FROM msg.Attempt A WHERE A.AttemptId=@AttemptId AND A.Outcome IN (''Unknown'',''Submitted'',''Accepted'',''Failed'');
 END;');
 -- SQL Server restore history protects accounts with no original local journal.
 EXEC(N'CREATE OR ALTER TRIGGER msg.TR_Attempt_RestoreGuard ON msg.Attempt AFTER INSERT
 AS BEGIN
  SET NOCOUNT ON;
  DECLARE @RestoredLocal datetime,@RestoredUtc datetime2(3);
  BEGIN TRY
   SELECT @RestoredLocal=MAX(restore_date) FROM msdb.dbo.restorehistory
     WHERE destination_database_name=DB_NAME();
   IF @RestoredLocal IS NOT NULL
    SET @RestoredUtc=CONVERT(datetime2(3),(@RestoredLocal AT TIME ZONE CURRENT_TIMEZONE_ID()) AT TIME ZONE ''UTC'');
  END TRY
  BEGIN CATCH
   THROW 51342,''Database recovery history cannot be verified. Sending is held; ask the administrator to verify the SQL installation.'',1;
  END CATCH;
  IF @RestoredUtc IS NOT NULL AND EXISTS(
    SELECT 1 FROM inserted I JOIN msg.Recipient R ON R.RecipientId=I.RecipientId
    JOIN msg.Batch B ON B.BatchId=R.BatchId WHERE B.CreatedUtc<=@RestoredUtc)
   THROW 51341,''This pending message predates a database restore. Keep it held and reconcile prior submissions; do not resume it as unsent.'',1;
 END;');
 INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
 VALUES(N'29',SYSDATETIME(),SUSER_SNAME(),N'Permission-scoped dispatch identity for independent communication recovery evidence');
 COMMIT;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK;
 THROW;
END CATCH;
