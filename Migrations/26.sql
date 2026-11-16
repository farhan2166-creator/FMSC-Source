-- Review-only fee suggestion intake. Posted balances and due dates are
-- verified live; the event ledger stores source IDs but never amounts/phones.
SET NOCOUNT ON; SET XACT_ABORT ON;
IF EXISTS(SELECT 1 FROM dbo.DatabaseVersion WHERE TRY_CONVERT(int,VersionNumber)=26) RETURN;
IF ISNULL((SELECT TOP(1) TRY_CONVERT(int,VersionNumber)
  FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0)<>25
 THROW 51280,'Fee suggestion intake requires schema 25.',1;
-- Administrator-visible review frequency controls. These do not enable
-- automatic dispatch, alter existing school records or create provider work.
ALTER TABLE msg.AutomationRule ADD
 FeeReminderGapDays int NOT NULL
  CONSTRAINT DF_MsgFeeReminderGapDays DEFAULT(7),
 FeeRemindersPerMonth int NOT NULL
  CONSTRAINT DF_MsgFeeRemindersPerMonth DEFAULT(2);
-- SQL Server binds CHECK expressions before a preceding ALTER ADD in the
-- same installer batch; defer compilation until those columns really exist.
EXEC(N'ALTER TABLE msg.AutomationRule ADD CONSTRAINT CK_MsgFeeReminderGapDays
 CHECK(FeeReminderGapDays BETWEEN 1 AND 90)');
EXEC(N'ALTER TABLE msg.AutomationRule ADD CONSTRAINT CK_MsgFeeRemindersPerMonth
 CHECK(FeeRemindersPerMonth BETWEEN 1 AND 12)');
ALTER TABLE msg.DispatchControl ADD
 RoutineFamilyDailyLimit int NOT NULL
  CONSTRAINT DF_MsgRoutineFamilyDailyLimit DEFAULT(3);
EXEC(N'ALTER TABLE msg.DispatchControl ADD CONSTRAINT CK_MsgRoutineFamilyDailyLimit
 CHECK(RoutineFamilyDailyLimit BETWEEN 1 AND 20)');

-- Language preferences are school-level settings. Existing templates remain
-- English; adding Urdu variants never translates or changes saved content.
ALTER TABLE msg.DispatchControl ADD DefaultMessageLanguage varchar(2) NOT NULL
 CONSTRAINT DF_MsgDefaultMessageLanguage DEFAULT('en');
ALTER TABLE msg.Template ADD LanguageCode varchar(2) NOT NULL
 CONSTRAINT DF_MsgTemplateLanguageCode DEFAULT('en');
EXEC(N'ALTER TABLE msg.DispatchControl ADD CONSTRAINT CK_MsgDefaultMessageLanguage
 CHECK(DefaultMessageLanguage IN (''en'',''ur''))');
EXEC(N'ALTER TABLE msg.Template ADD CONSTRAINT CK_MsgTemplateLanguageCode
 CHECK(LanguageCode IN (''en'',''ur''))');
-- Preserve the original (Name,Channel) uniqueness required by all
-- historical compatibility manifests. Urdu editions use a distinct name
-- (for example "Fee notice (Urdu)") and carry LanguageCode='ur'.


-- Preserve each school-approved template revision independently of current
-- editable content. A restore creates a NEW version, never erases the audit.
CREATE TABLE msg.TemplateRevision(
 RevisionId bigint IDENTITY(1,1) NOT NULL
  CONSTRAINT PK_MsgTemplateRevision PRIMARY KEY,
 TemplateId int NOT NULL CONSTRAINT FK_MsgTemplateRevisionTemplate
  REFERENCES msg.Template(TemplateId),
 RevisionNumber int NOT NULL,
 Name nvarchar(100) NOT NULL,
 Channel varchar(10) NOT NULL,
 Purpose varchar(20) NOT NULL,
 LanguageCode varchar(2) NOT NULL,
 Body nvarchar(1600) NOT NULL,
 Active bit NOT NULL,
 Operation varchar(12) NOT NULL,
 ChangedBy int NULL,
 ChangedUtc datetime2(3) NOT NULL CONSTRAINT DF_MsgTemplateRevisionUtc
  DEFAULT SYSUTCDATETIME(),
 CONSTRAINT UQ_MsgTemplateRevisionNumber UNIQUE(TemplateId,RevisionNumber),
 CONSTRAINT CK_MsgTemplateRevisionOperation
  CHECK(Operation IN ('Baseline','Created','Edited','Restored')),
 CONSTRAINT CK_MsgTemplateRevisionNumber CHECK(RevisionNumber>=1)
);
-- Bind the newly added LanguageCode column only after ALTER TABLE
-- completes: SQL Server compiles ordinary DML before all ALTERs in a batch.
EXEC(N'INSERT msg.TemplateRevision
 (TemplateId,RevisionNumber,Name,Channel,Purpose,LanguageCode,Body,Active,
  Operation,ChangedBy,ChangedUtc)
 SELECT TemplateId,1,Name,Channel,Purpose,LanguageCode,Body,Active,
  ''Baseline'',UpdatedBy,UpdatedUtc FROM msg.Template');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadTemplateRevisions
 @UserId int,@TemplateId int
AS BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 IF @TemplateId<=0 OR NOT EXISTS(SELECT 1 FROM msg.Template WHERE TemplateId=@TemplateId)
   THROW 51295,''Choose a saved school template to view its versions.'',1;
 SELECT RevisionNumber,Name,Channel,Purpose,LanguageCode,Body,Active,
        Operation,ChangedBy,ChangedUtc
 FROM msg.TemplateRevision
 WHERE TemplateId=@TemplateId ORDER BY RevisionNumber DESC;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.RestoreTemplateRevision
 @UserId int,@TemplateId int,@RevisionNumber int
AS BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 IF @TemplateId<=0 OR @RevisionNumber<=0
   THROW 51295,''Choose a saved template and an existing version.'',1;
 BEGIN TRANSACTION;
 IF NOT EXISTS(SELECT 1 FROM msg.Template WITH(UPDLOCK,HOLDLOCK)
      WHERE TemplateId=@TemplateId)
   THROW 51295,''This template no longer exists.'',1;
 DECLARE @Name nvarchar(100),@Channel varchar(10),@Purpose varchar(20),
         @LanguageCode varchar(2),@Body nvarchar(1600),@Active bit;
 SELECT @Name=Name,@Channel=Channel,@Purpose=Purpose,
        @LanguageCode=LanguageCode,@Body=Body,@Active=Active
 FROM msg.TemplateRevision
 WHERE TemplateId=@TemplateId AND RevisionNumber=@RevisionNumber;
 IF @Name IS NULL THROW 51295,''The chosen template version no longer exists.'',1;
 UPDATE msg.Template
 SET Name=@Name,Channel=@Channel,Purpose=@Purpose,LanguageCode=@LanguageCode,
     Body=@Body,Active=@Active,UpdatedBy=@UserId,UpdatedUtc=SYSUTCDATETIME()
 WHERE TemplateId=@TemplateId;
 INSERT msg.TemplateRevision
  (TemplateId,RevisionNumber,Name,Channel,Purpose,LanguageCode,Body,
   Active,Operation,ChangedBy)
 SELECT @TemplateId,MAX(RevisionNumber)+1,@Name,@Channel,@Purpose,
        @LanguageCode,@Body,@Active,''Restored'',@UserId
 FROM msg.TemplateRevision WHERE TemplateId=@TemplateId;
 COMMIT TRANSACTION;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadMessageLanguage @UserId int
AS BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequireAnyPermission @UserId,N''Communication.Send'',N''Communication.Configure'';
 SELECT DefaultMessageLanguage FROM msg.DispatchControl WHERE ControlId=1;
END');
EXEC(N'CREATE OR ALTER PROCEDURE msg.SaveMessageLanguage
 @UserId int,@LanguageCode varchar(2)
AS BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 IF @LanguageCode NOT IN (''en'',''ur'')
    THROW 51298,''Select English or Urdu.'',1;
 UPDATE msg.DispatchControl
 SET DefaultMessageLanguage=@LanguageCode,UpdatedBy=@UserId,
     UpdatedUtc=SYSUTCDATETIME() WHERE ControlId=1;
 IF @@ROWCOUNT<>1 THROW 51298,''School communication settings are missing.'',1;
END');
EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadComposeTemplates @UserId int
AS BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 SELECT TemplateId,Name,Channel,Purpose,Body,LanguageCode
 FROM msg.Template WHERE Active=1 ORDER BY Name,Channel,LanguageCode;
END');
EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadTemplates @UserId int
AS BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 SELECT TemplateId,Name,Channel,Purpose,Body,Active,UpdatedBy,UpdatedUtc,LanguageCode
 FROM msg.Template ORDER BY Name,Channel,LanguageCode;
END');
EXEC(N'CREATE OR ALTER PROCEDURE msg.SaveTemplate
 @UserId int,@TemplateId int=0,@Name nvarchar(100),
 @Channel varchar(10),@Purpose varchar(20),@Body nvarchar(1600),
 @Active bit=1,@LanguageCode varchar(2)=''en''
AS BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 IF @TemplateId<0 OR LEN(LTRIM(RTRIM(ISNULL(@Name,N''''))))=0 OR LEN(@Name)>100
   OR @Channel NOT IN (''Sms'',''WhatsApp'',''Email'')
   OR @Purpose NOT IN (''Announcement'',''FeeReminder'',''Individual'',''Staff'')
   OR @LanguageCode NOT IN (''en'',''ur'')
   OR LEN(LTRIM(RTRIM(ISNULL(@Body,N''''))))=0 OR LEN(@Body)>1600
   OR @Body LIKE N''%{{%'' OR @Body LIKE N''%}}%''
   THROW 51089,''Choose a valid school template, language and body.'',1;
 BEGIN TRANSACTION;
 DECLARE @SavedTemplateId int,@Operation varchar(12);
 IF @TemplateId=0
 BEGIN
   INSERT msg.Template(Name,Channel,Purpose,Body,Active,UpdatedBy,LanguageCode)
   VALUES(@Name,@Channel,@Purpose,@Body,@Active,@UserId,@LanguageCode);
   SET @SavedTemplateId=CONVERT(int,SCOPE_IDENTITY());
   SET @Operation=''Created'';
 END
 ELSE
 BEGIN
   IF NOT EXISTS(SELECT 1 FROM msg.Template WITH(UPDLOCK,HOLDLOCK)
        WHERE TemplateId=@TemplateId)
     THROW 51089,''The template no longer exists.'',1;
   UPDATE msg.Template SET Name=@Name,Channel=@Channel,Purpose=@Purpose,
     Body=@Body,Active=@Active,LanguageCode=@LanguageCode,UpdatedBy=@UserId,
     UpdatedUtc=SYSUTCDATETIME()
   WHERE TemplateId=@TemplateId;
   IF @@ROWCOUNT<>1 THROW 51089,''The template no longer exists.'',1;
   SET @SavedTemplateId=@TemplateId;
   SET @Operation=''Edited'';
 END;
 INSERT msg.TemplateRevision
  (TemplateId,RevisionNumber,Name,Channel,Purpose,LanguageCode,Body,
   Active,Operation,ChangedBy)
 SELECT T.TemplateId,COALESCE((SELECT MAX(R.RevisionNumber)
   FROM msg.TemplateRevision R WHERE R.TemplateId=T.TemplateId),0)+1,
   T.Name,T.Channel,T.Purpose,T.LanguageCode,T.Body,T.Active,@Operation,@UserId
 FROM msg.Template T WHERE T.TemplateId=@SavedTemplateId;
 COMMIT TRANSACTION;
 SELECT @SavedTemplateId TemplateId;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.SaveFeeReminderPolicy
 @UserId int,@EventCode varchar(32),@GapDays int,@PerMonth int,@PerDay int
AS BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 IF @EventCode NOT IN (''OutstandingFees'',''UpcomingDue'',''OverdueFollowUp'')
    OR @GapDays NOT BETWEEN 1 AND 90
    OR @PerMonth NOT BETWEEN 1 AND 12 OR @PerDay NOT BETWEEN 1 AND 20
   THROW 51288,''Choose valid fee reminder limits and an available fee event.'',1;
 BEGIN TRANSACTION;
 UPDATE msg.AutomationRule WITH(UPDLOCK,HOLDLOCK)
 SET FeeReminderGapDays=@GapDays,FeeRemindersPerMonth=@PerMonth,
     UpdatedUtc=SYSUTCDATETIME(),UpdatedBy=@UserId
 WHERE EventCode=@EventCode;
 IF @@ROWCOUNT<>1 THROW 51288,''This fee reminder event no longer exists.'',1;
 UPDATE msg.DispatchControl WITH(UPDLOCK,HOLDLOCK)
 SET RoutineFamilyDailyLimit=@PerDay,UpdatedUtc=SYSUTCDATETIME(),UpdatedBy=@UserId
 WHERE ControlId=1;
 IF @@ROWCOUNT<>1 THROW 51288,''Communication sending controls are missing.'',1;
 COMMIT;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadDispatchControl @UserId int
AS BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 SELECT ControlId,MasterPaused,UnattendedEnabled,WindowStart,WindowEnd,
        TimeZoneId,MaxPerMinute,UpdatedUtc,UpdatedBy,RoutineFamilyDailyLimit
 FROM msg.DispatchControl WHERE ControlId=1;
 SELECT Channel,ProviderCode,DailyLimit,ReservedImportant,UpdatedUtc
 FROM msg.ProviderQuota ORDER BY Channel,ProviderCode;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadAutomationRules @UserId int
AS BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 SELECT EventCode,Mode,CampusId,ClassId,ActivationBaselineUtc,UpdatedUtc,
        FeeReminderGapDays,FeeRemindersPerMonth
 FROM msg.AutomationRule ORDER BY EventCode;
END');

EXEC(N'CREATE OR ALTER FUNCTION msg.LiveFeeEventEligible
(@AdmissionId int,@EventCode varchar(32),@SchoolDate date)
RETURNS TABLE
AS RETURN
(
 SELECT TOP(1) C.BillingChargeId
 FROM dbo.BillingCharges C
 JOIN dbo.Students BilledStudent ON BilledStudent.AdmissionId=C.AdmissionId
  AND BilledStudent.Status=N''Active''
 WHERE C.Status<>N''Cancelled''
   AND dbo.GetFeeChargeBalance(C.BillingChargeId)>0
   AND (@EventCode=''OutstandingFees''
     OR (@EventCode=''UpcomingDue'' AND C.DueDate>=@SchoolDate
         AND C.DueDate<=DATEADD(day,7,@SchoolDate))
     OR (@EventCode=''OverdueFollowUp'' AND C.DueDate<@SchoolDate))
   AND (C.AdmissionId=@AdmissionId OR EXISTS(
     SELECT 1 FROM dbo.FamilyMembers SelectedMember
     JOIN dbo.FamilyMembers BilledMember
       ON BilledMember.FamilyId=SelectedMember.FamilyId
     WHERE SelectedMember.AdmissionId=@AdmissionId
       AND BilledMember.AdmissionId=C.AdmissionId))
)');

-- This is an eligibility predicate for reviewed fee suggestions only. The
-- family suffix is stable across the three fee events and excludes another
-- family with a longer identifier (e.g., :F1 cannot match :F11).
-- Previously approved reminders are counted even when a provider later
-- reports Unknown: only a new explicit reviewed batch can record another.
EXEC(N'CREATE OR ALTER FUNCTION msg.FeeReminderEligible
(@AdmissionId int,@EventCode varchar(32),@LocalDate date)
RETURNS TABLE
AS RETURN
(
 SELECT CONVERT(bit,1) Allowed
 FROM msg.AutomationRule R
 OUTER APPLY (SELECT TOP(1) FamilyId FROM dbo.FamilyMembers
              WHERE AdmissionId=@AdmissionId) F
 CROSS APPLY (VALUES(CASE WHEN F.FamilyId IS NULL
  THEN CONCAT('':S'',@AdmissionId) ELSE CONCAT('':F'',F.FamilyId) END)) KeyOf(Suffix)
 CROSS JOIN msg.DispatchControl D
 WHERE R.EventCode=@EventCode AND R.Mode=''Review'' AND D.ControlId=1
   AND (SELECT COUNT_BIG(*) FROM msg.AutomationCandidate C WITH(HOLDLOCK)
         WHERE C.State=''Prepared'' AND C.EventCode IN
            (''OutstandingFees'',''UpcomingDue'',''OverdueFollowUp'')
           AND RIGHT(C.SourceKey,LEN(KeyOf.Suffix))=KeyOf.Suffix
           AND YEAR(CONVERT(date,(C.ReviewedUtc AT TIME ZONE ''UTC'') AT TIME ZONE D.TimeZoneId))=YEAR(@LocalDate)
           AND MONTH(CONVERT(date,(C.ReviewedUtc AT TIME ZONE ''UTC'') AT TIME ZONE D.TimeZoneId))=MONTH(@LocalDate)
       )<R.FeeRemindersPerMonth
   AND NOT EXISTS(SELECT 1 FROM msg.AutomationCandidate C WITH(HOLDLOCK)
       WHERE C.State=''Prepared''
         AND C.EventCode IN (''OutstandingFees'',''UpcomingDue'',''OverdueFollowUp'')
         AND RIGHT(C.SourceKey,LEN(KeyOf.Suffix))=KeyOf.Suffix
         AND C.ReviewedUtc>DATEADD(day,-R.FeeReminderGapDays,SYSUTCDATETIME()))
   AND (SELECT COUNT_BIG(*) FROM msg.AutomationCandidate C WITH(HOLDLOCK)
         WHERE C.State=''Prepared'' AND C.EventCode IN
             (''OutstandingFees'',''UpcomingDue'',''OverdueFollowUp'')
           AND RIGHT(C.SourceKey,LEN(KeyOf.Suffix))=KeyOf.Suffix
           AND CONVERT(date,(C.ReviewedUtc AT TIME ZONE ''UTC'') AT TIME ZONE D.TimeZoneId)=@LocalDate
       )<D.RoutineFamilyDailyLimit
)');

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
     AND R.Mode=''Review''
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
 ORDER BY C.EventCode,C.AdmissionId;
 SELECT @@ROWCOUNT NewSuggestions;
 COMMIT;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadFeeReviewCandidates @UserId int
AS BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 EXEC dbo.RequirePermission @UserId,N''Students.View'';
 EXEC dbo.RequirePermission @UserId,N''Fees.View'';
 DECLARE @Day date=CONVERT(date,
   (SYSUTCDATETIME() AT TIME ZONE ''UTC'') AT TIME ZONE
   (SELECT TimeZoneId FROM msg.DispatchControl WHERE ControlId=1));
 SELECT TOP(200) C.CandidateId,C.EventCode,C.SourceRecordId,C.AdmissionId,
   S.StudentName,S.AdmissionNumber,C.CreatedUtc
 FROM msg.AutomationCandidate C
 JOIN msg.AutomationRule R ON R.EventCode=C.EventCode AND R.Mode=''Review''
 JOIN dbo.Students S ON S.AdmissionId=C.AdmissionId AND S.Status=N''Active''
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
 CROSS APPLY msg.LiveFeeEventEligible(S.AdmissionId,C.EventCode,@Day) Eligible
 CROSS APPLY msg.FeeReminderEligible(S.AdmissionId,C.EventCode,@Day) Policy
 WHERE C.State=''Pending''
   AND C.EventCode IN(''OutstandingFees'',''UpcomingDue'',''OverdueFollowUp'')
   AND C.CreatedUtc>=DATEADD(day,-7,SYSUTCDATETIME())
   AND G.HasConsent=1 AND G.PhoneE164 IS NOT NULL
   AND Placement.CampusId IS NOT NULL
   AND (R.CampusId=0 OR R.CampusId=Placement.CampusId)
   AND (R.ClassId=0 OR R.ClassId=Placement.ClassId)
   AND NOT EXISTS(SELECT 1 FROM msg.AutomationException X
      WHERE (X.FamilyId=Family.FamilyId OR X.AdmissionId=S.AdmissionId)
        AND X.EventCode=C.EventCode AND X.RevokedUtc IS NULL
        AND (X.UntilUtc IS NULL OR X.UntilUtc>SYSUTCDATETIME()))
 ORDER BY C.CreatedUtc,C.CandidateId;
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
 WHERE C.State=''Pending''
   AND C.EventCode NOT IN (''OutstandingFees'',''UpcomingDue'',''OverdueFollowUp'')
   AND G.HasConsent=1 AND G.PhoneE164 IS NOT NULL
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
      AND (@EventCode NOT IN(''OutstandingFees'',''UpcomingDue'',''OverdueFollowUp'')
        OR EXISTS(SELECT 1 FROM msg.LiveFeeEventEligible(@StudentId,@EventCode,@Today)))
      AND (@EventCode<>''PaymentReceived'' OR EXISTS(
           SELECT 1 FROM dbo.FeePayments P
           WHERE P.FeePaymentId=@SourceRecordId AND P.AdmissionId=@StudentId
             AND P.Status=N''Completed'' AND P.RelatedFeePaymentId IS NULL
             AND NOT EXISTS(SELECT 1 FROM dbo.FeePayments Changed
                WHERE Changed.RelatedFeePaymentId=P.FeePaymentId))))
    THROW 51285,''This event is no longer eligible for review. Refresh the suggestions.'',1;
 -- Lock the reviewed fee approval decision across candidates. Daily/monthly
 -- caps must not be bypassed by two staff members approving different events
 -- for the same family at the same time.
 IF @EventCode IN (''OutstandingFees'',''UpcomingDue'',''OverdueFollowUp'')
 BEGIN
   DECLARE @FeeReviewLock int;
   EXEC @FeeReviewLock=sys.sp_getapplock @Resource=N''FMSC_msg_fee_review_approval'',
     @LockMode=''Exclusive'',@LockOwner=''Transaction'',@LockTimeout=10000;
   IF @FeeReviewLock<0 THROW 51289,''Fee reminder review is busy. Retry after refresh.'',1;
   IF NOT EXISTS(SELECT 1 FROM msg.FeeReminderEligible(@StudentId,@EventCode,@Today))
      THROW 51287,''This family recently received a fee reminder or reached a reminder limit. Review the limits and refresh suggestions.'',1;
 END;
 IF NOT EXISTS(SELECT 1 FROM msg.Batch B
  JOIN msg.Recipient Recipient ON Recipient.BatchId=B.BatchId
  WHERE B.BatchId=@BatchId AND B.CreatedBy=@UserId AND B.ConfirmedBy=@UserId
   AND B.State IN (''Confirmed'',''Sending'',''Completed'',''Partial'')
   AND B.StudentId=@StudentId
   AND ((@EventCode IN (''OutstandingFees'',''UpcomingDue'',''OverdueFollowUp'')
          AND B.Audience IN (''FeeStudent'',''FeeFamily''))
      OR (@EventCode NOT IN (''OutstandingFees'',''UpcomingDue'',''OverdueFollowUp'')
          AND B.Audience IN (''Student'',''Family'')))
   AND Recipient.AdmissionId=@StudentId)
    THROW 51284,''Only a confirmed message for the proposed school record can resolve this event.'',1;
 UPDATE msg.AutomationCandidate SET State=''Prepared'',ReviewedBy=@UserId,
  ReviewedUtc=SYSUTCDATETIME(),BatchId=@BatchId WHERE CandidateId=@CandidateId;
 COMMIT;
END');

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
   (@Mode=''Review'' AND @EventCode NOT IN
      (''Admission'',''PaymentReceived'',''Birthday'',''OutstandingFees'',''UpcomingDue'',''OverdueFollowUp''))
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

CREATE TABLE msg.AutomationScanControl (
 ControlId tinyint NOT NULL CONSTRAINT PK_MsgAutomationScanControl PRIMARY KEY,
 LastScanClaimUtc datetime2(3) NOT NULL,
 CONSTRAINT CK_MsgAutomationScanControlId CHECK(ControlId=1)
);
INSERT msg.AutomationScanControl(ControlId,LastScanClaimUtc)
 VALUES(1,CONVERT(datetime2(3),'20000101'));

EXEC(N'CREATE OR ALTER PROCEDURE msg.ClaimAutomationReviewScan @UserId int
AS BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 EXEC dbo.RequirePermission @UserId,N''Students.View'';
 EXEC dbo.RequirePermission @UserId,N''Fees.View'';
 IF NOT EXISTS(SELECT 1 FROM msg.AutomationRule WHERE Mode=''Review'')
 BEGIN
   SELECT CONVERT(bit,0) ShouldScan;
   RETURN;
 END;
 UPDATE msg.AutomationScanControl WITH(UPDLOCK,HOLDLOCK)
 SET LastScanClaimUtc=SYSUTCDATETIME()
 WHERE ControlId=1 AND LastScanClaimUtc<=DATEADD(minute,-30,SYSUTCDATETIME());
 SELECT CONVERT(bit,CASE WHEN @@ROWCOUNT=1 THEN 1 ELSE 0 END) ShouldScan;
END');

-- An administrator may explicitly assume responsibility for a held,
-- previously reviewed API batch. The original creator and confirmation remain
-- immutable. Never store full provider credentials or duplicate recipients.
CREATE TABLE msg.BatchTakeover(
 TakeoverId bigint IDENTITY(1,1) NOT NULL
  CONSTRAINT PK_MsgBatchTakeover PRIMARY KEY,
 BatchId uniqueidentifier NOT NULL
  CONSTRAINT FK_MsgBatchTakeoverBatch REFERENCES msg.Batch(BatchId),
 PriorOwnerId int NOT NULL,
 NewOwnerId int NOT NULL,
 ApprovedBy int NOT NULL,
 ApprovedUtc datetime2(3) NOT NULL
  CONSTRAINT DF_MsgBatchTakeoverUtc DEFAULT SYSUTCDATETIME(),
 Reason nvarchar(200) NOT NULL,
 CONSTRAINT CK_MsgBatchTakeoverReason CHECK(LEN(LTRIM(RTRIM(Reason))) BETWEEN 5 AND 200)
);
CREATE INDEX IX_MsgBatchTakeoverLatest ON msg.BatchTakeover(BatchId,TakeoverId DESC)
 INCLUDE(NewOwnerId,ApprovedBy,ApprovedUtc);

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
 WHERE B.State IN (''Confirmed'',''Sending'')
 GROUP BY B.BatchId,B.CreatedUtc,B.Channel,B.Audience,B.CreatedBy,
    Owner.Username,Takeover.NewOwnerId
 ORDER BY B.CreatedUtc DESC;
END');

-- Routine messages share a conservative family-level daily ceiling across
-- providers and both manual and unattended API dispatch. The count includes
-- unknown attempts because the provider may have charged for them.
-- Transactional acknowledgements are not routine fee/announcement attempts.
EXEC(N'CREATE OR ALTER FUNCTION msg.RoutineFamilyDailyEligible
(@AdmissionId int,@NowUtc datetime2(3))
RETURNS TABLE
AS RETURN
(
 SELECT CONVERT(bit,1) Allowed
 FROM msg.DispatchControl D
 OUTER APPLY (SELECT TOP(1) FamilyId FROM dbo.FamilyMembers
   WHERE AdmissionId=@AdmissionId ORDER BY FamilyId) Family
 WHERE D.ControlId=1 AND @AdmissionId>0
  AND (
    SELECT COUNT_BIG(*) FROM msg.Attempt T WITH(HOLDLOCK)
    JOIN msg.Recipient R WITH(HOLDLOCK) ON R.RecipientId=T.RecipientId
    JOIN msg.Batch B WITH(HOLDLOCK) ON B.BatchId=R.BatchId
    WHERE B.Purpose IN (''Announcement'',''FeeReminder'')
      AND R.AdmissionId IS NOT NULL
      AND T.RequestUtc>DATEADD(day,-2,@NowUtc)
      AND CONVERT(date,(T.RequestUtc AT TIME ZONE ''UTC'') AT TIME ZONE D.TimeZoneId)
          =CONVERT(date,(@NowUtc AT TIME ZONE ''UTC'') AT TIME ZONE D.TimeZoneId)
      AND (CASE WHEN Family.FamilyId IS NULL
          THEN CASE WHEN R.AdmissionId=@AdmissionId THEN 1 ELSE 0 END
          ELSE CASE WHEN EXISTS(SELECT 1 FROM dbo.FamilyMembers F
               WHERE F.FamilyId=Family.FamilyId AND F.AdmissionId=R.AdmissionId)
               THEN 1 ELSE 0 END END)=1
  )<D.RoutineFamilyDailyLimit
)');

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

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadNextScheduledRecipient
 @UserId int,@EnrollmentId uniqueidentifier,@LeaseNonce uniqueidentifier
AS
BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 IF NOT EXISTS(SELECT 1 FROM msg.BackgroundWorkerLease WITH(HOLDLOCK)
    WHERE LeaseId=1 AND EnrollmentId=@EnrollmentId AND LeaseNonce=@LeaseNonce
      AND LeaseUntilUtc>SYSUTCDATETIME())
  THROW 51263,''Background worker lease is not owned by this process.'',1;
 IF NOT EXISTS(SELECT 1 FROM msg.DispatchControl
   WHERE ControlId=1 AND UnattendedEnabled=1 AND MasterPaused=0)
  RETURN;
 DECLARE @Start time(0),@End time(0),@Zone sysname,@Local datetimeoffset;
 SELECT @Start=WindowStart,@End=WindowEnd,@Zone=TimeZoneId
 FROM msg.DispatchControl WHERE ControlId=1;
 SET @Local=(SYSUTCDATETIME() AT TIME ZONE ''UTC'') AT TIME ZONE @Zone;
 IF CONVERT(time(0),@Local)<@Start OR CONVERT(time(0),@Local)>=@End RETURN;
 DECLARE @SchoolDate date=CONVERT(date,@Local);
 DECLARE @Day tinyint=CONVERT(tinyint,((DATEDIFF(day,''19000101'',@SchoolDate)%7)+7)%7+1);
 IF EXISTS(SELECT 1 FROM msg.NonWorkingWeekday WHERE IsoWeekday=@Day AND Enabled=0) RETURN;
 IF EXISTS(SELECT 1 FROM msg.SchoolHoliday WHERE HolidayDate=@SchoolDate) RETURN;
 SELECT TOP(1) R.RecipientId,B.BatchId,B.Channel,B.Audience,
  COALESCE(Takeover.NewOwnerId,B.CreatedBy) CreatedBy,S.Priority
 FROM msg.BatchSchedule S
 JOIN msg.Batch B ON B.BatchId=S.BatchId
 JOIN msg.ApiBatch A ON A.BatchId=B.BatchId
 JOIN msg.Recipient R ON R.BatchId=B.BatchId
 OUTER APPLY (SELECT TOP(1) T.NewOwnerId FROM msg.BatchTakeover T
   WHERE T.BatchId=B.BatchId ORDER BY T.TakeoverId DESC) Takeover
 WHERE S.HoldForReview=0 AND S.DueUtc<=SYSUTCDATETIME()
   AND (S.ExpiresUtc IS NULL OR S.ExpiresUtc>SYSUTCDATETIME())
   AND B.State IN (''Confirmed'',''Sending'') AND R.State=''Pending''
 ORDER BY CASE S.Priority WHEN ''High'' THEN 0 WHEN ''Normal'' THEN 1 ELSE 2 END,
          S.DueUtc,R.RecipientId;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ClaimRecipient
    @UserId int,
    @RecipientId bigint,
    @AttemptKey uniqueidentifier,
    @ExpectedProviderCode varchar(40)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
    IF @RecipientId IS NULL OR @RecipientId<=0
       OR @AttemptKey IS NULL OR @AttemptKey=''00000000-0000-0000-0000-000000000000''
       OR NULLIF(LTRIM(RTRIM(@ExpectedProviderCode)),'''') IS NULL
        THROW 51090,''A valid recipient, attempt key and approved provider are required.'',1;

    BEGIN TRANSACTION;

    -- The global stop and reviewed due date are authoritative at the same
    -- transaction boundary that reserves an external provider attempt.
    IF EXISTS(SELECT 1 FROM msg.DispatchControl WITH(UPDLOCK,HOLDLOCK)
              WHERE ControlId=1 AND MasterPaused=1)
        THROW 51240,''Communication sending is paused by the administrator.'',1;
    IF EXISTS(
        SELECT 1 FROM msg.BatchSchedule S WITH(UPDLOCK,HOLDLOCK)
        JOIN msg.Recipient R WITH(HOLDLOCK) ON R.BatchId=S.BatchId
        WHERE R.RecipientId=@RecipientId AND
             (S.DueUtc>SYSUTCDATETIME()
              OR S.HoldForReview=1
              OR (S.ExpiresUtc IS NOT NULL AND S.ExpiresUtc<=SYSUTCDATETIME()))
    )
        THROW 51241,''This batch is scheduled, expired or awaiting review.'',1;

    DECLARE @BatchId uniqueidentifier,@Kind varchar(8),@AdmissionId int,@StaffId int,
        @SnapshotPhone varchar(16),@RecipientState varchar(12),@BatchState varchar(14),
        @Channel varchar(10),@Purpose varchar(20),@Body nvarchar(1600),
        @Audience varchar(20),@CampusId int,@ClassId int,@StudentId int,@TargetStaffId int,
        @ProviderCode varchar(40),@CurrentPhone varchar(16),@Exclusion varchar(32),
        @AttemptId bigint;

    -- This UPDLOCK serializes competing workers before any attempt is created.
    SELECT @BatchId=R.BatchId,@Kind=R.RecipientKind,@AdmissionId=R.AdmissionId,
        @StaffId=R.StaffId,@SnapshotPhone=R.PhoneE164,@RecipientState=R.State,
        @BatchState=B.State,@Channel=B.Channel,@Purpose=B.Purpose,@Body=B.Body,
        @Audience=B.Audience,@CampusId=B.CampusId,@ClassId=B.ClassId,
        @StudentId=CASE WHEN B.Audience=''OutstandingFees'' AND B.SingleStudentScope=0 THEN 0 ELSE B.StudentId END,@TargetStaffId=B.StaffId
    FROM msg.Recipient R WITH(UPDLOCK,HOLDLOCK)
    JOIN msg.Batch B WITH(HOLDLOCK) ON B.BatchId=R.BatchId
    WHERE R.RecipientId=@RecipientId;
    IF @BatchId IS NULL OR @BatchState NOT IN (''Confirmed'',''Sending'')
       OR @RecipientState<>''Pending''
        THROW 51093,''Recipient is not pending. Never resend an uncertain attempt.'',1;


    -- Scheduled work cannot outlive the originating sender''s authority.
    -- An administrator takeover will need its own explicit audit action.
    IF EXISTS(SELECT 1 FROM msg.BatchSchedule WHERE BatchId=@BatchId)
    BEGIN
        DECLARE @OriginalSenderId int,@StartHour time(0),@EndHour time(0),
                @Zone sysname,@LocalTime datetimeoffset,@LocalDate date,@IsoDay tinyint;
        SELECT @OriginalSenderId=COALESCE(Takeover.NewOwnerId,B.CreatedBy)
        FROM msg.Batch B
        OUTER APPLY (SELECT TOP(1) T.NewOwnerId FROM msg.BatchTakeover T
           WHERE T.BatchId=B.BatchId ORDER BY T.TakeoverId DESC) Takeover
        WHERE B.BatchId=@BatchId;
        EXEC dbo.RequirePermission @OriginalSenderId,N''Communication.Send'';
        IF @Kind=''Staff''
            EXEC dbo.RequirePermission @OriginalSenderId,N''Staff.View'';
        ELSE
            EXEC dbo.RequirePermission @OriginalSenderId,N''Students.View'';
        IF @Audience IN (''OutstandingFees'',''FeeStudent'',''FeeFamily'')
            EXEC dbo.RequirePermission @OriginalSenderId,N''Fees.View'';
        SELECT @StartHour=WindowStart,@EndHour=WindowEnd,@Zone=TimeZoneId
        FROM msg.DispatchControl WITH(HOLDLOCK) WHERE ControlId=1;
        SET @LocalTime=(SYSUTCDATETIME() AT TIME ZONE ''UTC'') AT TIME ZONE @Zone;
        SET @LocalDate=CONVERT(date,@LocalTime);
        SET @IsoDay=CONVERT(tinyint,((DATEDIFF(day,''19000101'',@LocalDate)%7)+7)%7+1);
        IF CONVERT(time(0),@LocalTime)<@StartHour OR CONVERT(time(0),@LocalTime)>=@EndHour
          OR EXISTS(SELECT 1 FROM msg.NonWorkingWeekday WHERE IsoWeekday=@IsoDay AND Enabled=0)
          OR EXISTS(SELECT 1 FROM msg.SchoolHoliday WHERE HolidayDate=@LocalDate)
          THROW 51251,''Scheduled sending is outside approved school hours or working days.'',1;
    END;

    IF @Kind=''Staff''
        EXEC dbo.RequirePermission @UserId,N''Staff.View'';
    ELSE
        EXEC dbo.RequirePermission @UserId,N''Students.View'';
    IF @Audience IN (''OutstandingFees'',''FeeStudent'',''FeeFamily'')
        EXEC dbo.RequirePermission @UserId,N''Fees.View'';

    SELECT @ProviderCode=C.ProviderCode FROM msg.ChannelSettings C WITH(HOLDLOCK)
    WHERE C.Channel=@Channel AND C.Enabled=1;
    IF @ProviderCode IS NULL OR @ProviderCode<>@ExpectedProviderCode
        THROW 51092,''Channel is paused or runtime provider does not match the approved profile.'',1;

    -- Re-read live records while locks protect the verification transaction.
    -- The persisted scope prevents a campus/class/fee/family target from silently
    -- becoming eligible just because an old batch retained a valid phone.
    IF @Kind=''Staff''
    BEGIN
        IF @Audience=''Staff'' AND @TargetStaffId=@StaffId
            SELECT @CurrentPhone=msg.NormalizeMobile(S.ContactNumber)
            FROM dbo.Staff S WITH(HOLDLOCK)
            WHERE S.StaffId=@StaffId AND S.Status=N''Active'';
    END
    ELSE IF @Audience IN (''Student'',''Family'',''FeeStudent'',''FeeFamily'')
    BEGIN
        SELECT @CurrentPhone=G.PhoneE164 FROM msg.RecordGuardian(@StudentId) G
        WHERE G.AdmissionId=@AdmissionId AND G.HasConsent=1;
    END
    ELSE
    BEGIN
        SELECT @CurrentPhone=msg.NormalizeMobile(S.ContactNumber)
        FROM dbo.Students S WITH(HOLDLOCK)
        OUTER APPLY (
            SELECT TOP(1) E.ClassSectionId,E.Status
            FROM dbo.Enrollments E WITH(HOLDLOCK)
            WHERE E.AdmissionId=S.AdmissionId
            ORDER BY CASE WHEN E.Status=N''Active'' THEN 0 ELSE 1 END,E.EnrollmentId DESC
        ) E
        LEFT JOIN dbo.CampusClassSections CCS WITH(HOLDLOCK)
            ON CCS.ClassSectionId=E.ClassSectionId
        OUTER APPLY (
            SELECT SUM(dbo.GetFeeChargeBalance(BC.BillingChargeId)) Outstanding
            FROM dbo.BillingCharges BC WITH(HOLDLOCK)
            WHERE @Audience=''OutstandingFees''
              AND BC.AdmissionId=S.AdmissionId AND BC.Status<>N''Cancelled''
        ) F
        WHERE S.AdmissionId=@AdmissionId AND S.Status=N''Active''
          AND S.SchoolMessageConsent=1
          AND (@Audience=''School''
            OR (@Audience=''Campus'' AND E.Status=N''Active'' AND CCS.CampusId=@CampusId)
            OR (@Audience=''Class'' AND E.Status=N''Active''
                AND CCS.CampusId=@CampusId AND CCS.ClassId=@ClassId)
            OR (@Audience=''OutstandingFees'' AND E.Status=N''Active''
                AND (@CampusId=0 OR CCS.CampusId=@CampusId)
                AND (@ClassId=0 OR CCS.ClassId=@ClassId)
                AND (@StudentId=0 OR S.AdmissionId=@StudentId) AND F.Outstanding>0));
    END;

    SET @Exclusion=CASE
        WHEN @CurrentPhone IS NULL THEN ''NoLongerEligible''
        WHEN @CurrentPhone<>@SnapshotPhone THEN ''DestinationChanged''
        WHEN EXISTS(SELECT 1 FROM msg.Suppression O WITH(HOLDLOCK)
                    WHERE O.Channel=@Channel AND O.PhoneE164=@CurrentPhone)
            THEN ''ChannelOptOut''
        ELSE NULL END;
    IF @Exclusion IS NOT NULL
    BEGIN
        UPDATE msg.Recipient
        SET State=''Excluded'',ExclusionCode=@Exclusion,UpdatedUtc=SYSUTCDATETIME()
        WHERE RecipientId=@RecipientId;
        -- No provider call may occur after an excluded result.
        SELECT CONVERT(bit,0) Claimed,CONVERT(bigint,NULL) AttemptId,
               CONVERT(uniqueidentifier,NULL) AttemptKey,
               CONVERT(varchar(16),NULL) PhoneE164,
               CONVERT(varchar(10),NULL) Channel,
               CONVERT(varchar(40),NULL) ProviderCode,
               CONVERT(nvarchar(1600),NULL) Body,@Exclusion ExclusionCode;
        COMMIT TRANSACTION; RETURN;
    END;

    -- All routine student/family fee and announcement messages respect the
    -- same school-day ceiling, even when different staff or devices reserve
    -- different siblings simultaneously. The local app lock is transaction-
    -- owned; a crash cannot release it before the SQL attempt is persisted.
    IF @Kind=''Student'' AND @Purpose IN (''Announcement'',''FeeReminder'')
    BEGIN
       DECLARE @FamilyForRate int;
       SELECT TOP(1) @FamilyForRate=FM.FamilyId
       FROM dbo.FamilyMembers FM WITH(HOLDLOCK)
       WHERE FM.AdmissionId=@AdmissionId ORDER BY FM.FamilyId;
       DECLARE @RateLock int,@RateResource nvarchar(255)=
          CASE WHEN @FamilyForRate IS NULL
             THEN CONCAT(N''FMSC_msg_daily_student_'',@AdmissionId)
             ELSE CONCAT(N''FMSC_msg_daily_family_'',@FamilyForRate) END;
       EXEC @RateLock=sys.sp_getapplock @Resource=@RateResource,
          @LockMode=''Exclusive'',@LockOwner=''Transaction'',@LockTimeout=10000;
       IF @RateLock<0
          THROW 51293,''Family routine sending is busy. No provider request was created.'',1;
       IF NOT EXISTS(SELECT 1 FROM msg.RoutineFamilyDailyEligible(
           @AdmissionId,SYSUTCDATETIME()))
          THROW 51294,''This family reached the daily routine message limit. Try the next school day after review.'',1;
    END;

    DECLARE @Ordinal int;
    SELECT @Ordinal=COUNT(*)+1 FROM msg.Attempt WITH(HOLDLOCK) WHERE RecipientId=@RecipientId;
    IF @Ordinal>3 OR EXISTS(
        SELECT 1 FROM msg.Attempt WITH(HOLDLOCK)
        WHERE RecipientId=@RecipientId AND (Outcome<>''Failed'' OR RetryApprovedBy IS NULL))
        THROW 51093,''The prior attempt requires definitive failure and explicit retry approval.'',1;

    -- Quota rows serialize every reservation through the same provider.
    -- A reserved attempt consumes capacity even if its result later becomes
    -- Unknown: never reuse that allowance to create a duplicate request.
    DECLARE @Limit int,@Reserve int,@MinuteLimit int,@Priority varchar(10),
            @NowUtc datetime2(3)=SYSUTCDATETIME();
    SELECT @Limit=DailyLimit,@Reserve=ReservedImportant
      FROM msg.ProviderQuota WITH(UPDLOCK,HOLDLOCK)
      WHERE ProviderCode=@ExpectedProviderCode;
    IF @Limit IS NULL AND EXISTS(
        SELECT 1 FROM msg.Recipient R JOIN msg.BatchSchedule S ON S.BatchId=R.BatchId
        WHERE R.RecipientId=@RecipientId)
        THROW 51248,''A scheduled API batch requires a configured daily provider limit.'',1;
    SELECT @MinuteLimit=MaxPerMinute FROM msg.DispatchControl WITH(HOLDLOCK) WHERE ControlId=1;
    SELECT @Priority=COALESCE(S.Priority,''Normal'')
      FROM msg.Recipient R JOIN msg.Batch B ON B.BatchId=R.BatchId
      LEFT JOIN msg.BatchSchedule S ON S.BatchId=B.BatchId
      WHERE R.RecipientId=@RecipientId;
    IF @Limit IS NOT NULL
    BEGIN
    DECLARE @TodayUtc datetime2(3)=CONVERT(datetime2(3),CONVERT(date,@NowUtc));
    DECLARE @Used bigint=(
      SELECT COUNT_BIG(*) FROM msg.Attempt T WITH(HOLDLOCK)
      WHERE T.ProviderCode=@ExpectedProviderCode AND
       T.RequestUtc>=@TodayUtc AND T.RequestUtc<DATEADD(day,1,@TodayUtc));
    IF @Used>=@Limit OR (@Priority<>''High'' AND @Used>=@Limit-@Reserve)
        THROW 51249,''The provider daily allowance is exhausted or reserved for priority messages.'',1;
    IF (SELECT COUNT_BIG(*) FROM msg.Attempt T WITH(HOLDLOCK)
        WHERE T.ProviderCode=@ExpectedProviderCode AND
          T.RequestUtc>DATEADD(minute,-1,@NowUtc)) >= @MinuteLimit
        THROW 51250,''Provider sending speed limit reached. Retry a pending recipient later.'',1;
    END;

    -- Reserve the attempt BEFORE HTTP. Unknown is deliberately conservative:
    -- a crash/timeout must never make this recipient eligible for blind replay.
    INSERT msg.Attempt(RecipientId,AttemptNumber,AttemptKey,Channel,ProviderCode,Outcome)
    VALUES(@RecipientId,@Ordinal,@AttemptKey,@Channel,@ProviderCode,''Unknown'');
    SET @AttemptId=CONVERT(bigint,SCOPE_IDENTITY());
    UPDATE msg.Recipient
    SET State=''Unknown'',UpdatedUtc=SYSUTCDATETIME()
    WHERE RecipientId=@RecipientId;
    UPDATE msg.Batch SET State=''Sending''
    WHERE BatchId=@BatchId AND State=''Confirmed'';
    SELECT CONVERT(bit,1) Claimed,@AttemptId AttemptId,@AttemptKey AttemptKey,
           @CurrentPhone PhoneE164,@Channel Channel,@ProviderCode ProviderCode,
           @Body Body,CONVERT(varchar(32),NULL) ExclusionCode;
    COMMIT TRANSACTION;
END');

INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
VALUES(N'26',SYSDATETIME(),SUSER_SNAME(),
 N'Review-only fee event suggestions with family grouping, current charges and due-date eligibility');
