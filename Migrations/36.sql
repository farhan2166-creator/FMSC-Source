-- Recovery-safe bounded communication maintenance. All school and financial tables,
-- prepared batch history, claimed provider attempts and their audit evidence are preserved.
SET NOCOUNT ON;SET XACT_ABORT ON;
IF EXISTS(SELECT 1 FROM dbo.DatabaseVersion WHERE TRY_CONVERT(int,VersionNumber)=36) RETURN;
IF ISNULL((SELECT TOP(1) TRY_CONVERT(int,VersionNumber) FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0)<>35
 THROW 51000,'Communication operational retention requires the preceding migration.',1;
BEGIN TRY
 BEGIN TRANSACTION;

-- Retain the exact original source identity after retiring an old, unprepared
-- suggestion. A new copy may never become eligible merely because its detailed
-- operational row was archived. This ledger is intentionally non-expiring.
CREATE TABLE msg.AutomationSourceTombstone(
 EventCode varchar(32) NOT NULL,
 SourceKey varchar(96) NOT NULL,
 ArchivedUtc datetime2(3) NOT NULL CONSTRAINT DF_MsgSourceTombstoneArchivedUtc DEFAULT SYSUTCDATETIME(),
 CONSTRAINT PK_MsgAutomationSourceTombstone PRIMARY KEY(EventCode,SourceKey)
);

EXEC(N'CREATE OR ALTER TRIGGER msg.TR_AutomationSourceTombstoneImmutable
ON msg.AutomationSourceTombstone AFTER UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 THROW 51511,''An archived communication source identity cannot be changed or deleted.'',1;
END;');

EXEC(N'CREATE OR ALTER TRIGGER msg.TR_AutomationCandidateNoReintake
ON msg.AutomationCandidate AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted I JOIN msg.AutomationSourceTombstone T WITH(HOLDLOCK)
   ON T.EventCode=I.EventCode AND T.SourceKey=I.SourceKey)
  THROW 51512,''An archived communication event cannot be offered a second time.'',1;
END;');

-- Re-create the two existing, trusted intake procedures with one additional
-- negative lookup for the small immutable source-key ledger. The ordinary
-- unique constraints and all source/consent/authorization checks remain.
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

EXEC(N'CREATE OR ALTER PROCEDURE msg.ScanFeeReviewCandidates @UserId int
AS BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 EXEC dbo.RequirePermission @UserId,N''Students.View'';
 EXEC dbo.RequirePermission @UserId,N''Fees.View'';
 DECLARE @Day date=CONVERT(date,
   (SYSUTCDATETIME() AT TIME ZONE ''UTC'') AT TIME ZONE
   (SELECT TimeZoneId FROM msg.DispatchControl WHERE ControlId=1));
 DECLARE @Week date=DATEADD(day,-(DATEDIFF(day,''19000101'',@Day)%7),@Day);
 BEGIN TRANSACTION;
 DECLARE @Locked int;
 EXEC @Locked=sys.sp_getapplock @Resource=N''FMSC_msg_automation_intake'',
   @LockMode=''Exclusive'',@LockOwner=''Transaction'',@LockTimeout=10000;
 IF @Locked<0 THROW 51281,''School event intake is busy.'',1;
 UPDATE msg.AutomationCandidate
 SET State=''Expired''
 WHERE State=''Pending'' AND EventCode IN(''OutstandingFees'',''UpcomingDue'',''OverdueFollowUp'')
   AND CreatedUtc<DATEADD(day,-7,SYSUTCDATETIME());
 ;WITH Candidates AS
 (
   SELECT R.EventCode,S.AdmissionId,
     CONCAT(R.EventCode,'':'',CONVERT(char(10),@Week,23),'':'',
        CASE WHEN Family.FamilyId IS NOT NULL THEN CONCAT(''F'',Family.FamilyId)
        ELSE CONCAT(''S'',S.AdmissionId) END) SourceKey,
     ROW_NUMBER() OVER(PARTITION BY R.EventCode,
       CASE WHEN Family.FamilyId IS NOT NULL THEN CONCAT(''F'',Family.FamilyId)
       ELSE CONCAT(''S'',S.AdmissionId) END
       ORDER BY S.AdmissionId DESC) FamilyRank
   FROM msg.AutomationRule R
   JOIN dbo.Students S ON S.Status=N''Active''
   LEFT JOIN dbo.FamilyMembers Family ON Family.AdmissionId=S.AdmissionId
   OUTER APPLY msg.RecordGuardian(S.AdmissionId) G
   OUTER APPLY
   (
     SELECT TOP(1) CCS.CampusId,CCS.ClassId
     FROM dbo.Enrollments E
     JOIN dbo.CampusClassSections CCS ON CCS.ClassSectionId=E.ClassSectionId
     WHERE E.AdmissionId=S.AdmissionId AND E.Status=N''Active''
     ORDER BY E.EnrollmentId DESC
   ) Placement
   CROSS APPLY msg.LiveFeeEventEligible(S.AdmissionId,R.EventCode,@Day) Eligible
   CROSS APPLY msg.FeeReminderEligible(S.AdmissionId,R.EventCode,@Day) Policy
   WHERE R.EventCode IN(''OutstandingFees'',''UpcomingDue'',''OverdueFollowUp'')
     AND R.Mode IN(''Review'',''Automatic'')
     AND G.HasConsent=1 AND G.PhoneE164 IS NOT NULL
     AND Placement.CampusId IS NOT NULL
     AND (R.CampusId=0 OR R.CampusId=Placement.CampusId)
     AND (R.ClassId=0 OR R.ClassId=Placement.ClassId)
     AND NOT EXISTS(SELECT 1 FROM msg.AutomationException X WITH(HOLDLOCK)
       WHERE (X.FamilyId=Family.FamilyId OR X.AdmissionId=S.AdmissionId)
         AND X.EventCode=R.EventCode AND X.RevokedUtc IS NULL
         AND (X.UntilUtc IS NULL OR X.UntilUtc>SYSUTCDATETIME()))
 )
 INSERT msg.AutomationCandidate(EventCode,SourceKey,AdmissionId,SourceRecordId)
 SELECT TOP(300) C.EventCode,C.SourceKey,C.AdmissionId,C.AdmissionId
 FROM Candidates C
 WHERE C.FamilyRank=1
   AND NOT EXISTS(SELECT 1 FROM msg.AutomationCandidate Old WITH(UPDLOCK,HOLDLOCK)
     WHERE Old.EventCode=C.EventCode AND Old.SourceKey=C.SourceKey)
   AND NOT EXISTS(SELECT 1 FROM msg.AutomationSourceTombstone Archived WITH(HOLDLOCK)
     WHERE Archived.EventCode=C.EventCode AND Archived.SourceKey=C.SourceKey)
 ORDER BY C.EventCode,C.AdmissionId;
 SELECT @@ROWCOUNT NewSuggestions;
 COMMIT;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.PruneTerminalCommunication
 @UserId int,@MaxRows int=200
AS BEGIN
 SET NOCOUNT ON;SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 IF @MaxRows IS NULL OR @MaxRows NOT BETWEEN 1 AND 200
  THROW 51510,''Invalid bounded communication maintenance request.'',1;
 BEGIN TRANSACTION;
 DECLARE @Lock int,@Now datetime2(3)=SYSUTCDATETIME(),@Plans int=0,@Archived int=0;
 EXEC @Lock=sys.sp_getapplock @Resource=N''FMSC_msg_automation_intake'',
  @LockMode=''Exclusive'',@LockOwner=''Transaction'',@LockTimeout=10000;
 IF @Lock<0 THROW 51510,''Communication maintenance is busy; no evidence was deleted.'',1;
 -- Delete only terminal, unbound work: it has no confirmed batch, attempt,
 -- provider request or historical sending outcome. A terminal event must also
 -- retain its candidate tombstone, while a recurrence must be non-resumable.
 ;WITH Retired AS
 (
  SELECT TOP(@MaxRows) W.WorkId FROM msg.PlannedWork W WITH(UPDLOCK,HOLDLOCK)
  WHERE W.BatchId IS NULL AND W.State IN(''Expired'',''Cancelled'',''Resolved'')
    AND W.ExpiresUtc<DATEADD(day,-30,@Now)
    AND ((W.SourceKind=''Event'' AND EXISTS(
      SELECT 1 FROM msg.AutomationCandidate C WITH(HOLDLOCK)
      WHERE C.CandidateId=W.CandidateId AND C.State IN(''Expired'',''Dismissed'')
        AND C.BatchId IS NULL))
     OR (W.SourceKind=''Recurrence'' AND EXISTS(
      SELECT 1 FROM msg.RecurringRule R WITH(HOLDLOCK)
      WHERE R.RuleId=W.RuleId AND R.State IN(''Cancelled'',''Completed'')
        AND R.EndUtc<DATEADD(day,-30,@Now))))
  ORDER BY W.ExpiresUtc,W.WorkId
 )
 DELETE W FROM msg.PlannedWork W JOIN Retired R ON R.WorkId=W.WorkId;
 SET @Plans=@@ROWCOUNT;
 -- Never remove Pending, Reviewed, Prepared or unresolved event history.
 -- The immutable source key takes over deduplication BEFORE the detailed
 -- expired, unprepared record is deleted, inside the same transaction.
 DECLARE @Archive TABLE(CandidateId bigint PRIMARY KEY,EventCode varchar(32),SourceKey varchar(96));
 INSERT @Archive(CandidateId,EventCode,SourceKey)
 SELECT TOP(@MaxRows) C.CandidateId,C.EventCode,C.SourceKey
 FROM msg.AutomationCandidate C WITH(UPDLOCK,HOLDLOCK)
 WHERE C.State IN(''Expired'',''Dismissed'') AND C.BatchId IS NULL
   AND C.CreatedUtc<DATEADD(day,-365,@Now)
   AND NOT EXISTS(SELECT 1 FROM msg.PlannedWork W WITH(HOLDLOCK)
     WHERE W.CandidateId=C.CandidateId)
 ORDER BY C.CreatedUtc,C.CandidateId;
 INSERT msg.AutomationSourceTombstone(EventCode,SourceKey)
 SELECT A.EventCode,A.SourceKey FROM @Archive A
 WHERE NOT EXISTS(SELECT 1 FROM msg.AutomationSourceTombstone T WITH(UPDLOCK,HOLDLOCK)
   WHERE T.EventCode=A.EventCode AND T.SourceKey=A.SourceKey);
 DELETE C FROM msg.AutomationCandidate C JOIN @Archive A ON A.CandidateId=C.CandidateId;
 SET @Archived=@@ROWCOUNT;
 IF @Archived<>(SELECT COUNT_BIG(*) FROM @Archive)
  THROW 51510,''Communication archival changed concurrently; nothing was committed.'',1;
 COMMIT;
 SELECT @Plans PrunedTerminalPlans,@Archived ArchivedExpiredSuggestions;
END;');

 INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
 VALUES(N'36',SYSDATETIME(),SUSER_SNAME(),
  N'Bounded archival of disposable communication work with immutable replay-prevention source keys');
 COMMIT;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK;
 THROW;
END CATCH;
