-- Review-only school event intake. It never creates a provider request,
-- schedules a batch, modifies school records, or sends anything.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF EXISTS(SELECT 1 FROM dbo.DatabaseVersion WHERE TRY_CONVERT(int,VersionNumber)=25) RETURN;
IF ISNULL((SELECT TOP(1) TRY_CONVERT(int,VersionNumber)
 FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0)<>24
 THROW 51280,'Automation intake requires schema 24.',1;
ALTER TABLE msg.AutomationRule ADD
 BaselineAdmissionId int NOT NULL CONSTRAINT DF_MsgAutoBaselineAdmission DEFAULT(0),
 BaselinePaymentId bigint NOT NULL CONSTRAINT DF_MsgAutoBaselinePayment DEFAULT(0);
-- A guardian with one unlinked student also needs a temporary event opt-out.
-- Exactly one scope (registered family OR individual admission) is authoritative.
ALTER TABLE msg.AutomationException DROP CONSTRAINT CK_MsgAutomationExceptionFamily;
ALTER TABLE msg.AutomationException ALTER COLUMN FamilyId int NULL;
ALTER TABLE msg.AutomationException ADD AdmissionId int NULL;
ALTER TABLE msg.AutomationException ADD CONSTRAINT CK_MsgAutomationExceptionScope
 CHECK((FamilyId>0 AND AdmissionId IS NULL)
   OR (FamilyId IS NULL AND AdmissionId>0));

CREATE TABLE msg.AutomationCandidate (
 CandidateId bigint IDENTITY(1,1) CONSTRAINT PK_MsgAutomationCandidate PRIMARY KEY,
 EventCode varchar(32) NOT NULL CONSTRAINT FK_MsgCandidateRule REFERENCES msg.AutomationRule(EventCode),
 SourceKey varchar(96) NOT NULL,
 AdmissionId int NOT NULL,
 SourceRecordId bigint NOT NULL,
 State varchar(12) NOT NULL CONSTRAINT DF_MsgCandidateState DEFAULT('Pending'),
 CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_MsgCandidateCreated DEFAULT SYSUTCDATETIME(),
 ReviewedBy int NULL,
 ReviewedUtc datetime2(3) NULL,
 BatchId uniqueidentifier NULL,
 CONSTRAINT UQ_MsgCandidateSource UNIQUE(EventCode,SourceKey),
 CONSTRAINT CK_MsgCandidateState CHECK(State IN ('Pending','Prepared','Dismissed','Expired')),
 CONSTRAINT CK_MsgCandidateReview CHECK(
   (State IN ('Pending','Expired') AND ReviewedBy IS NULL AND ReviewedUtc IS NULL AND BatchId IS NULL)
   OR (State='Dismissed' AND ReviewedBy IS NOT NULL AND ReviewedUtc IS NOT NULL AND BatchId IS NULL)
   OR (State='Prepared' AND ReviewedBy IS NOT NULL AND ReviewedUtc IS NOT NULL AND BatchId IS NOT NULL)),
 CONSTRAINT FK_MsgCandidateBatch FOREIGN KEY(BatchId) REFERENCES msg.Batch(BatchId)
);
CREATE INDEX IX_MsgAutomationCandidatePending ON msg.AutomationCandidate(State,CreatedUtc)
 INCLUDE(EventCode,AdmissionId);
EXEC(N'CREATE OR ALTER PROCEDURE msg.SaveAutomationMode
 @UserId int,@EventCode varchar(32),@Mode varchar(10),@CampusId int=0,@ClassId int=0
AS BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 IF @Mode NOT IN (''Off'',''Review'',''Automatic'') OR @CampusId<0 OR @ClassId<0
   OR (@ClassId>0 AND @CampusId=0)
  THROW 51246,''Invalid automation mode or audience.'',1;
 -- Automatic sending remains disabled until full verified event dispatch.
 IF @Mode=''Automatic'' OR
   (@Mode=''Review'' AND @EventCode NOT IN (''Admission'',''PaymentReceived'',''Birthday''))
  THROW 51247,''This automatic event source is not yet available. Use manual messages for now.'',1;
 BEGIN TRANSACTION;
 IF NOT EXISTS(SELECT 1 FROM msg.AutomationRule WITH(UPDLOCK,HOLDLOCK)
     WHERE EventCode=@EventCode)
  THROW 51246,''Unknown automation event.'',1;
 UPDATE msg.AutomationRule
 SET ActivationBaselineUtc=CASE WHEN Mode=''Off'' AND @Mode=''Review''
       THEN SYSUTCDATETIME() ELSE ActivationBaselineUtc END,
     BaselineAdmissionId=CASE WHEN Mode=''Off'' AND @Mode=''Review''
       THEN ISNULL((SELECT MAX(AdmissionId) FROM dbo.Students),0)
       ELSE BaselineAdmissionId END,
     BaselinePaymentId=CASE WHEN Mode=''Off'' AND @Mode=''Review''
       THEN ISNULL((SELECT MAX(FeePaymentId) FROM dbo.FeePayments),0)
       ELSE BaselinePaymentId END,
     Mode=@Mode,CampusId=@CampusId,ClassId=@ClassId,
     UpdatedUtc=SYSUTCDATETIME(),UpdatedBy=@UserId
 WHERE EventCode=@EventCode;
 COMMIT;
END');

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
 JOIN msg.AutomationRule R ON R.EventCode=''Admission'' AND R.Mode=''Review''
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
 JOIN msg.AutomationRule R ON R.EventCode=''PaymentReceived'' AND R.Mode=''Review''
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
 JOIN msg.AutomationRule R ON R.EventCode=''Birthday'' AND R.Mode=''Review''
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
   WHERE Existing.EventCode=C.EventCode AND Existing.SourceKey=C.SourceKey);
 SELECT @@ROWCOUNT NewSuggestions;
 COMMIT;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadAutomationCandidates @UserId int
AS BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 EXEC dbo.RequirePermission @UserId,N''Students.View'';
 EXEC dbo.RequirePermission @UserId,N''Fees.View'';
 DECLARE @Today date=CONVERT(date,
  (SYSUTCDATETIME() AT TIME ZONE ''UTC'') AT TIME ZONE
  (SELECT TimeZoneId FROM msg.DispatchControl WHERE ControlId=1));
 SELECT TOP(200) C.CandidateId,C.EventCode,C.SourceRecordId,C.AdmissionId,
   S.StudentName,S.AdmissionNumber,C.CreatedUtc
 FROM msg.AutomationCandidate C
 JOIN msg.AutomationRule R ON R.EventCode=C.EventCode AND R.Mode=''Review''
 JOIN dbo.Students S ON S.AdmissionId=C.AdmissionId AND S.Status=N''Active''
 OUTER APPLY msg.RecordGuardian(S.AdmissionId) G
 LEFT JOIN dbo.FamilyMembers FM ON FM.AdmissionId=S.AdmissionId
 WHERE C.State=''Pending'' AND G.HasConsent=1 AND G.PhoneE164 IS NOT NULL
   AND (C.EventCode<>''Birthday'' OR
       (C.SourceKey=CONCAT(''BD:'',YEAR(@Today),'':'',C.AdmissionId)
        AND MONTH(S.DateOfBirth)=MONTH(@Today) AND DAY(S.DateOfBirth)=DAY(@Today)))
   AND NOT EXISTS(SELECT 1 FROM msg.AutomationException X
     WHERE (X.FamilyId=FM.FamilyId OR X.AdmissionId=S.AdmissionId) AND X.EventCode=C.EventCode
       AND X.RevokedUtc IS NULL AND (X.UntilUtc IS NULL OR X.UntilUtc>SYSUTCDATETIME()))
   AND (C.EventCode<>''PaymentReceived'' OR EXISTS(
     SELECT 1 FROM dbo.FeePayments P
      WHERE P.FeePaymentId=C.SourceRecordId AND P.AdmissionId=C.AdmissionId
        AND P.Status=N''Completed'' AND P.RelatedFeePaymentId IS NULL
        AND NOT EXISTS(SELECT 1 FROM dbo.FeePayments Adjustment
          WHERE Adjustment.RelatedFeePaymentId=P.FeePaymentId)))
 ORDER BY C.CreatedUtc,C.CandidateId;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.DismissAutomationCandidate @UserId int,
 @CandidateId bigint
AS BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 UPDATE msg.AutomationCandidate WITH(UPDLOCK)
 SET State=''Dismissed'',ReviewedBy=@UserId,ReviewedUtc=SYSUTCDATETIME()
 WHERE CandidateId=@CandidateId AND State=''Pending'';
 IF @@ROWCOUNT<>1 THROW 51282,''This suggestion is already resolved or no longer exists.'',1;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.MarkAutomationCandidatePrepared @UserId int,
 @CandidateId bigint,@BatchId uniqueidentifier
AS BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 BEGIN TRANSACTION;
 DECLARE @StudentId int,@EventCode varchar(32),@SourceRecordId bigint;
 SELECT @StudentId=AdmissionId,@EventCode=EventCode,@SourceRecordId=SourceRecordId
 FROM msg.AutomationCandidate WITH(UPDLOCK,HOLDLOCK)
 WHERE CandidateId=@CandidateId AND State=''Pending'';
 IF @StudentId IS NULL
    THROW 51283,''This suggested event is already resolved.'',1;
 DECLARE @Today date=CONVERT(date,
  (SYSUTCDATETIME() AT TIME ZONE ''UTC'') AT TIME ZONE
  (SELECT TimeZoneId FROM msg.DispatchControl WHERE ControlId=1));
 IF NOT EXISTS(SELECT 1 FROM dbo.Students S
   JOIN msg.AutomationRule R ON R.EventCode=@EventCode AND R.Mode=''Review''
   OUTER APPLY msg.RecordGuardian(S.AdmissionId) Guardian
   LEFT JOIN dbo.FamilyMembers Family ON Family.AdmissionId=S.AdmissionId
   WHERE S.AdmissionId=@StudentId AND S.Status=N''Active''
      AND Guardian.HasConsent=1 AND Guardian.PhoneE164 IS NOT NULL
      AND NOT EXISTS(SELECT 1 FROM msg.AutomationException X
         WHERE (X.FamilyId=Family.FamilyId OR X.AdmissionId=S.AdmissionId) AND X.EventCode=@EventCode
         AND X.RevokedUtc IS NULL AND (X.UntilUtc IS NULL OR X.UntilUtc>SYSUTCDATETIME()))
      AND (@EventCode<>''Birthday'' OR
          (MONTH(S.DateOfBirth)=MONTH(@Today) AND DAY(S.DateOfBirth)=DAY(@Today)))
      AND (@EventCode<>''PaymentReceived'' OR EXISTS(
           SELECT 1 FROM dbo.FeePayments P
           WHERE P.FeePaymentId=@SourceRecordId AND P.AdmissionId=@StudentId
             AND P.Status=N''Completed'' AND P.RelatedFeePaymentId IS NULL
             AND NOT EXISTS(SELECT 1 FROM dbo.FeePayments Changed
                WHERE Changed.RelatedFeePaymentId=P.FeePaymentId))))
    THROW 51285,''This event is no longer eligible for review. Refresh the suggestions.'',1;
 IF NOT EXISTS(SELECT 1 FROM msg.Batch B
  JOIN msg.Recipient Recipient ON Recipient.BatchId=B.BatchId
  WHERE B.BatchId=@BatchId AND B.CreatedBy=@UserId AND B.ConfirmedBy=@UserId
   AND B.State IN (''Confirmed'',''Sending'',''Completed'',''Partial'')
   AND B.StudentId=@StudentId
   AND B.Audience IN (''Student'',''Family'')
   AND Recipient.AdmissionId=@StudentId)
    THROW 51284,''Only a confirmed message for the proposed school record can resolve this event.'',1;
 UPDATE msg.AutomationCandidate SET State=''Prepared'',ReviewedBy=@UserId,
  ReviewedUtc=SYSUTCDATETIME(),BatchId=@BatchId WHERE CandidateId=@CandidateId;
 COMMIT;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadAutomationFamilyExceptions @UserId int
AS BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 EXEC dbo.RequirePermission @UserId,N''Students.View'';
 SELECT E.ExceptionId,E.FamilyId,E.EventCode,E.UntilUtc,E.Reason,E.CreatedUtc,
   COALESCE(Student.StudentName,N''Family record'') Representative
 FROM msg.AutomationException E
 OUTER APPLY(SELECT TOP(1) S.StudentName FROM dbo.FamilyMembers FM
   JOIN dbo.Students S ON S.AdmissionId=FM.AdmissionId
   WHERE S.Status=N''Active'' AND
     ((E.FamilyId IS NOT NULL AND FM.FamilyId=E.FamilyId)
      OR (E.AdmissionId IS NOT NULL AND S.AdmissionId=E.AdmissionId))
   ORDER BY S.AdmissionId DESC) Student
 WHERE E.RevokedUtc IS NULL AND (E.UntilUtc IS NULL OR E.UntilUtc>SYSUTCDATETIME())
 ORDER BY E.CreatedUtc DESC,E.ExceptionId DESC;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.SaveAutomationFamilyException
 @UserId int,@AdmissionId int,@EventCode varchar(32),
 @UntilUtc datetime2(3)=NULL,@Reason nvarchar(200)
AS BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 EXEC dbo.RequirePermission @UserId,N''Students.View'';
 IF @AdmissionId<=0 OR @EventCode IS NULL OR
   NOT EXISTS(SELECT 1 FROM msg.AutomationRule WHERE EventCode=@EventCode) OR
   @Reason IS NULL OR LEN(LTRIM(RTRIM(@Reason)))=0 OR
   (@UntilUtc IS NOT NULL AND @UntilUtc<=SYSUTCDATETIME())
  THROW 51290,''A valid student, event, reason and future expiry are required.'',1;
 BEGIN TRANSACTION;
 IF NOT EXISTS(SELECT 1 FROM dbo.Students WITH(HOLDLOCK)
      WHERE AdmissionId=@AdmissionId AND Status=N''Active'')
   THROW 51291,''Choose an active school student for this exception.'',1;
 DECLARE @FamilyId int,@StandaloneAdmissionId int;
 IF (SELECT COUNT(DISTINCT FamilyId) FROM dbo.FamilyMembers WITH(UPDLOCK,HOLDLOCK)
     WHERE AdmissionId=@AdmissionId)>1
   THROW 51291,''The student has inconsistent family membership.'',1;
 SELECT @FamilyId=MIN(FamilyId) FROM dbo.FamilyMembers WITH(HOLDLOCK)
 WHERE AdmissionId=@AdmissionId;
 SET @StandaloneAdmissionId=CASE WHEN @FamilyId IS NULL THEN @AdmissionId END;
 UPDATE msg.AutomationException WITH(UPDLOCK,HOLDLOCK)
 SET RevokedBy=@UserId,RevokedUtc=SYSUTCDATETIME()
 WHERE ((@FamilyId IS NOT NULL AND FamilyId=@FamilyId)
   OR (@StandaloneAdmissionId IS NOT NULL AND AdmissionId=@StandaloneAdmissionId))
   AND EventCode=@EventCode AND RevokedUtc IS NULL;
 INSERT msg.AutomationException(FamilyId,AdmissionId,EventCode,UntilUtc,Reason,CreatedBy)
 VALUES(@FamilyId,@StandaloneAdmissionId,@EventCode,@UntilUtc,LTRIM(RTRIM(@Reason)),@UserId);
 SELECT CONVERT(bigint,SCOPE_IDENTITY()) ExceptionId,
    @FamilyId FamilyId,@StandaloneAdmissionId AdmissionId;
 COMMIT;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.RevokeAutomationFamilyException
 @UserId int,@ExceptionId bigint
AS BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 IF @ExceptionId<=0 THROW 51292,''Select a valid family exception.'',1;
 UPDATE msg.AutomationException WITH(UPDLOCK)
 SET RevokedBy=@UserId,RevokedUtc=SYSUTCDATETIME()
 WHERE ExceptionId=@ExceptionId AND RevokedUtc IS NULL;
 IF @@ROWCOUNT<>1 THROW 51292,''This family exception is already inactive.'',1;
END');

INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
VALUES(N'25',SYSDATETIME(),SUSER_SNAME(),
N'Review-only admission, payment and birthday event suggestion intake with source idempotency');
