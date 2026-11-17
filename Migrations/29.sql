-- Preserve independent dispatch evidence and hold restored pending messages.
SET NOCOUNT ON; SET XACT_ABORT ON;
IF EXISTS(SELECT 1 FROM dbo.DatabaseVersion WHERE TRY_CONVERT(int,VersionNumber)=29) RETURN;
IF ISNULL((SELECT TOP(1) TRY_CONVERT(int,VersionNumber) FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0)<>28
 THROW 51000,'Communication dispatch evidence requires the preceding migration.',1;
BEGIN TRY
 BEGIN TRANSACTION;

 -- Compare restore event identity, not server-local dates. This works on the
 -- supported SQL engines without timezone-only builtins or DST assumptions.
 ALTER TABLE msg.Batch ADD RestoreBoundaryHash binary(32) NULL;
 EXEC(N'CREATE OR ALTER FUNCTION msg.RestoreBoundary()
 RETURNS binary(32)
 AS BEGIN
  DECLARE @Boundary binary(32);
  SELECT TOP(1) @Boundary=CONVERT(binary(32),HASHBYTES(''SHA2_256'',CONCAT(
    restore_history_id,NCHAR(31),CONVERT(varchar(33),restore_date,126),NCHAR(31),
    backup_set_id,NCHAR(31),restore_type,NCHAR(31),destination_database_name)))
  FROM msdb.dbo.restorehistory WHERE destination_database_name=DB_NAME()
  ORDER BY restore_history_id DESC;
  RETURN @Boundary;
 END;');

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
        CreatedBy,ConfirmedBy,ConfirmedUtc,RestoreBoundaryHash)
    VALUES(@BatchId,@ClientRequestId,@Channel,@Purpose,@Body,
        @Audience,@CampusId,@ClassId,@StudentId,@StaffId,CASE WHEN @Audience=''OutstandingFees'' AND @StudentId>0 THEN 1 ELSE 0 END,''Confirmed'',
        @UserId,@UserId,SYSUTCDATETIME(),msg.RestoreBoundary());
    INSERT msg.Recipient(BatchId,RecipientKind,AdmissionId,StaffId,PhoneE164)
    SELECT @BatchId,RecipientKind,
        CASE WHEN RecipientKind=''Student'' THEN RecordId END,
        CASE WHEN RecipientKind=''Staff'' THEN RecordId END,PhoneE164
    FROM @Eligible;

    SELECT @BatchId BatchId,COUNT_BIG(*) RecipientCount,CONVERT(bit,0) AlreadyConfirmed
    FROM msg.Recipient WHERE BatchId=@BatchId;
    COMMIT TRANSACTION;
END;');


 EXEC(N'CREATE OR ALTER TRIGGER msg.TR_Batch_RestoreBoundaryImmutable ON msg.Batch AFTER UPDATE
 AS BEGIN
  SET NOCOUNT ON;
  IF EXISTS(SELECT BatchId,RestoreBoundaryHash FROM inserted
    EXCEPT SELECT BatchId,RestoreBoundaryHash FROM deleted)
   THROW 51343,''A reviewed message cannot be reassigned to another database recovery boundary. Create a new review.'',1;
 END;');
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
  DECLARE @Boundary binary(32);
  BEGIN TRY
   SET @Boundary=msg.RestoreBoundary();
  END TRY
  BEGIN CATCH
   THROW 51342,''Database recovery history cannot be verified. Sending is held; ask the administrator to verify the SQL installation.'',1;
  END CATCH;
  IF EXISTS(SELECT 1 FROM inserted I JOIN msg.Recipient R ON R.RecipientId=I.RecipientId
    JOIN msg.Batch B ON B.BatchId=R.BatchId WHERE B.RestoreBoundaryHash<>@Boundary
      OR (B.RestoreBoundaryHash IS NULL AND @Boundary IS NOT NULL)
      OR (B.RestoreBoundaryHash IS NOT NULL AND @Boundary IS NULL))
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
