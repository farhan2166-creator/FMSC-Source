-- Administrator takeover cannot release an approval from before database recovery.
SET NOCOUNT ON;SET XACT_ABORT ON;
IF EXISTS(SELECT 1 FROM dbo.DatabaseVersion WHERE TRY_CONVERT(int,VersionNumber)=33) RETURN;
IF ISNULL((SELECT TOP(1) TRY_CONVERT(int,VersionNumber) FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0)<>32
 THROW 51000,'Communication takeover recovery requires the preceding migration.',1;
BEGIN TRY
 BEGIN TRANSACTION;
EXEC(N'CREATE OR ALTER PROCEDURE msg.TakeOverScheduledBatch
 @UserId int,@BatchId uniqueidentifier,@Reason nvarchar(200)
AS BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 EXEC dbo.RequirePermission @UserId,N''Communication.View'';
 EXEC dbo.RequirePermission @UserId,N''Students.View'';
 EXEC dbo.RequirePermission @UserId,N''Staff.View'';
 EXEC dbo.RequirePermission @UserId,N''Fees.View'';
 IF @BatchId IS NULL OR LEN(LTRIM(RTRIM(ISNULL(@Reason,N'''')))) NOT BETWEEN 5 AND 200
   THROW 51296,''Choose a held batch and record why its responsibility is changing.'',1;
 BEGIN TRANSACTION;
 DECLARE @OldOwner int,@Channel varchar(10),@ProviderCode varchar(40);
 SELECT @OldOwner=COALESCE(T.NewOwnerId,B.CreatedBy),
        @Channel=B.Channel,@ProviderCode=A.ProviderCode
 FROM msg.Batch B WITH(UPDLOCK,HOLDLOCK)
 JOIN msg.ApiBatch A ON A.BatchId=B.BatchId
 JOIN msg.BatchSchedule S WITH(UPDLOCK,HOLDLOCK)
    ON S.BatchId=B.BatchId AND S.HoldForReview=1
 OUTER APPLY (SELECT TOP(1) H.NewOwnerId FROM msg.BatchTakeover H
    WHERE H.BatchId=B.BatchId ORDER BY H.TakeoverId DESC) T
 WHERE B.BatchId=@BatchId AND B.State IN (''Confirmed'',''Sending'')
   AND EXISTS (SELECT 1 FROM msg.Recipient R WITH(HOLDLOCK)
       WHERE R.BatchId=B.BatchId AND R.State=''Pending'');
 IF @OldOwner IS NULL
   THROW 51296,''Only a held, reviewed, unfinished API batch can be taken over.'',1;
 IF msg.BatchRecoveryBlocked(@BatchId)=1
   THROW 51341,''This approval predates database recovery. Reconcile earlier submissions and review a new request before takeover.'',1;
 IF EXISTS(SELECT 1 FROM msg.DispatchControl WHERE ControlId=1 AND MasterPaused=1)
   THROW 51296,''Master Pause is active. Review recovery or administrator holds before resuming.'',1;
 IF NOT EXISTS(SELECT 1 FROM msg.ChannelSettings WITH(HOLDLOCK)
    WHERE Channel=@Channel AND ProviderCode=@ProviderCode AND Enabled=1)
   THROW 51296,''The original reviewed provider is no longer active. Re-review before sending.'',1;
 IF EXISTS(SELECT 1 FROM msg.Recipient R WITH(HOLDLOCK)
    JOIN msg.Attempt A WITH(HOLDLOCK) ON A.RecipientId=R.RecipientId
    WHERE R.BatchId=@BatchId AND R.State=''Pending''
      AND A.Outcome IN (''Unknown'',''Submitted'',''Accepted''))
   THROW 51296,''An uncertain recipient cannot be released through takeover.'',1;
 INSERT msg.BatchTakeover(BatchId,PriorOwnerId,NewOwnerId,ApprovedBy,Reason)
 VALUES(@BatchId,@OldOwner,@UserId,@UserId,LTRIM(RTRIM(@Reason)));
 -- Atomic release is safe: the original provider and reviewed scope are
 -- still checked again by ClaimRecipient before any external request.
 UPDATE msg.BatchSchedule SET HoldForReview=0,UpdatedBy=@UserId,
   UpdatedUtc=SYSUTCDATETIME() WHERE BatchId=@BatchId;
 IF @@ROWCOUNT<>1 THROW 51296,''The held batch changed during review.'',1;
 COMMIT;
END');
EXEC(N'CREATE OR ALTER PROCEDURE msg.ScheduleReviewedBatch
 @UserId int,@BatchId uniqueidentifier,@DueUtc datetime2(3),
 @ExpiresUtc datetime2(3)=NULL,@Priority varchar(10)=''Normal''
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 IF @BatchId IS NULL OR @DueUtc IS NULL OR @DueUtc<=SYSUTCDATETIME()
    OR (@ExpiresUtc IS NOT NULL AND @ExpiresUtc<=@DueUtc)
    OR @Priority NOT IN (''High'',''Normal'',''Low'')
    THROW 51245,''Invalid schedule, expiry, or priority.'',1;
 BEGIN TRANSACTION;
 DECLARE @Owner int,@Audience varchar(20);
 SELECT @Owner=B.CreatedBy,@Audience=B.Audience
 FROM msg.Batch B WITH(UPDLOCK,HOLDLOCK)
 JOIN msg.ApiBatch A ON A.BatchId=B.BatchId
 WHERE B.BatchId=@BatchId AND B.State=''Confirmed'';
 IF @Owner IS NULL THROW 51245,''Only reviewed, confirmed API batches can be scheduled.'',1;
 IF @Owner<>@UserId EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 IF @Audience=''Staff'' EXEC dbo.RequirePermission @UserId,N''Staff.View'';
 ELSE EXEC dbo.RequirePermission @UserId,N''Students.View'';
 IF @Audience IN (''OutstandingFees'',''FeeStudent'',''FeeFamily'')
    EXEC dbo.RequirePermission @UserId,N''Fees.View'';
 IF msg.BatchRecoveryBlocked(@BatchId)=1
    THROW 51341,''This approval predates database recovery. Review a new request instead of rescheduling it.'',1;
 IF EXISTS(SELECT 1 FROM msg.Attempt T JOIN msg.Recipient R
           ON R.RecipientId=T.RecipientId WHERE R.BatchId=@BatchId)
     THROW 51245,''A batch with previous attempts cannot be newly scheduled.'',1;
 IF EXISTS(SELECT 1 FROM msg.BatchSchedule WITH(UPDLOCK,HOLDLOCK)
           WHERE BatchId=@BatchId)
     UPDATE msg.BatchSchedule SET DueUtc=@DueUtc,ExpiresUtc=@ExpiresUtc,
        Priority=@Priority,HoldForReview=0,UpdatedUtc=SYSUTCDATETIME(),
        UpdatedBy=@UserId WHERE BatchId=@BatchId;
 ELSE
     INSERT msg.BatchSchedule(BatchId,DueUtc,ExpiresUtc,Priority,UpdatedBy)
     VALUES(@BatchId,@DueUtc,@ExpiresUtc,@Priority,@UserId);
 COMMIT;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadTakeoverCandidates @UserId int
AS BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 EXEC dbo.RequirePermission @UserId,N''Communication.View'';
 EXEC dbo.RequirePermission @UserId,N''Students.View'';
 EXEC dbo.RequirePermission @UserId,N''Staff.View'';
 EXEC dbo.RequirePermission @UserId,N''Fees.View'';
 SELECT TOP(100) B.BatchId,B.CreatedUtc,B.Channel,B.Audience,
   B.CreatedBy OriginalCreatorId,COALESCE(Owner.Username,CONVERT(nvarchar(30),
     COALESCE(Takeover.NewOwnerId,B.CreatedBy))) CurrentOwner,
   COUNT_BIG(R.RecipientId) PendingRecipients
 FROM msg.Batch B
 JOIN msg.ApiBatch A ON A.BatchId=B.BatchId
 JOIN msg.BatchSchedule S ON S.BatchId=B.BatchId AND S.HoldForReview=1
 JOIN msg.Recipient R ON R.BatchId=B.BatchId AND R.State=''Pending''
 OUTER APPLY (SELECT TOP(1) NewOwnerId FROM msg.BatchTakeover
    WHERE BatchId=B.BatchId ORDER BY TakeoverId DESC) Takeover
 LEFT JOIN dbo.Users Owner ON Owner.UserId=COALESCE(Takeover.NewOwnerId,B.CreatedBy)
 WHERE B.State IN (''Confirmed'',''Sending'') AND msg.BatchRecoveryBlocked(B.BatchId)=0
 GROUP BY B.BatchId,B.CreatedUtc,B.Channel,B.Audience,B.CreatedBy,
    Owner.Username,Takeover.NewOwnerId
 ORDER BY B.CreatedUtc DESC;
END');

 INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
 VALUES(N'33',SYSDATETIME(),SUSER_SNAME(),N'Preserve recovery holds during schedule changes and administrator takeover');
 COMMIT;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK;
 THROW;
END CATCH;
