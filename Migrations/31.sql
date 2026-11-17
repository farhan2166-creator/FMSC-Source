-- Surface recovery holds consistently before manual or provider dispatch.
SET NOCOUNT ON;SET XACT_ABORT ON;
IF EXISTS(SELECT 1 FROM dbo.DatabaseVersion WHERE TRY_CONVERT(int,VersionNumber)=31) RETURN;
IF ISNULL((SELECT TOP(1) TRY_CONVERT(int,VersionNumber) FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0)<>30
 THROW 51000,'Communication recovery visibility requires the preceding migration.',1;
BEGIN TRY
 BEGIN TRANSACTION;
 EXEC(N'CREATE OR ALTER FUNCTION msg.BatchRecoveryBlocked(@BatchId uniqueidentifier)
 RETURNS bit
 AS BEGIN
  DECLARE @Reviewed binary(32),@Current binary(32)=msg.RestoreBoundary();
  SELECT @Reviewed=RestoreBoundaryHash FROM msg.Batch WHERE BatchId=@BatchId;
  IF NOT EXISTS(SELECT 1 FROM msg.Batch WHERE BatchId=@BatchId) RETURN 1;
  IF @Reviewed<>@Current OR (@Reviewed IS NULL AND @Current IS NOT NULL)
    OR (@Reviewed IS NOT NULL AND @Current IS NULL) RETURN 1;
  RETURN 0;
 END;');
 -- No approval or provider outcome is changed by discovering the hold.
EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadMyUnfinishedBatches @UserId int
AS
BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 SELECT B.BatchId,B.Channel,B.Audience,B.CreatedUtc,COUNT_BIG(*) InProgress,
   CONVERT(varchar(40),CASE WHEN M.BatchId IS NOT NULL
     THEN ''WhatsApp Business (manual)'' ELSE A.ProviderCode END) Method,
   S.DueUtc,msg.BatchRecoveryBlocked(B.BatchId) RecoveryBlocked,
   CONVERT(datetime2(3),S.DueUtc AT TIME ZONE ''UTC'' AT TIME ZONE
      (SELECT TimeZoneId FROM msg.DispatchControl WHERE ControlId=1)) DueSchool,
   CONVERT(varchar(20),CASE
     WHEN msg.BatchRecoveryBlocked(B.BatchId)=1 THEN ''Needs Attention''
     WHEN SUM(CASE WHEN R.State IN (''Unknown'',''Failed'') THEN 1 ELSE 0 END)>0
       OR S.HoldForReview=1 OR (S.ExpiresUtc IS NOT NULL AND S.ExpiresUtc<=SYSUTCDATETIME())
       THEN ''Needs Attention''
     WHEN S.DueUtc>SYSUTCDATETIME() THEN ''Upcoming''
     ELSE ''All Active'' END) QueueStatus
 FROM msg.Batch B JOIN msg.Recipient R ON R.BatchId=B.BatchId
 LEFT JOIN msg.ManualBatch M ON M.BatchId=B.BatchId
 LEFT JOIN msg.ManualHandoff H ON H.RecipientId=R.RecipientId
 LEFT JOIN msg.ApiBatch A ON A.BatchId=B.BatchId
 LEFT JOIN msg.BatchSchedule S ON S.BatchId=B.BatchId
 WHERE (B.CreatedBy=@UserId OR EXISTS(
   SELECT 1 FROM dbo.Users U JOIN dbo.Roles AccessRole ON AccessRole.RoleId=U.RoleId
   WHERE U.UserId=@UserId AND AccessRole.RoleName=N''Administrator'') OR EXISTS(
   SELECT 1 FROM dbo.UserRoles UR JOIN dbo.Roles AccessRole ON AccessRole.RoleId=UR.RoleId
   WHERE UR.UserId=@UserId AND AccessRole.RoleName=N''Administrator'')) AND
   ((M.BatchId IS NOT NULL AND H.State IN (''Pending'',''Opened''))
   OR (A.BatchId IS NOT NULL AND
     (R.State IN (''Pending'',''Unknown'',''Submitted'',''Failed'')
      OR (R.State=''Accepted'' AND NOT
       (A.ProviderCode=''MetaCloud'' AND B.Channel=''WhatsApp'')))))
 GROUP BY B.BatchId,B.Channel,B.Audience,B.CreatedUtc,M.BatchId,A.ProviderCode,
  S.DueUtc,S.HoldForReview,S.ExpiresUtc
 ORDER BY B.CreatedUtc DESC,B.BatchId;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadApiBatch @UserId int,@BatchId uniqueidentifier
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
 IF @Audience IN (''OutstandingFees'',''FeeStudent'',''FeeFamily'') EXEC dbo.RequirePermission @UserId,N''Fees.View'';
 SELECT B.BatchId,B.Channel,B.Body,B.Audience,B.CampusId,B.ClassId,B.StudentId,B.StaffId,
 A.ProviderCode,A.ProfileHash,A.ReviewJson,A.TemplateJson,
 msg.BatchRecoveryBlocked(B.BatchId) RecoveryBlocked,
 CONVERT(datetime2(3),Schedule.DueUtc AT TIME ZONE ''UTC'' AT TIME ZONE
   (SELECT TimeZoneId FROM msg.DispatchControl WHERE ControlId=1)) DueSchool,
 (SELECT TimeZoneId FROM msg.DispatchControl WHERE ControlId=1) SchoolTimeZone,
 Schedule.DueUtc,Schedule.ExpiresUtc,Schedule.Priority,Schedule.HoldForReview,
 R.RecipientId,R.RecipientKind,COALESCE(R.AdmissionId,R.StaffId) RecordId,R.PhoneE164,R.State,J.Text RenderedBody,
 COALESCE(S.StudentName,T.StaffName,N''School record'') RecipientName,
 LastAttempt.AttemptId,LastAttempt.ProviderMessageId,LastAttempt.FailureCode
 FROM msg.ApiBatch A JOIN msg.Batch B ON B.BatchId=A.BatchId
 LEFT JOIN msg.BatchSchedule Schedule ON Schedule.BatchId=B.BatchId
 JOIN msg.Recipient R ON R.BatchId=B.BatchId
 CROSS APPLY OPENJSON(A.ReviewJson) WITH(Kind varchar(8),RecordId int,Phone varchar(16),Text nvarchar(1600)) J
 LEFT JOIN dbo.Students S ON S.AdmissionId=R.AdmissionId
 LEFT JOIN dbo.Staff T ON T.StaffId=R.StaffId
 OUTER APPLY(SELECT TOP(1) AttemptId,ProviderMessageId,FailureCode FROM msg.Attempt X
  WHERE X.RecipientId=R.RecipientId ORDER BY X.AttemptNumber DESC) LastAttempt
 WHERE B.BatchId=@BatchId AND J.Kind=R.RecipientKind AND J.RecordId=COALESCE(R.AdmissionId,R.StaffId) AND J.Phone=R.PhoneE164 ORDER BY R.RecipientId;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadManualHandoff @UserId int,@BatchId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
    IF NOT EXISTS(SELECT 1 FROM msg.ManualBatch WHERE BatchId=@BatchId)
        THROW 51113,''This is not a manual WhatsApp batch.'',1;
    DECLARE @Audience varchar(20);
    SELECT @Audience=B.Audience FROM msg.Batch B WHERE B.BatchId=@BatchId;
    IF NOT EXISTS(SELECT 1 FROM msg.ManualBatch WHERE BatchId=@BatchId AND CreatedBy=@UserId)
        EXEC dbo.RequirePermission @UserId,N''Communication.View'';
    IF @Audience=''Staff'' EXEC dbo.RequirePermission @UserId,N''Staff.View'';
    ELSE EXEC dbo.RequirePermission @UserId,N''Students.View'';
    IF @Audience IN (''OutstandingFees'',''FeeStudent'',''FeeFamily'') EXEC dbo.RequirePermission @UserId,N''Fees.View'';
    SELECT R.RecipientId,H.State,
        COALESCE(S.StudentName,T.StaffName,N''School record'') RecipientName,
        RIGHT(R.PhoneE164,4) PhoneLast4,
        H.OpenCount,H.LastOpenedUtc,H.ReportedUtc,H.ShellHandoffUtc,
        msg.BatchRecoveryBlocked(@BatchId) RecoveryBlocked,
        (SELECT E.RenderedBody FROM msg.ManualOpenEvidence E
         WHERE E.RecipientId=H.RecipientId AND E.OpenCount=H.OpenCount) RetainedBody
    FROM msg.ManualHandoff H
    JOIN msg.Recipient R ON R.RecipientId=H.RecipientId
    LEFT JOIN dbo.Students S ON S.AdmissionId=R.AdmissionId
    LEFT JOIN dbo.Staff T ON T.StaffId=R.StaffId
    WHERE R.BatchId=@BatchId ORDER BY R.RecipientId;
END;');

EXEC(N'CREATE OR ALTER PROCEDURE msg.OpenManualHandoff @UserId int,@RecipientId bigint
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
    IF @RecipientId IS NULL OR @RecipientId<=0 THROW 51114,''Invalid manual recipient.'',1;
    BEGIN TRANSACTION;
    DECLARE @BatchId uniqueidentifier,@State varchar(16),@Kind varchar(8),
        @AdmissionId int,@StaffId int,@Snapshot varchar(16),@Live varchar(16),
        @Audience varchar(20),@CampusId int,@ClassId int,@StudentId int,@TargetStaff int,
        @Body nvarchar(1600),@Excluded varchar(32),@OpenCount int;
    SELECT @BatchId=R.BatchId,@State=H.State,@Kind=R.RecipientKind,
        @AdmissionId=R.AdmissionId,@StaffId=R.StaffId,@OpenCount=H.OpenCount,
        @Snapshot=R.PhoneE164,@Body=B.Body,@Audience=B.Audience,
        @CampusId=B.CampusId,@ClassId=B.ClassId,@StudentId=CASE WHEN B.Audience=''OutstandingFees'' AND B.SingleStudentScope=0 THEN 0 ELSE B.StudentId END,@TargetStaff=B.StaffId
    FROM msg.ManualHandoff H WITH(UPDLOCK,HOLDLOCK)
    JOIN msg.Recipient R WITH(UPDLOCK,HOLDLOCK) ON R.RecipientId=H.RecipientId
    JOIN msg.ManualBatch M ON M.BatchId=R.BatchId
    JOIN msg.Batch B ON B.BatchId=R.BatchId
    WHERE H.RecipientId=@RecipientId AND B.Channel=''WhatsApp''
      AND R.State=''Pending'' AND B.State IN (''Confirmed'',''Sending'');
    IF @BatchId IS NULL OR @State NOT IN (''Pending'',''Opened'')
        THROW 51114,''This handoff was already processed, cancelled or excluded.'',1;
    IF NOT EXISTS(SELECT 1 FROM msg.ManualBatch WHERE BatchId=@BatchId AND CreatedBy=@UserId)
        EXEC dbo.RequirePermission @UserId,N''Communication.View'';
    IF msg.BatchRecoveryBlocked(@BatchId)=1
        THROW 51341,''This message predates database recovery. Check prior WhatsApp conversations and review a new request before opening another chat.'',1;
    -- Manual handoff remains operator-controlled while API automation is paused.
    IF NOT EXISTS(SELECT 1 FROM msg.ChannelSettings WITH(HOLDLOCK)
       WHERE Channel=''WhatsApp'' AND ProviderCode=''WhatsAppBusinessManual'' AND Enabled=1)
        THROW 51114,''Free WhatsApp handoff is paused or no longer configured.'',1;
    IF @Kind=''Staff'' EXEC dbo.RequirePermission @UserId,N''Staff.View'';
    ELSE EXEC dbo.RequirePermission @UserId,N''Students.View'';
    IF @Audience IN (''OutstandingFees'',''FeeStudent'',''FeeFamily'') EXEC dbo.RequirePermission @UserId,N''Fees.View'';

    -- Source and scope checks mirror msg.ClaimRecipient. No snapshot is
    -- usable when admission consent, placement, phone or opt-out changes.
    IF @Kind=''Staff''
    BEGIN
        IF @Audience=''Staff'' AND @TargetStaff=@StaffId
            SELECT @Live=msg.NormalizeMobile(T.ContactNumber)
            FROM dbo.Staff T WITH(HOLDLOCK)
            WHERE T.StaffId=@StaffId AND T.Status=N''Active'';
    END
    ELSE IF @Audience IN (''Student'',''Family'',''FeeStudent'',''FeeFamily'')
    BEGIN
        SELECT @Live=G.PhoneE164 FROM msg.RecordGuardian(@StudentId) G
        WHERE G.AdmissionId=@AdmissionId AND G.HasConsent=1;
    END
    ELSE
    BEGIN
        SELECT @Live=Guardian.PhoneE164
        FROM dbo.Students S WITH(HOLDLOCK)
        OUTER APPLY msg.RecordGuardian(S.AdmissionId) Guardian
        OUTER APPLY (
          SELECT TOP(1) E.ClassSectionId,E.Status
          FROM dbo.Enrollments E WITH(HOLDLOCK) WHERE E.AdmissionId=S.AdmissionId
          ORDER BY CASE WHEN E.Status=N''Active'' THEN 0 ELSE 1 END,E.EnrollmentId DESC
        ) E
        LEFT JOIN dbo.CampusClassSections CCS WITH(HOLDLOCK)
          ON CCS.ClassSectionId=E.ClassSectionId
        OUTER APPLY (
          SELECT SUM(dbo.GetFeeChargeBalance(BC.BillingChargeId)) Outstanding
          FROM dbo.BillingCharges BC WITH(HOLDLOCK)
          WHERE @Audience=''OutstandingFees'' AND BC.AdmissionId=S.AdmissionId
            AND BC.Status<>N''Cancelled''
        ) F
        WHERE S.AdmissionId=@AdmissionId AND S.Status=N''Active''
          AND Guardian.HasConsent=1 AND
          (@Audience=''School''
            OR (@Audience=''Campus'' AND E.Status=N''Active'' AND CCS.CampusId=@CampusId)
            OR (@Audience=''Class'' AND E.Status=N''Active'' AND CCS.CampusId=@CampusId
                AND CCS.ClassId=@ClassId)
            OR (@Audience=''OutstandingFees'' AND E.Status=N''Active''
                AND (@CampusId=0 OR CCS.CampusId=@CampusId)
                AND (@ClassId=0 OR CCS.ClassId=@ClassId)
                AND (@StudentId=0 OR S.AdmissionId=@StudentId) AND F.Outstanding>0));
    END;
    SET @Excluded=CASE
      WHEN NOT EXISTS(SELECT 1 FROM msg.PreparedEventEligible(@BatchId)) THEN ''SourceChanged''
      WHEN @Live IS NULL THEN ''NoLongerEligible''
      WHEN @Live<>@Snapshot THEN ''DestinationChanged''
      WHEN EXISTS(SELECT 1 FROM msg.Suppression O WITH(HOLDLOCK)
                  WHERE O.Channel=''WhatsApp'' AND O.PhoneE164=@Live)
           THEN ''ChannelOptOut''
      WHEN @Audience=''FeeFamily'' AND
        (EXISTS(SELECT AdmissionId FROM msg.ActiveFamilyFeeMembers(@StudentId)
           EXCEPT SELECT AdmissionId FROM msg.ReviewedFeeStudent WHERE RecipientId=@RecipientId)
         OR EXISTS(SELECT AdmissionId FROM msg.ReviewedFeeStudent WHERE RecipientId=@RecipientId
           EXCEPT SELECT AdmissionId FROM msg.ActiveFamilyFeeMembers(@StudentId))) THEN ''FamilyChanged''
      ELSE NULL END;
    IF @Excluded IS NOT NULL
    BEGIN
        UPDATE msg.ManualHandoff SET State=''Excluded'',UpdatedUtc=SYSUTCDATETIME(),
            ChangedBy=@UserId WHERE RecipientId=@RecipientId;
        UPDATE msg.Recipient SET State=''Excluded'',ExclusionCode=@Excluded,
            UpdatedUtc=SYSUTCDATETIME() WHERE RecipientId=@RecipientId;
        -- Auto-excluding the final target completes the manual session too.
        -- This records no send or delivery evidence.
        IF NOT EXISTS(SELECT 1 FROM msg.ManualHandoff H
            JOIN msg.Recipient R ON R.RecipientId=H.RecipientId
            WHERE R.BatchId=@BatchId AND H.State IN (''Pending'',''Opened''))
            UPDATE msg.Batch SET State=''Completed'' WHERE BatchId=@BatchId AND State=''Confirmed'';
        SELECT CONVERT(bit,0) Opened,CONVERT(varchar(16),NULL) PhoneE164,
          CONVERT(nvarchar(1600),NULL) Body,@Excluded ExclusionCode,@Audience Audience;
        COMMIT TRANSACTION; RETURN;
    END;
    UPDATE msg.ManualHandoff SET State=''Opened'',ShellHandoffUtc=NULL,OpenCount=OpenCount+1,
        LastOpenedUtc=SYSUTCDATETIME(),ChangedBy=@UserId,UpdatedUtc=SYSUTCDATETIME()
    WHERE RecipientId=@RecipientId;
    SELECT CONVERT(bit,1) Opened,@Live PhoneE164,@Body Body,
        CONVERT(varchar(32),NULL) ExclusionCode,@Audience Audience,@OpenCount+1 OpenCount;
    COMMIT TRANSACTION;
END;');

EXEC(N'CREATE OR ALTER PROCEDURE msg.RecordShellHandoff
 @UserId int,@RecipientId bigint,@OpenCount int,@RenderedBody nvarchar(1600),@ShellOpened bit=0
AS BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 IF @ShellOpened IS NULL OR @OpenCount IS NULL OR @OpenCount<=0 OR @RenderedBody IS NULL
   OR LEN(LTRIM(RTRIM(@RenderedBody)))=0
  THROW 51114,''The exact opened message and handoff reference are required.'',1;
 BEGIN TRANSACTION;
 DECLARE @Audience varchar(20),@EvidenceBatchId uniqueidentifier;
 SELECT @Audience=B.Audience,@EvidenceBatchId=B.BatchId FROM msg.ManualHandoff H WITH(UPDLOCK,HOLDLOCK)
 JOIN msg.Recipient R WITH(HOLDLOCK) ON R.RecipientId=H.RecipientId
 JOIN msg.Batch B WITH(HOLDLOCK) ON B.BatchId=R.BatchId
 WHERE H.RecipientId=@RecipientId AND H.OpenCount=@OpenCount
   AND H.State=''Opened'' AND H.ChangedBy=@UserId AND R.State=''Pending'';
 IF @Audience IS NULL THROW 51114,''The WhatsApp handoff changed. Review the current session.'',1;
 IF msg.BatchRecoveryBlocked(@EvidenceBatchId)=1
  THROW 51341,''The handoff predates database recovery. Check the original WhatsApp conversation before reporting its outcome.'',1;
 IF @ShellOpened=0 AND NOT EXISTS(SELECT 1 FROM msg.PreparedEventEligible(@EvidenceBatchId))
   THROW 51114,''The source event changed. Review the message again.'',1;
 IF @Audience=''Staff'' EXEC dbo.RequirePermission @UserId,N''Staff.View'';
 ELSE EXEC dbo.RequirePermission @UserId,N''Students.View'';
 IF @Audience IN (''OutstandingFees'',''FeeStudent'',''FeeFamily'') EXEC dbo.RequirePermission @UserId,N''Fees.View'';
 IF EXISTS(SELECT 1 FROM msg.ManualOpenEvidence WHERE RecipientId=@RecipientId AND OpenCount=@OpenCount)
 BEGIN
   IF NOT EXISTS(SELECT 1 FROM msg.ManualOpenEvidence
     WHERE RecipientId=@RecipientId AND OpenCount=@OpenCount AND PreparedBy=@UserId
      AND RenderedBody COLLATE Latin1_General_100_BIN2=@RenderedBody COLLATE Latin1_General_100_BIN2)
    THROW 51114,''This handoff already records different content. Review the current session.'',1;
 END
 ELSE
   INSERT msg.ManualOpenEvidence(RecipientId,OpenCount,RenderedBody,PreparedBy)
   VALUES(@RecipientId,@OpenCount,@RenderedBody,@UserId);
 IF @ShellOpened=1
 BEGIN
   UPDATE msg.ManualOpenEvidence
   SET LaunchedBy=COALESCE(LaunchedBy,@UserId),LaunchedUtc=COALESCE(LaunchedUtc,SYSUTCDATETIME())
   WHERE RecipientId=@RecipientId AND OpenCount=@OpenCount;
   UPDATE msg.ManualHandoff SET ShellHandoffUtc=COALESCE(ShellHandoffUtc,SYSUTCDATETIME())
   WHERE RecipientId=@RecipientId AND OpenCount=@OpenCount;
 END;
 COMMIT;
END;
');

EXEC(N'CREATE OR ALTER PROCEDURE msg.MarkManualHandoff
    @UserId int,@RecipientId bigint,@Outcome varchar(16),@RecoveredReport bit=0
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
    IF @RecipientId IS NULL OR @RecipientId<=0
       OR @Outcome IS NULL OR @Outcome NOT IN (''ReportedSent'',''Skipped'')
        THROW 51115,''Unknown manual handoff outcome.'',1;
    BEGIN TRANSACTION;
    DECLARE @BatchId uniqueidentifier,@State varchar(16),@Audience varchar(20),@ShellHandoffUtc datetime2(3);
    SELECT @State=H.State,@BatchId=R.BatchId,@Audience=B.Audience,@ShellHandoffUtc=H.ShellHandoffUtc
    FROM msg.ManualHandoff H WITH(UPDLOCK,HOLDLOCK)
    JOIN msg.Recipient R ON R.RecipientId=H.RecipientId
    JOIN msg.ManualBatch M ON M.BatchId=R.BatchId
    JOIN msg.Batch B ON B.BatchId=R.BatchId
    WHERE H.RecipientId=@RecipientId;
    IF @BatchId IS NULL OR (@State=''Pending'' AND @Outcome=''ReportedSent'')
       OR (@State NOT IN (''Pending'',''Opened'') AND @State<>@Outcome)
        THROW 51115,''Open a chat before reporting it sent; completed outcomes cannot be overwritten.'',1;
    IF NOT EXISTS(SELECT 1 FROM msg.ManualBatch WHERE BatchId=@BatchId AND CreatedBy=@UserId)
        EXEC dbo.RequirePermission @UserId,N''Communication.View'';
    IF @Audience=''Staff'' EXEC dbo.RequirePermission @UserId,N''Staff.View'';
    ELSE EXEC dbo.RequirePermission @UserId,N''Students.View'';
    IF @Audience IN (''OutstandingFees'',''FeeStudent'',''FeeFamily'') EXEC dbo.RequirePermission @UserId,N''Fees.View'';
    IF @State=''Opened'' AND @Outcome=''ReportedSent'' AND (@ShellHandoffUtc IS NULL OR msg.BatchRecoveryBlocked(@BatchId)=1) AND ISNULL(@RecoveredReport,0)=0
        THROW 51115,''Check WhatsApp before reporting an interrupted handoff.'',1;
    IF @State<>@Outcome
      UPDATE msg.ManualHandoff SET State=@Outcome,ChangedBy=@UserId,
        ReportedUtc=CASE WHEN @Outcome=''ReportedSent'' THEN SYSUTCDATETIME() ELSE NULL END,
        UpdatedUtc=SYSUTCDATETIME() WHERE RecipientId=@RecipientId;
    IF NOT EXISTS(SELECT 1 FROM msg.ManualHandoff H
          JOIN msg.Recipient R ON R.RecipientId=H.RecipientId
          WHERE R.BatchId=@BatchId AND H.State IN (''Pending'',''Opened''))
      UPDATE msg.Batch SET State=''Completed'' WHERE BatchId=@BatchId AND State=''Confirmed'';
    COMMIT TRANSACTION;
END;');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReleaseScheduledHold
 @UserId int,@BatchId uniqueidentifier
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 IF NOT EXISTS(SELECT 1 FROM msg.BatchSchedule S JOIN msg.ApiBatch A ON A.BatchId=S.BatchId
 WHERE S.BatchId=@BatchId) THROW 51265,''Only a reviewed API schedule can be released.'',1;
 IF msg.BatchRecoveryBlocked(@BatchId)=1
  THROW 51341,''This approval predates database recovery. Reconcile earlier submissions and review a new request.'',1;
 UPDATE msg.BatchSchedule SET HoldForReview=0,UpdatedBy=@UserId,
 UpdatedUtc=SYSUTCDATETIME() WHERE BatchId=@BatchId;
END');


 INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
 VALUES(N'31',SYSDATETIME(),SUSER_SNAME(),N'Explicit Queue and manual handoff restore holds with school-time scheduling');
 COMMIT;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK;
 THROW;
END CATCH;
