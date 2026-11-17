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
 INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
 VALUES(N'37',SYSDATETIME(),SUSER_SNAME(),N'Communication archived-event starvation guard');
 COMMIT;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK;
 THROW;
END CATCH;
