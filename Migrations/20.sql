-- Explicit API enrollment and immutable reviewed batches; no sender or scheduled job.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF EXISTS(SELECT 1 FROM dbo.DatabaseVersion WHERE TRY_CONVERT(int,VersionNumber)=20) RETURN;
IF ISNULL((SELECT TOP(1) TRY_CONVERT(int,VersionNumber) FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0)<>19
    THROW 51000,'Communication API changes require the preceding migration.',1;
CREATE TABLE msg.ApiBatch(
 BatchId uniqueidentifier NOT NULL PRIMARY KEY REFERENCES msg.Batch(BatchId),
 ProviderCode varchar(40) NOT NULL,
 ProfileHash varchar(64) NOT NULL,
 ReviewJson nvarchar(max) NOT NULL,
 TemplateJson nvarchar(max) NULL,
 CONSTRAINT CK_MsgApiReviewJson CHECK(ISJSON(ReviewJson)=1),
 CONSTRAINT CK_MsgApiTemplateJson CHECK(TemplateJson IS NULL OR ISJSON(TemplateJson)=1)
);

EXEC(N'CREATE PROCEDURE msg.EnableApiChannel @UserId int,@Channel varchar(10),@ProviderCode varchar(40)
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 IF NOT ((@Channel=''Sms'' AND @ProviderCode=''Twilio'') OR (@Channel=''WhatsApp'' AND @ProviderCode=''MetaCloud''))
  THROW 51130,''This API method is not supported.'',1;
 BEGIN TRANSACTION;
 IF NOT EXISTS(SELECT 1 FROM msg.ChannelSettings WITH(UPDLOCK,HOLDLOCK)
   WHERE Channel=@Channel AND ProviderCode=@ProviderCode AND Enabled=0)
  THROW 51130,''Save the selected provider and pause the channel before enabling it.'',1;
 -- Accepted messages are submitted, not pending sends; switching never resends them.
 IF EXISTS(SELECT 1 FROM msg.Batch B WITH(UPDLOCK,HOLDLOCK)
   JOIN msg.Recipient R WITH(HOLDLOCK) ON R.BatchId=B.BatchId
   LEFT JOIN msg.ManualHandoff H ON H.RecipientId=R.RecipientId
   LEFT JOIN msg.ApiBatch A ON A.BatchId=B.BatchId
   WHERE B.Channel=@Channel AND
    ((H.State IN (''Pending'',''Opened'')) OR (H.RecipientId IS NULL
      AND R.State IN (''Pending'',''Unknown'',''Submitted'') AND (A.BatchId IS NULL OR A.ProviderCode<>@ProviderCode))))
  THROW 51130,''Finish older communication sessions before switching delivery methods.'',1;
 UPDATE msg.ChannelSettings SET Enabled=1,UpdatedBy=@UserId,UpdatedUtc=SYSUTCDATETIME() WHERE Channel=@Channel;
 COMMIT;
END');

EXEC(N'CREATE PROCEDURE msg.ConfirmReviewedApiBatch
 @UserId int,@ClientRequestId uniqueidentifier,@Channel varchar(10),@Purpose varchar(20),
 @Body nvarchar(1600),@Audience varchar(20),@CampusId int,@ClassId int,@StudentId int,@StaffId int,
 @Recipients msg.RecipientSelection READONLY,@ProviderCode varchar(40),@ProfileHash varchar(64),
 @ReviewJson nvarchar(max),@TemplateJson nvarchar(max)=NULL
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 IF ISNULL(ISJSON(@ReviewJson),0)<>1 OR LEN(@ProfileHash)<>64 OR @ProfileHash LIKE ''%[^0-9A-F]%'' COLLATE Latin1_General_100_BIN2
  OR NOT ((@Channel=''Sms'' AND @ProviderCode=''Twilio'' AND @TemplateJson IS NULL)
       OR (@Channel=''WhatsApp'' AND @ProviderCode=''MetaCloud'' AND ISNULL(ISJSON(@TemplateJson),0)=1))
  THROW 51131,''The API review is incomplete.'',1;
 DECLARE @Review TABLE(Kind varchar(8),RecordId int,Phone varchar(16),Hash varchar(64),Text nvarchar(1600));
 INSERT @Review SELECT Kind,RecordId,Phone,Hash,Text FROM OPENJSON(@ReviewJson)
  WITH(Kind varchar(8),RecordId int,Phone varchar(16),Hash varchar(64),Text nvarchar(1600));
 IF EXISTS(SELECT 1 FROM @Review WHERE NULLIF(LTRIM(RTRIM(Text)),N'''') IS NULL OR Hash IS NULL OR LEN(Hash)<>64 OR Hash LIKE ''%[^0-9A-F]%'' COLLATE Latin1_General_100_BIN2)
  OR (SELECT COUNT(*) FROM @Review)<>(SELECT COUNT(*) FROM @Recipients)
  OR EXISTS(SELECT Kind,RecordId,Phone FROM @Review EXCEPT SELECT RecipientKind,RecordId,PhoneE164 FROM @Recipients)
  OR EXISTS(SELECT RecipientKind,RecordId,PhoneE164 FROM @Recipients EXCEPT SELECT Kind,RecordId,Phone FROM @Review)
  THROW 51131,''The reviewed API recipients changed.'',1;
 BEGIN TRANSACTION;
 IF NOT EXISTS(SELECT 1 FROM msg.ChannelSettings WITH(UPDLOCK,HOLDLOCK)
  WHERE Channel=@Channel AND ProviderCode=@ProviderCode AND Enabled=1)
  THROW 51131,''The reviewed sending method is paused or changed.'',1;
 DECLARE @Result TABLE(BatchId uniqueidentifier,RecipientCount bigint,AlreadyConfirmed bit);
 INSERT @Result EXEC msg.ConfirmBatch @UserId,@ClientRequestId,@Channel,@Purpose,@Body,@Audience,
  @CampusId,@ClassId,@StudentId,@StaffId,@Recipients;
 DECLARE @BatchId uniqueidentifier=(SELECT BatchId FROM @Result);
 IF EXISTS(SELECT 1 FROM msg.ManualBatch WHERE BatchId=@BatchId)
  THROW 51131,''A manual handoff cannot become an API batch.'',1;
 IF EXISTS(SELECT 1 FROM msg.ApiBatch WITH(UPDLOCK,HOLDLOCK) WHERE BatchId=@BatchId)
 BEGIN
  IF NOT EXISTS(SELECT 1 FROM msg.ApiBatch WHERE BatchId=@BatchId AND ProviderCode=@ProviderCode
   AND ProfileHash=@ProfileHash AND ReviewJson COLLATE Latin1_General_100_BIN2=@ReviewJson COLLATE Latin1_General_100_BIN2
   AND ISNULL(TemplateJson,N'''') COLLATE Latin1_General_100_BIN2=ISNULL(@TemplateJson,N'''') COLLATE Latin1_General_100_BIN2)
   THROW 51131,''This confirmation already contains another API review.'',1;
 END
 ELSE
 BEGIN
  IF EXISTS(SELECT 1 FROM msg.Attempt A JOIN msg.Recipient R ON R.RecipientId=A.RecipientId WHERE R.BatchId=@BatchId)
   THROW 51131,''An earlier attempted batch cannot be rebound.'',1;
  INSERT msg.ApiBatch VALUES(@BatchId,@ProviderCode,@ProfileHash,@ReviewJson,@TemplateJson);
 END;
 SELECT * FROM @Result;
 COMMIT;
END');

EXEC(N'CREATE PROCEDURE msg.ReadApiBatch @UserId int,@BatchId uniqueidentifier
AS
BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 DECLARE @Audience varchar(20),@Owner int;
 SELECT @Audience=Audience,@Owner=CreatedBy FROM msg.Batch WHERE BatchId=@BatchId;
 IF @Audience IS NULL THROW 51132,''The communication batch does not exist.'',1;
 IF @Owner<>@UserId EXEC dbo.RequirePermission @UserId,N''Communication.View'';
 IF @Audience=''Staff'' EXEC dbo.RequirePermission @UserId,N''Staff.View'';
 ELSE EXEC dbo.RequirePermission @UserId,N''Students.View'';
 IF @Audience=''OutstandingFees'' EXEC dbo.RequirePermission @UserId,N''Fees.View'';
 SELECT B.BatchId,B.Channel,B.Body,B.Audience,B.CampusId,B.ClassId,B.StudentId,B.StaffId,
 A.ProviderCode,A.ProfileHash,A.ReviewJson,A.TemplateJson,
 R.RecipientId,R.RecipientKind,COALESCE(R.AdmissionId,R.StaffId) RecordId,R.PhoneE164,R.State,J.Text RenderedBody,
 COALESCE(S.StudentName,T.StaffName,N''School record'') RecipientName,
 LastAttempt.AttemptId,LastAttempt.ProviderMessageId,LastAttempt.FailureCode
 FROM msg.ApiBatch A JOIN msg.Batch B ON B.BatchId=A.BatchId
 JOIN msg.Recipient R ON R.BatchId=B.BatchId
 CROSS APPLY OPENJSON(A.ReviewJson) WITH(Kind varchar(8),RecordId int,Phone varchar(16),Text nvarchar(1600)) J
 LEFT JOIN dbo.Students S ON S.AdmissionId=R.AdmissionId
 LEFT JOIN dbo.Staff T ON T.StaffId=R.StaffId
 OUTER APPLY(SELECT TOP(1) AttemptId,ProviderMessageId,FailureCode FROM msg.Attempt X
  WHERE X.RecipientId=R.RecipientId ORDER BY X.AttemptNumber DESC) LastAttempt
 WHERE B.BatchId=@BatchId AND J.Kind=R.RecipientKind AND J.RecordId=COALESCE(R.AdmissionId,R.StaffId) AND J.Phone=R.PhoneE164 ORDER BY R.RecipientId;
END');

EXEC(N'CREATE OR ALTER TRIGGER msg.TR_Attempt_RejectManualProvider ON msg.Attempt AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted I JOIN msg.Recipient R ON R.RecipientId=I.RecipientId
   LEFT JOIN msg.ApiBatch A ON A.BatchId=R.BatchId
   WHERE I.ProviderCode IN (''WhatsAppBusinessManual'',''EmailClientManual'')
    OR EXISTS(SELECT 1 FROM msg.ManualBatch M WHERE M.BatchId=R.BatchId)
    OR (A.BatchId IS NOT NULL AND A.ProviderCode<>I.ProviderCode)
    OR (I.ProviderCode=''MetaCloud'' AND A.BatchId IS NULL))
  THROW 51116,''The reviewed sending method does not permit this API attempt.'',1;

 -- Shared guardian fee amounts and consent are checked under the same claim
 -- transaction as the representative. No stale sibling can slip through while
 -- the desktop awaits provider-template validation.
 DECLARE @Expected TABLE(RecipientId bigint,AdmissionId int,Outstanding decimal(19,2));
 INSERT @Expected
 SELECT I.RecipientId,F.AdmissionId,F.Outstanding
 FROM inserted I JOIN msg.Recipient R ON R.RecipientId=I.RecipientId
 JOIN msg.ApiBatch A ON A.BatchId=R.BatchId JOIN msg.Batch B ON B.BatchId=A.BatchId
 CROSS APPLY OPENJSON(A.ReviewJson) WITH(Phone varchar(16),FeeMembers nvarchar(max) AS JSON) J
 CROSS APPLY OPENJSON(J.FeeMembers) WITH(AdmissionId int,Outstanding decimal(19,2)) F
 WHERE B.Audience=''OutstandingFees'' AND J.Phone=R.PhoneE164;
 DECLARE @Live TABLE(RecipientId bigint,AdmissionId int,Outstanding decimal(19,2));
 INSERT @Live
 SELECT I.RecipientId,S.AdmissionId,F.Outstanding
 FROM inserted I JOIN msg.Recipient R ON R.RecipientId=I.RecipientId
 JOIN msg.Batch B ON B.BatchId=R.BatchId JOIN msg.ApiBatch A ON A.BatchId=B.BatchId
 JOIN dbo.Students S WITH(HOLDLOCK) ON msg.NormalizeMobile(S.ContactNumber)=R.PhoneE164
 OUTER APPLY(SELECT TOP(1) E.ClassSectionId,E.Status FROM dbo.Enrollments E WITH(HOLDLOCK)
  WHERE E.AdmissionId=S.AdmissionId ORDER BY CASE WHEN E.Status=N''Active'' THEN 0 ELSE 1 END,E.EnrollmentId DESC) E
 LEFT JOIN dbo.CampusClassSections CCS WITH(HOLDLOCK) ON CCS.ClassSectionId=E.ClassSectionId
 OUTER APPLY(SELECT SUM(dbo.GetFeeChargeBalance(C.BillingChargeId)) Outstanding FROM dbo.BillingCharges C WITH(HOLDLOCK)
  WHERE C.AdmissionId=S.AdmissionId AND C.Status<>N''Cancelled'') F
 WHERE B.Audience=''OutstandingFees'' AND S.Status=N''Active'' AND S.SchoolMessageConsent=1 AND E.Status=N''Active''
  AND (B.CampusId=0 OR CCS.CampusId=B.CampusId) AND (B.ClassId=0 OR CCS.ClassId=B.ClassId)
  AND (B.StudentId=0 OR S.AdmissionId=B.StudentId) AND F.Outstanding>0;
 IF EXISTS(SELECT * FROM @Expected EXCEPT SELECT * FROM @Live)
  OR EXISTS(SELECT * FROM @Live EXCEPT SELECT * FROM @Expected)
  THROW 51133,''The reviewed family fee membership or balances changed. Review a new message.'',1;
END');

EXEC(N'CREATE TRIGGER msg.TR_ManualBatch_RejectApi ON msg.ManualBatch AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted I JOIN msg.ApiBatch A ON A.BatchId=I.BatchId)
  THROW 51131,''An API review cannot become a manual handoff.'',1;
END');

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
   OR (A.BatchId IS NOT NULL AND R.State IN (''Pending'',''Unknown'',''Accepted'',''Submitted'',''Failed'')))
 GROUP BY B.BatchId,B.Channel,B.Audience,B.CreatedUtc,M.BatchId,A.ProviderCode
 ORDER BY B.CreatedUtc DESC,B.BatchId;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadHistory @UserId int,@Limit int=100
AS
BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.View'';
 IF @Limit<1 OR @Limit>200 THROW 51087,''History page size must be between 1 and 200.'',1;
 SELECT TOP(@Limit) B.BatchId,B.Channel,B.Purpose,B.State,B.CreatedUtc,B.ConfirmedUtc,B.CreatedBy,B.Audience,
  COALESCE(U.Username,CONVERT(nvarchar(30),B.CreatedBy)) OperatorName,COUNT(R.RecipientId) Destinations,
  SUM(CASE WHEN M.BatchId IS NULL AND R.State=''Delivered'' THEN 1 ELSE 0 END) Delivered,
  SUM(CASE WHEN M.BatchId IS NULL AND R.State=''Failed'' THEN 1 ELSE 0 END) Failed,
  SUM(CASE WHEN M.BatchId IS NULL AND R.State=''Unknown'' THEN 1 ELSE 0 END) Unknown,
  SUM(CASE WHEN M.BatchId IS NULL AND R.State IN (''Submitted'',''Accepted'') THEN 1 ELSE 0 END) Accepted,
  SUM(CASE WHEN M.BatchId IS NULL AND R.State IN (''Pending'',''Submitted'',''Unknown'')
      OR M.BatchId IS NOT NULL AND H.State IN (''Pending'',''Opened'') THEN 1 ELSE 0 END) InProgress,
  CASE WHEN M.BatchId IS NOT NULL THEN ''WhatsApp Business (manual)'' ELSE COALESCE(A.ProviderCode,''Direct provider'') END Method,
  SUM(CASE WHEN H.State=''ReportedSent'' THEN 1 ELSE 0 END) ManualReported,
  SUM(CASE WHEN H.State=''Opened'' THEN 1 ELSE 0 END) ManualOpened,
  SUM(CASE WHEN H.State IN (''Skipped'',''Excluded'') THEN 1 ELSE 0 END) ManualSkipped
 FROM msg.Batch B LEFT JOIN dbo.Users U ON U.UserId=B.CreatedBy
 LEFT JOIN msg.Recipient R ON R.BatchId=B.BatchId
 LEFT JOIN msg.ManualBatch M ON M.BatchId=B.BatchId
 LEFT JOIN msg.ManualHandoff H ON H.RecipientId=R.RecipientId
 LEFT JOIN msg.ApiBatch A ON A.BatchId=B.BatchId
 GROUP BY B.BatchId,B.Channel,B.Purpose,B.State,B.CreatedUtc,B.ConfirmedUtc,B.CreatedBy,B.Audience,U.Username,M.BatchId,A.ProviderCode
 ORDER BY B.CreatedUtc DESC;
END');
EXEC(N'CREATE OR ALTER PROCEDURE msg.EnableManualWhatsApp @UserId int
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
    BEGIN TRANSACTION;
    IF EXISTS(
        SELECT 1 FROM msg.Batch B WITH(UPDLOCK,HOLDLOCK)
        JOIN msg.Recipient R WITH(HOLDLOCK) ON R.BatchId=B.BatchId
        WHERE B.Channel=''WhatsApp''
          AND R.State IN (''Pending'',''Unknown'',''Submitted'')
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


EXEC(N'CREATE PROCEDURE msg.CancelPendingApiMessages @UserId int,@BatchId uniqueidentifier
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 BEGIN TRANSACTION;
 DECLARE @Owner int,@Audience varchar(20);
 SELECT @Owner=B.CreatedBy,@Audience=B.Audience FROM msg.Batch B WITH(UPDLOCK,HOLDLOCK)
  JOIN msg.ApiBatch A ON A.BatchId=B.BatchId WHERE B.BatchId=@BatchId;
 IF @Owner IS NULL THROW 51132,''A reviewed API batch is required.'',1;
 IF @Owner<>@UserId EXEC dbo.RequirePermission @UserId,N''Communication.View'';
 IF @Audience=''Staff'' EXEC dbo.RequirePermission @UserId,N''Staff.View'';
 ELSE EXEC dbo.RequirePermission @UserId,N''Students.View'';
 IF @Audience=''OutstandingFees'' EXEC dbo.RequirePermission @UserId,N''Fees.View'';
 -- Attempted and uncertain messages retain their evidence and state.
 UPDATE msg.Recipient SET State=''Excluded'',ExclusionCode=''OperatorCancelled'',UpdatedUtc=SYSUTCDATETIME()
 WHERE BatchId=@BatchId AND State=''Pending'';
 IF NOT EXISTS(SELECT 1 FROM msg.Recipient WHERE BatchId=@BatchId AND State IN (''Pending'',''Unknown'',''Submitted'',''Accepted''))
  UPDATE msg.Batch SET State=''Completed'' WHERE BatchId=@BatchId;
 COMMIT;
END');
EXEC(N'CREATE PROCEDURE msg.RecordChannelOptOut @UserId int,@Channel varchar(10),@Phone nvarchar(100)
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 IF @Channel IS NULL OR @Channel NOT IN (''Sms'',''WhatsApp'')
  THROW 51134,''Choose SMS or WhatsApp for the requested opt-out.'',1;
 DECLARE @Number varchar(16)=msg.NormalizeMobile(@Phone);
 IF @Number IS NULL THROW 51134,''Enter a valid Pakistan mobile number.'',1;
 BEGIN TRANSACTION;
 IF NOT EXISTS(SELECT 1 FROM msg.Suppression WITH(UPDLOCK,HOLDLOCK) WHERE Channel=@Channel AND PhoneE164=@Number)
  INSERT msg.Suppression(Channel,PhoneE164,Source,Reason)
  VALUES(@Channel,@Number,''OptOut'',N''Phone contact requested no messages on this channel.'');
 COMMIT;
END');

INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes) VALUES(N'20',SYSDATETIME(),SUSER_SNAME(),N'Explicit API channels and immutable reviewed communication batches');
