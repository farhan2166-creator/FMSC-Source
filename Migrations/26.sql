-- Review-only fee suggestion intake. Posted balances and due dates are
-- verified live; the event ledger stores source IDs but never amounts/phones.
SET NOCOUNT ON; SET XACT_ABORT ON;
IF EXISTS(SELECT 1 FROM dbo.DatabaseVersion WHERE TRY_CONVERT(int,VersionNumber)=26) RETURN;
IF ISNULL((SELECT TOP(1) TRY_CONVERT(int,VersionNumber)
  FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0)<>25
 THROW 51280,'Fee suggestion intake requires schema 25.',1;
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

INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
VALUES(N'26',SYSDATETIME(),SUSER_SNAME(),
 N'Review-only fee event suggestions with family grouping, current charges and due-date eligibility');
