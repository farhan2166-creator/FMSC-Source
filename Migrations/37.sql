-- Schema 37: prevent old archived source identities from starving newly eligible
-- admissions, payments or birthdays in bounded event-intake scans.
-- No school, fee, existing message or archived replay-protection data is changed.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF EXISTS(SELECT 1 FROM dbo.DatabaseVersion WHERE TRY_CONVERT(int,VersionNumber)=37) RETURN;
IF ISNULL((SELECT TOP(1) TRY_CONVERT(int,VersionNumber) FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0)<>36
 THROW 51000,'Communication intake correction requires schema 36.',1;
BEGIN TRY
 BEGIN TRANSACTION;
 -- Preserve schema-36 procedure behavior while moving every tombstone predicate
 -- ahead of TOP(300), so historical archived rows cannot consume a scan window.
EXEC(N'CREATE OR ALTER PROCEDURE msg.ScanAutomationCandidates @UserId int
AS BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 EXEC dbo.RequirePermission @UserId,N''Students.View'';
 EXEC dbo.RequirePermission @UserId,N''Fees.View'';
 DECLARE @Day date=CONVERT(date,
  (SYSUTCDATETIME() AT TIME ZONE ''UTC'') AT TIME ZONE
  (SELECT TimeZoneId FROM msg.DispatchControl WHERE ControlId=1));
 BEGIN TRANSACTION;
 DECLARE @LockResult int;
 EXEC @LockResult=sys.sp_getapplock
  @Resource=N''FMSC_msg_automation_intake'',@LockMode=''Exclusive'',
  @LockOwner=''Transaction'',@LockTimeout=10000;
 IF @LockResult<0 THROW 51281,''Automation review intake is busy.'',1;
 -- Expire unprepared suggestions; never silently send a late birthday or
 -- old transaction announcement. Historical keys remain as dedupe tombstones.
 UPDATE msg.AutomationCandidate
 SET State=''Expired''
 WHERE State=''Pending'' AND (
   (EventCode=''Admission'' AND CreatedUtc<DATEADD(day,-7,SYSUTCDATETIME()))
   OR (EventCode=''PaymentReceived'' AND CreatedUtc<DATEADD(day,-2,SYSUTCDATETIME()))
   OR (EventCode=''Birthday'' AND (
      SourceKey<>CONCAT(''BD:'',YEAR(@Day),'':'',AdmissionId)
      OR CONVERT(date,
         (CreatedUtc AT TIME ZONE ''UTC'') AT TIME ZONE
         (SELECT TimeZoneId FROM msg.DispatchControl WHERE ControlId=1))<>@Day))
 );
 DECLARE @Candidate TABLE(
  EventCode varchar(32) NOT NULL,SourceKey varchar(96) NOT NULL,
  AdmissionId int NOT NULL,SourceRecordId bigint NOT NULL,
  PRIMARY KEY(EventCode,SourceKey)
 );
 -- Admission: watermark is recorded when review mode is enabled, so
 -- historical students are not unexpectedly presented as new admissions.
 INSERT @Candidate
 SELECT TOP(300) ''Admission'',CONCAT(''AD:'',S.AdmissionId),S.AdmissionId,S.AdmissionId
 FROM dbo.Students S
 JOIN msg.AutomationRule R ON R.EventCode=''Admission'' AND R.Mode IN(''Review'',''Automatic'')
 CROSS APPLY msg.AutomationRecordInScope(S.AdmissionId,R.EventCode) RuleScope
 OUTER APPLY msg.RecordGuardian(S.AdmissionId) G
 LEFT JOIN dbo.FamilyMembers FM ON FM.AdmissionId=S.AdmissionId
 WHERE S.AdmissionId>R.BaselineAdmissionId AND S.Status=N''Active''
 AND G.HasConsent=1 AND G.PhoneE164 IS NOT NULL
 AND NOT EXISTS(SELECT 1 FROM msg.AutomationSourceTombstone Archived WITH(HOLDLOCK)
  WHERE Archived.EventCode=''Admission'' AND Archived.SourceKey=CONCAT(''AD:'',S.AdmissionId))
 AND NOT EXISTS(SELECT 1 FROM msg.AutomationCandidate Existing WITH(UPDLOCK,HOLDLOCK)
  WHERE Existing.EventCode=''Admission'' AND Existing.SourceKey=CONCAT(''AD:'',S.AdmissionId))
 AND NOT EXISTS(SELECT 1 FROM msg.AutomationException X WITH(HOLDLOCK)
  WHERE (X.FamilyId=FM.FamilyId OR X.AdmissionId=S.AdmissionId) AND X.EventCode=''Admission''
   AND X.RevokedUtc IS NULL AND (X.UntilUtc IS NULL OR X.UntilUtc>SYSUTCDATETIME()))
 ORDER BY S.AdmissionId;
 -- Payment acknowledgement: payment insert identity, not mutable payment date.
 -- A cancelled/refunded/adjustment record is never a new receipt.
 INSERT @Candidate
 SELECT TOP(300) ''PaymentReceived'',CONCAT(''PAY:'',P.FeePaymentId),P.AdmissionId,P.FeePaymentId
 FROM dbo.FeePayments P
 JOIN dbo.Students S ON S.AdmissionId=P.AdmissionId AND S.Status=N''Active''
 JOIN msg.AutomationRule R ON R.EventCode=''PaymentReceived'' AND R.Mode IN(''Review'',''Automatic'')
 CROSS APPLY msg.AutomationRecordInScope(S.AdmissionId,R.EventCode) RuleScope
 OUTER APPLY msg.RecordGuardian(S.AdmissionId) G
 LEFT JOIN dbo.FamilyMembers FM ON FM.AdmissionId=S.AdmissionId
 WHERE P.FeePaymentId>R.BaselinePaymentId AND P.Status=N''Completed''
   AND P.RelatedFeePaymentId IS NULL
   AND G.HasConsent=1 AND G.PhoneE164 IS NOT NULL
   AND NOT EXISTS(SELECT 1 FROM msg.AutomationSourceTombstone Archived WITH(HOLDLOCK)
       WHERE Archived.EventCode=''PaymentReceived'' AND Archived.SourceKey=CONCAT(''PAY:'',P.FeePaymentId))
   AND NOT EXISTS(SELECT 1 FROM msg.AutomationCandidate Existing WITH(UPDLOCK,HOLDLOCK)
       WHERE Existing.EventCode=''PaymentReceived''
       AND Existing.SourceKey=CONCAT(''PAY:'',P.FeePaymentId))
   AND NOT EXISTS(SELECT 1 FROM msg.AutomationException X WITH(HOLDLOCK)
      WHERE (X.FamilyId=FM.FamilyId OR X.AdmissionId=S.AdmissionId) AND X.EventCode=''PaymentReceived''
       AND X.RevokedUtc IS NULL AND (X.UntilUtc IS NULL OR X.UntilUtc>SYSUTCDATETIME()))
 ORDER BY P.FeePaymentId;
 -- Birthdays: today in the configured school timezone only, one per calendar
 -- year. No retroactive catch-up after holidays or an offline computer.
 INSERT @Candidate
 SELECT TOP(300) ''Birthday'',
    CONCAT(''BD:'',DATEPART(year,@Day),'':'',S.AdmissionId),S.AdmissionId,S.AdmissionId
 FROM dbo.Students S
 JOIN msg.AutomationRule R ON R.EventCode=''Birthday'' AND R.Mode IN(''Review'',''Automatic'')
 CROSS APPLY msg.AutomationRecordInScope(S.AdmissionId,R.EventCode) RuleScope
 OUTER APPLY msg.RecordGuardian(S.AdmissionId) G
 LEFT JOIN dbo.FamilyMembers FM ON FM.AdmissionId=S.AdmissionId
 WHERE S.Status=N''Active'' AND S.DateOfBirth IS NOT NULL
   AND MONTH(S.DateOfBirth)=MONTH(@Day) AND DAY(S.DateOfBirth)=DAY(@Day)
   AND G.HasConsent=1 AND G.PhoneE164 IS NOT NULL
   AND NOT EXISTS(SELECT 1 FROM msg.AutomationSourceTombstone Archived WITH(HOLDLOCK)
       WHERE Archived.EventCode=''Birthday'' AND Archived.SourceKey=CONCAT(''BD:'',DATEPART(year,@Day),'':'',S.AdmissionId))
   AND NOT EXISTS(SELECT 1 FROM msg.AutomationCandidate Existing WITH(UPDLOCK,HOLDLOCK)
      WHERE Existing.EventCode=''Birthday''
      AND Existing.SourceKey=CONCAT(''BD:'',DATEPART(year,@Day),'':'',S.AdmissionId))
   AND NOT EXISTS(SELECT 1 FROM msg.AutomationException X WITH(HOLDLOCK)
      WHERE (X.FamilyId=FM.FamilyId OR X.AdmissionId=S.AdmissionId) AND X.EventCode=''Birthday''
       AND X.RevokedUtc IS NULL AND (X.UntilUtc IS NULL OR X.UntilUtc>SYSUTCDATETIME()))
 ORDER BY S.AdmissionId;
 INSERT msg.AutomationCandidate(EventCode,SourceKey,AdmissionId,SourceRecordId)
 SELECT C.EventCode,C.SourceKey,C.AdmissionId,C.SourceRecordId
 FROM @Candidate C
 WHERE NOT EXISTS(SELECT 1 FROM msg.AutomationCandidate Existing WITH(UPDLOCK,HOLDLOCK)
   WHERE Existing.EventCode=C.EventCode AND Existing.SourceKey=C.SourceKey)
 AND NOT EXISTS(SELECT 1 FROM msg.AutomationSourceTombstone Archived WITH(HOLDLOCK)
   WHERE Archived.EventCode=C.EventCode AND Archived.SourceKey=C.SourceKey);
 DECLARE @NewSuggestions int=@@ROWCOUNT;
 -- One candidate per published notice and approved roster student.
 INSERT msg.AutomationCandidate(EventCode,SourceKey,AdmissionId,SourceRecordId)
 SELECT TOP(300) N.EventCode,CONCAT(''NOTICE:'',N.NoticeId,'':'',M.AdmissionId),M.AdmissionId,N.NoticeId
 FROM msg.SchoolNotice N WITH(HOLDLOCK)
 JOIN msg.AutomationRule R ON R.EventCode=N.EventCode AND R.Mode IN(''Review'',''Automatic'')
 JOIN msg.SchoolNoticeMember M WITH(HOLDLOCK) ON M.NoticeId=N.NoticeId
 JOIN dbo.Students S WITH(HOLDLOCK) ON S.AdmissionId=M.AdmissionId AND S.Status=N''Active''
 CROSS APPLY msg.AutomationRecordInScope(M.AdmissionId,N.EventCode) RuleScope
 CROSS APPLY msg.NoticeFamilyRepresentative(N.NoticeId,M.AdmissionId,N.EventCode) FamilyRepresentative
 OUTER APPLY msg.RecordGuardian(M.AdmissionId) Guardian
 LEFT JOIN dbo.FamilyMembers FM WITH(HOLDLOCK) ON FM.AdmissionId=M.AdmissionId
 WHERE N.RevokedUtc IS NULL AND N.ExpiresUtc>SYSUTCDATETIME()
 AND (R.ActivationBaselineUtc IS NULL OR N.ApprovedUtc>=R.ActivationBaselineUtc)
 AND Guardian.HasConsent=1 AND Guardian.PhoneE164 IS NOT NULL
 AND NOT EXISTS(SELECT 1 FROM msg.AutomationCandidate Existing WITH(UPDLOCK,HOLDLOCK)
  WHERE Existing.EventCode=N.EventCode AND Existing.SourceKey=CONCAT(''NOTICE:'',N.NoticeId,'':'',M.AdmissionId))
 AND NOT EXISTS(SELECT 1 FROM msg.AutomationSourceTombstone Archived WITH(HOLDLOCK)
   WHERE Archived.EventCode=N.EventCode AND Archived.SourceKey=CONCAT(''NOTICE:'',N.NoticeId,'':'',M.AdmissionId))
 AND NOT EXISTS(SELECT 1 FROM msg.AutomationException X WITH(HOLDLOCK)
  WHERE X.EventCode=N.EventCode AND (X.AdmissionId=M.AdmissionId OR X.FamilyId=FM.FamilyId)
  AND X.RevokedUtc IS NULL AND (X.UntilUtc IS NULL OR X.UntilUtc>SYSUTCDATETIME()))
 ORDER BY N.NoticeId,M.AdmissionId;
 SET @NewSuggestions+=@@ROWCOUNT;
 UPDATE C SET State=''Expired'' FROM msg.AutomationCandidate C
 JOIN msg.SchoolNotice N ON N.NoticeId=C.SourceRecordId AND N.EventCode=C.EventCode
 WHERE C.EventCode IN(''Announcement'',''ExaminationResult'') AND C.State=''Pending''
  AND (N.RevokedUtc IS NOT NULL OR N.ExpiresUtc<=SYSUTCDATETIME());
 SELECT @NewSuggestions NewSuggestions;
 COMMIT;
END');
 -- Search and page permission-scoped history before applying row limits.
EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadHistory @UserId int,@Limit int=100,@Search nvarchar(120)=NULL,@Offset int=0
AS
BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.View'';
 IF @Limit IS NULL OR @Limit<1 OR @Limit>200 OR @Offset IS NULL OR @Offset<0 OR @Offset>1000000
   THROW 51087,''History page or offset is outside safe limits.'',1;
 SELECT B.BatchId,B.Channel,B.Purpose,B.State,B.CreatedUtc,B.ConfirmedUtc,B.CreatedBy,B.Audience,
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
 WHERE EXISTS(SELECT 1 FROM msg.CanViewAudience(@UserId,B.Audience))
 AND (@Search IS NULL OR LTRIM(RTRIM(@Search))=N''''
 OR CHARINDEX(@Search,CONCAT(B.Channel,N'' '',B.Purpose,N'' '',B.Audience,N'' '',B.State,
          N'' '',COALESCE(U.Username,N''''),N'' '',COALESCE(A.ProviderCode,N''''),
          N'' '',CONVERT(nvarchar(23),B.CreatedUtc,120)))>0
 OR EXISTS(SELECT 1 FROM msg.Recipient SearchRecipient
 LEFT JOIN dbo.Students SearchStudent ON SearchStudent.AdmissionId=SearchRecipient.AdmissionId
 LEFT JOIN dbo.Staff SearchStaff ON SearchStaff.StaffId=SearchRecipient.StaffId
 WHERE SearchRecipient.BatchId=B.BatchId
 AND CHARINDEX(@Search,CONCAT(COALESCE(SearchStudent.StudentName,SearchStaff.StaffName,N''''),N'' '',
     COALESCE(SearchStudent.AdmissionNumber,SearchStaff.StaffNumber,N''''),N'' '',
     COALESCE(SearchStudent.FatherName,SearchStaff.FatherName,N''''),N'' '',
     COALESCE(SearchStudent.GuardianName,N'''')))>0))
 GROUP BY B.BatchId,B.Channel,B.Purpose,B.State,B.CreatedUtc,B.ConfirmedUtc,B.CreatedBy,B.Audience,U.Username,M.BatchId,A.ProviderCode
 ORDER BY B.CreatedUtc DESC,B.BatchId DESC
 OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY;
END');

 -- Independent, bounded status-check scheduling. No provider submission,
 -- batch mutation, retry authorization or delivery claim is created here.
 CREATE TABLE msg.AttemptStatusPoll
 (
   AttemptId bigint NOT NULL CONSTRAINT PK_MsgAttemptStatusPoll PRIMARY KEY
     CONSTRAINT FK_MsgAttemptStatusPollAttempt REFERENCES msg.Attempt(AttemptId),
   PollCount smallint NOT NULL CONSTRAINT DF_MsgAttemptStatusPollCount DEFAULT(0),
   LastPollUtc datetime2(3) NOT NULL,
   NextPollUtc datetime2(3) NOT NULL,
   CONSTRAINT CK_MsgAttemptStatusPollCount CHECK(PollCount BETWEEN 0 AND 24)
 );
 CREATE INDEX IX_MsgAttemptStatusPollNext ON msg.AttemptStatusPoll(NextPollUtc);

 EXEC(N'CREATE OR ALTER PROCEDURE msg.ClaimDueTwilioReceipts
  @UserId int,@EnrollmentId uniqueidentifier,@WindowsSid nvarchar(185),
  @SecretHash varbinary(32),@LeaseNonce uniqueidentifier,@MaxRows int=5
 AS BEGIN
  SET NOCOUNT ON;SET XACT_ABORT ON;
  IF @MaxRows IS NULL OR @MaxRows NOT BETWEEN 1 AND 5
   THROW 51530,''Invalid bounded delivery-status check request.'',1;
  BEGIN TRANSACTION;
  EXEC msg.VerifyPlanWorker @UserId,@EnrollmentId,@WindowsSid,@SecretHash,@LeaseNonce;
  DECLARE @Now datetime2(3)=SYSUTCDATETIME();
  -- This is disposable polling metadata, not a provider attempt, receipt,
  -- protected replay ledger or retained financial/history evidence.
  DELETE TOP(200) P FROM msg.AttemptStatusPoll P
   JOIN msg.Attempt A ON A.AttemptId=P.AttemptId
   WHERE A.RequestUtc<DATEADD(day,-8,@Now) OR A.Outcome IN(''Delivered'',''Failed'');
  DECLARE @Due TABLE(AttemptId bigint NOT NULL PRIMARY KEY);
  INSERT @Due(AttemptId)
  SELECT TOP(@MaxRows) A.AttemptId
  FROM msg.Attempt A WITH(UPDLOCK,READPAST)
  LEFT JOIN msg.AttemptStatusPoll P WITH(UPDLOCK) ON P.AttemptId=A.AttemptId
  WHERE A.ProviderCode=''Twilio'' AND A.Outcome IN(''Submitted'',''Accepted'')
   AND A.ProviderMessageId IS NOT NULL
   AND A.RequestUtc>=DATEADD(day,-7,@Now)
   AND A.RequestUtc<=DATEADD(minute,-2,@Now)
   AND (P.AttemptId IS NULL OR (P.PollCount<24 AND P.NextPollUtc<=@Now))
  ORDER BY A.RequestUtc,A.AttemptId;
  UPDATE P SET PollCount=P.PollCount+1,LastPollUtc=@Now,
   NextPollUtc=DATEADD(minute,CASE WHEN P.PollCount<5 THEN 5
    WHEN P.PollCount<12 THEN 30 ELSE 180 END,@Now)
  FROM msg.AttemptStatusPoll P JOIN @Due D ON D.AttemptId=P.AttemptId;
  INSERT msg.AttemptStatusPoll(AttemptId,PollCount,LastPollUtc,NextPollUtc)
  SELECT D.AttemptId,1,@Now,DATEADD(minute,5,@Now)
  FROM @Due D
  WHERE NOT EXISTS(SELECT 1 FROM msg.AttemptStatusPoll P WHERE P.AttemptId=D.AttemptId);
  COMMIT;
  SELECT AttemptId FROM @Due ORDER BY AttemptId;
 END;');
 INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
 VALUES(N'37',SYSDATETIME(),SUSER_SNAME(),N'Communication archived-event starvation guard');
 COMMIT;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK;
 THROW;
END CATCH;
