-- Explicit event authorization feeds the existing shared preparation/dispatch path.
SET NOCOUNT ON;SET XACT_ABORT ON;
IF EXISTS(SELECT 1 FROM dbo.DatabaseVersion WHERE TRY_CONVERT(int,VersionNumber)=35) RETURN;
IF ISNULL((SELECT TOP(1) TRY_CONVERT(int,VersionNumber) FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0)<>34
 THROW 51000,'Communication event authorization requires the preceding migration.',1;
BEGIN TRY
 BEGIN TRANSACTION;
CREATE TABLE msg.AutomaticAuthorization(
 AuthorizationId uniqueidentifier NOT NULL CONSTRAINT PK_MsgAutomaticAuthorization PRIMARY KEY,
 EventCode varchar(32) NOT NULL CONSTRAINT FK_MsgAutomaticEvent REFERENCES msg.AutomationRule(EventCode),
 TemplateId int NOT NULL CONSTRAINT FK_MsgAutomaticTemplate REFERENCES msg.Template(TemplateId),
 TemplateRevision int NOT NULL,Body nvarchar(1600) NOT NULL,LanguageCode varchar(2) NOT NULL,
 Channel varchar(10) NOT NULL,ProviderCode varchar(40) NOT NULL,ProfileHash varchar(64) NOT NULL,TemplateJson nvarchar(max) NULL,
 CampusId int NOT NULL,ClassId int NOT NULL,DailyFamilyLimit int NOT NULL,ExpiryHours int NOT NULL,Priority varchar(10) NOT NULL,
 ApprovedBy int NOT NULL,ApprovedUtc datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
 RestoreBoundaryHash binary(32) NULL,AcademicToken binary(32) NULL,
 CONSTRAINT CK_MsgAutomaticProvider CHECK((Channel='Sms' AND ProviderCode='Twilio' AND TemplateJson IS NULL)
  OR (Channel='WhatsApp' AND ProviderCode='MetaCloud' AND TemplateJson IS NOT NULL AND ISJSON(TemplateJson)=1)),
 CONSTRAINT CK_MsgAutomaticBounds CHECK(TemplateRevision>0 AND DailyFamilyLimit BETWEEN 1 AND 20 AND ExpiryHours BETWEEN 1 AND 168
  AND LEN(LTRIM(RTRIM(Body)))>0 AND LanguageCode IN('en','ur') AND Priority IN('High','Normal','Low')
  AND CampusId>=0 AND ClassId>=0 AND (ClassId=0 OR CampusId>0)),
 CONSTRAINT CK_MsgAutomaticHash CHECK(LEN(ProfileHash)=64 AND ProfileHash NOT LIKE '%[^0-9A-F]%' COLLATE Latin1_General_100_BIN2)
);
ALTER TABLE msg.AutomationRule ADD AuthorizationId uniqueidentifier NULL
 CONSTRAINT FK_MsgRuleAuthorization REFERENCES msg.AutomaticAuthorization(AuthorizationId);
ALTER TABLE msg.PlannedWork ADD EventDataHash binary(32) NULL,FamilyToken binary(32) NULL,RequestedReviewBy int NULL,RequestedReviewUtc datetime2(3) NULL;
ALTER TABLE msg.PlannedWork ADD CONSTRAINT FK_MsgWorkAuthorization FOREIGN KEY(AuthorizationId) REFERENCES msg.AutomaticAuthorization(AuthorizationId);
ALTER TABLE msg.AutomationCandidate ADD ReviewOnly bit NOT NULL CONSTRAINT DF_MsgCandidateReviewOnly DEFAULT(0);

-- Explicitly approved school-issued notices are the only announcement and
-- examination/result sources. Existing marks sheets are not results records.
-- Record the eligible roster when approved; later admissions cannot silently
-- join an old announcement.
CREATE TABLE msg.SchoolNotice(
 NoticeId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_MsgSchoolNotice PRIMARY KEY,
 ClientRequestId uniqueidentifier NOT NULL CONSTRAINT UQ_MsgSchoolNoticeRequest UNIQUE,
 EventCode varchar(32) NOT NULL CONSTRAINT FK_MsgSchoolNoticeEvent REFERENCES msg.AutomationRule(EventCode),
 Title nvarchar(120) NOT NULL,NoticeText nvarchar(1000) NOT NULL,
 LanguageCode varchar(2) NOT NULL,SourceReference nvarchar(160) NULL,
 CampusId int NOT NULL,ClassId int NOT NULL,ExpiresUtc datetime2(3) NOT NULL,
 SourceHash binary(32) NOT NULL,ApprovedBy int NOT NULL,
 ApprovedUtc datetime2(3) NOT NULL CONSTRAINT DF_MsgSchoolNoticeApproved DEFAULT SYSUTCDATETIME(),
 RevokedBy int NULL,RevokedUtc datetime2(3) NULL,RevocationReason nvarchar(200) NULL,
 CONSTRAINT CK_MsgSchoolNoticeKind CHECK(EventCode IN('Announcement','ExaminationResult')),
 CONSTRAINT CK_MsgSchoolNoticeLanguage CHECK(LanguageCode IN('en','ur')),
 CONSTRAINT CK_MsgSchoolNoticeScope CHECK(CampusId>=0 AND ClassId>=0 AND (ClassId=0 OR CampusId>0)),
 CONSTRAINT CK_MsgSchoolNoticeText CHECK(LEN(LTRIM(RTRIM(Title)))>0 AND LEN(LTRIM(RTRIM(NoticeText)))>0),
 CONSTRAINT CK_MsgSchoolNoticeReference CHECK(EventCode<>'ExaminationResult' OR LEN(LTRIM(RTRIM(ISNULL(SourceReference,N''))))>=5),
 CONSTRAINT CK_MsgSchoolNoticeRevoked CHECK((RevokedUtc IS NULL AND RevokedBy IS NULL AND RevocationReason IS NULL)
  OR (RevokedUtc IS NOT NULL AND RevokedBy IS NOT NULL AND LEN(LTRIM(RTRIM(RevocationReason)))>=8))
);
CREATE TABLE msg.SchoolNoticeMember(
 NoticeId bigint NOT NULL CONSTRAINT FK_MsgNoticeMemberNotice REFERENCES msg.SchoolNotice(NoticeId),
 AdmissionId int NOT NULL CONSTRAINT FK_MsgNoticeMemberStudent REFERENCES dbo.Students(AdmissionId),
 CONSTRAINT PK_MsgSchoolNoticeMember PRIMARY KEY(NoticeId,AdmissionId)
);
CREATE INDEX IX_MsgSchoolNoticeActive ON msg.SchoolNotice(EventCode,ApprovedUtc,ExpiresUtc);

EXEC(N'CREATE OR ALTER PROCEDURE msg.ApproveSchoolNotice
 @UserId int,@ClientRequestId uniqueidentifier,@EventCode varchar(32),@Title nvarchar(120),@NoticeText nvarchar(1000),
 @LanguageCode varchar(2),@SourceReference nvarchar(160)=NULL,@ExpiresUtc datetime2(3),
 @ExpectedCampusId int,@ExpectedClassId int
AS BEGIN
 SET NOCOUNT ON;SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 EXEC dbo.RequirePermission @UserId,N''Students.View'';
 IF NOT EXISTS(SELECT 1 FROM dbo.Roles O WHERE O.RoleName=N''Administrator'' AND
 (EXISTS(SELECT 1 FROM dbo.Users U WHERE U.UserId=@UserId AND U.RoleId=O.RoleId)
 OR EXISTS(SELECT 1 FROM dbo.UserRoles UR WHERE UR.UserId=@UserId AND UR.RoleId=O.RoleId)))
  THROW 51450,''Only an administrator may approve an official school notice.'',1;
 IF @ClientRequestId IS NULL OR @ClientRequestId=''00000000-0000-0000-0000-000000000000''
  OR @EventCode NOT IN(''Announcement'',''ExaminationResult'') OR @LanguageCode NOT IN(''en'',''ur'')
  OR LEN(LTRIM(RTRIM(ISNULL(@Title,N''''))))=0 OR LEN(LTRIM(RTRIM(ISNULL(@NoticeText,N''''))))=0
  OR LEN(@Title)>120 OR LEN(@NoticeText)>1000
  OR CHARINDEX(N''['',ISNULL(@Title,N''''))>0 OR CHARINDEX(N'']'',ISNULL(@Title,N''''))>0
  OR CHARINDEX(N''['',ISNULL(@NoticeText,N''''))>0 OR CHARINDEX(N'']'',ISNULL(@NoticeText,N''''))>0
  OR (@EventCode=''ExaminationResult'' AND LEN(LTRIM(RTRIM(ISNULL(@SourceReference,N''''))))<5)
  OR @ExpiresUtc IS NULL OR @ExpiresUtc<=DATEADD(minute,2,SYSUTCDATETIME()) OR @ExpiresUtc>DATEADD(day,30,SYSUTCDATETIME())
  THROW 51450,''Provide the notice text, approved language, valid expiry and official examination source reference.'',1;
 BEGIN TRANSACTION;
 DECLARE @Campus int,@Class int,@Mode varchar(10);
 SELECT @Campus=CampusId,@Class=ClassId,@Mode=Mode
 FROM msg.AutomationRule WITH(UPDLOCK,HOLDLOCK) WHERE EventCode=@EventCode;
 IF @Campus IS NULL OR @Mode=''Off'' OR @ExpectedCampusId IS NULL OR @ExpectedClassId IS NULL
  OR @Campus<>@ExpectedCampusId OR @Class<>@ExpectedClassId
  THROW 51450,''Enable notice review and refresh the approved audience before publishing.'',1;
 SET @Title=LTRIM(RTRIM(@Title));
 SET @NoticeText=LTRIM(RTRIM(@NoticeText));
 SET @SourceReference=NULLIF(LTRIM(RTRIM(@SourceReference)),N'''');
 DECLARE @Hash binary(32)=CONVERT(binary(32),HASHBYTES(''SHA2_256'',CONCAT(@EventCode,N''|'',@Title,N''|'',@NoticeText,
  N''|'',@LanguageCode,N''|'',ISNULL(@SourceReference,N''''),N''|'',@Campus,N''|'',@Class,N''|'',CONVERT(varchar(33),@ExpiresUtc,126))));
 DECLARE @ExistingId bigint,@ExistingHash binary(32),@ExistingActor int;
 SELECT @ExistingId=NoticeId,@ExistingHash=SourceHash,@ExistingActor=ApprovedBy
 FROM msg.SchoolNotice WITH(UPDLOCK,HOLDLOCK) WHERE ClientRequestId=@ClientRequestId;
 IF @ExistingId IS NOT NULL
 BEGIN
  IF @ExistingHash<>@Hash OR @ExistingActor<>@UserId
   THROW 51450,''The approval request already belongs to another notice. Refresh before approving again.'',1;
  COMMIT;SELECT @ExistingId NoticeId;RETURN;
 END;
 INSERT msg.SchoolNotice(ClientRequestId,EventCode,Title,NoticeText,LanguageCode,SourceReference,CampusId,ClassId,ExpiresUtc,SourceHash,ApprovedBy)
 VALUES(@ClientRequestId,@EventCode,@Title,@NoticeText,@LanguageCode,@SourceReference,
  @Campus,@Class,@ExpiresUtc,@Hash,@UserId);
 DECLARE @NoticeId bigint=CONVERT(bigint,SCOPE_IDENTITY());
 INSERT msg.SchoolNoticeMember(NoticeId,AdmissionId)
 SELECT @NoticeId,S.AdmissionId
 FROM dbo.Students S WITH(HOLDLOCK)
 CROSS APPLY(SELECT TOP(1) E.Status,CCS.CampusId,CCS.ClassId FROM dbo.Enrollments E WITH(HOLDLOCK)
  JOIN dbo.CampusClassSections CCS WITH(HOLDLOCK) ON CCS.ClassSectionId=E.ClassSectionId
  WHERE E.AdmissionId=S.AdmissionId ORDER BY CASE WHEN E.Status=N''Active'' THEN 0 ELSE 1 END,E.EnrollmentId DESC) Placement
 OUTER APPLY msg.RecordGuardian(S.AdmissionId) Guardian
 WHERE S.Status=N''Active'' AND Placement.Status=N''Active'' AND Guardian.HasConsent=1 AND Guardian.PhoneE164 IS NOT NULL
  AND (@Campus=0 OR Placement.CampusId=@Campus) AND (@Class=0 OR Placement.ClassId=@Class);
 IF @@ROWCOUNT=0 THROW 51450,''No consenting active students are in the approved notice audience.'',1;
 COMMIT;
 SELECT @NoticeId NoticeId;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.RevokeSchoolNotice @UserId int,@NoticeId bigint,@Reason nvarchar(200)
AS BEGIN
 SET NOCOUNT ON;SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 IF NOT EXISTS(SELECT 1 FROM dbo.Roles O WHERE O.RoleName=N''Administrator'' AND
  (EXISTS(SELECT 1 FROM dbo.Users U WHERE U.UserId=@UserId AND U.RoleId=O.RoleId)
   OR EXISTS(SELECT 1 FROM dbo.UserRoles UR WHERE UR.UserId=@UserId AND UR.RoleId=O.RoleId)))
  THROW 51451,''Only an administrator can revoke a school notice.'',1;
 IF @NoticeId IS NULL OR @NoticeId<=0 OR LEN(LTRIM(RTRIM(ISNULL(@Reason,N'''')))) NOT BETWEEN 8 AND 200
  THROW 51451,''Explain why the approved notice is being revoked.'',1;
 BEGIN TRANSACTION;
 UPDATE msg.SchoolNotice SET RevokedBy=@UserId,RevokedUtc=SYSUTCDATETIME(),RevocationReason=LTRIM(RTRIM(@Reason))
 WHERE NoticeId=@NoticeId AND RevokedUtc IS NULL;
 IF @@ROWCOUNT<>1 THROW 51451,''This notice is unavailable or already revoked.'',1;
 UPDATE W SET State=''Held'',ReasonCode=''SourceChanged'',UpdatedUtc=SYSUTCDATETIME()
 FROM msg.PlannedWork W JOIN msg.AutomationCandidate C ON C.CandidateId=W.CandidateId
 WHERE C.SourceRecordId=@NoticeId AND C.EventCode IN(''Announcement'',''ExaminationResult'')
  AND W.State IN(''Pending'',''Preparing'');
 UPDATE BS SET HoldForReview=1,UpdatedBy=@UserId,UpdatedUtc=SYSUTCDATETIME()
 FROM msg.BatchSchedule BS JOIN msg.PlannedWork W ON W.BatchId=BS.BatchId
 JOIN msg.AutomationCandidate C ON C.CandidateId=W.CandidateId
 WHERE C.SourceRecordId=@NoticeId AND C.EventCode IN(''Announcement'',''ExaminationResult'');
 COMMIT;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadSchoolNotices @UserId int
AS BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 EXEC dbo.RequirePermission @UserId,N''Students.View'';
 SELECT TOP(200) N.NoticeId,N.EventCode,N.Title,N.NoticeText,N.LanguageCode,N.SourceReference,
 N.CampusId,N.ClassId,N.ExpiresUtc,N.ApprovedBy,N.ApprovedUtc,N.RevokedBy,N.RevokedUtc,N.RevocationReason,
 (SELECT COUNT_BIG(*) FROM msg.SchoolNoticeMember M WHERE M.NoticeId=N.NoticeId) ApprovedAudience
 FROM msg.SchoolNotice N ORDER BY N.ApprovedUtc DESC,N.NoticeId DESC;
END');

EXEC(N'CREATE OR ALTER FUNCTION msg.NoticeFamilyRepresentative(@NoticeId bigint,@AdmissionId int,@EventCode varchar(32))
RETURNS TABLE AS RETURN
(
 SELECT CONVERT(bit,1) Allowed WHERE
 NOT EXISTS(SELECT 1 FROM msg.AutomationCandidate Prior WITH(HOLDLOCK)
  JOIN dbo.FamilyMembers PriorFamily WITH(HOLDLOCK) ON PriorFamily.AdmissionId=Prior.AdmissionId
  JOIN dbo.FamilyMembers CurrentFamily WITH(HOLDLOCK) ON CurrentFamily.AdmissionId=@AdmissionId
    AND CurrentFamily.FamilyId=PriorFamily.FamilyId
  WHERE Prior.EventCode=@EventCode AND Prior.SourceRecordId=@NoticeId AND Prior.State=''Prepared''
    AND Prior.AdmissionId<>@AdmissionId)
 AND NOT EXISTS(
  SELECT 1 FROM dbo.FamilyMembers CurrentFamily WITH(HOLDLOCK)
  JOIN dbo.FamilyMembers EarlierFamily WITH(HOLDLOCK) ON EarlierFamily.FamilyId=CurrentFamily.FamilyId
   AND EarlierFamily.AdmissionId<@AdmissionId
  JOIN msg.SchoolNoticeMember MemberOfRoster WITH(HOLDLOCK) ON MemberOfRoster.NoticeId=@NoticeId
   AND MemberOfRoster.AdmissionId=EarlierFamily.AdmissionId
  JOIN msg.SchoolNotice N WITH(HOLDLOCK) ON N.NoticeId=@NoticeId AND N.EventCode=@EventCode
  JOIN dbo.Students Peer WITH(HOLDLOCK) ON Peer.AdmissionId=EarlierFamily.AdmissionId AND Peer.Status=N''Active''
  CROSS APPLY msg.AutomationRecordInScope(EarlierFamily.AdmissionId,@EventCode) PeerScope
  OUTER APPLY msg.RecordGuardian(EarlierFamily.AdmissionId) Contact
  WHERE CurrentFamily.AdmissionId=@AdmissionId
   AND Contact.HasConsent=1 AND Contact.PhoneE164 IS NOT NULL
   AND EXISTS(SELECT 1 FROM dbo.Enrollments E WITH(HOLDLOCK)
     JOIN dbo.CampusClassSections CCS WITH(HOLDLOCK) ON CCS.ClassSectionId=E.ClassSectionId
     WHERE E.AdmissionId=EarlierFamily.AdmissionId AND E.Status=N''Active''
       AND (N.CampusId=0 OR CCS.CampusId=N.CampusId)
       AND (N.ClassId=0 OR CCS.ClassId=N.ClassId))
   AND NOT EXISTS(SELECT 1 FROM msg.AutomationException X WITH(HOLDLOCK)
     WHERE X.EventCode=@EventCode AND
       (X.AdmissionId=EarlierFamily.AdmissionId OR X.FamilyId=CurrentFamily.FamilyId)
       AND X.RevokedUtc IS NULL AND (X.UntilUtc IS NULL OR X.UntilUtc>SYSUTCDATETIME()))
 )
);');

EXEC(N'CREATE OR ALTER FUNCTION msg.NoticeCandidateEligible(@CandidateId bigint)
RETURNS TABLE AS RETURN
(
 SELECT C.CandidateId FROM msg.AutomationCandidate C WITH(HOLDLOCK)
 JOIN msg.SchoolNotice N WITH(HOLDLOCK) ON N.NoticeId=C.SourceRecordId AND N.EventCode=C.EventCode
 JOIN msg.SchoolNoticeMember Member WITH(HOLDLOCK) ON Member.NoticeId=N.NoticeId AND Member.AdmissionId=C.AdmissionId
 JOIN msg.AutomationRule R WITH(HOLDLOCK) ON R.EventCode=N.EventCode AND (R.Mode IN(''Review'',''Automatic'') OR C.ReviewOnly=1)
 JOIN dbo.Students S WITH(HOLDLOCK) ON S.AdmissionId=C.AdmissionId AND S.Status=N''Active''
 CROSS APPLY(SELECT TOP(1) E.Status,CCS.CampusId,CCS.ClassId FROM dbo.Enrollments E WITH(HOLDLOCK)
  JOIN dbo.CampusClassSections CCS WITH(HOLDLOCK) ON CCS.ClassSectionId=E.ClassSectionId
  WHERE E.AdmissionId=C.AdmissionId ORDER BY CASE WHEN E.Status=N''Active'' THEN 0 ELSE 1 END,E.EnrollmentId DESC) Placement
 CROSS APPLY msg.AutomationRecordInScope(C.AdmissionId,C.EventCode) RuleScope
 CROSS APPLY msg.NoticeFamilyRepresentative(N.NoticeId,C.AdmissionId,C.EventCode) FamilyRepresentative
 OUTER APPLY msg.RecordGuardian(C.AdmissionId) Guardian
 LEFT JOIN dbo.FamilyMembers FM WITH(HOLDLOCK) ON FM.AdmissionId=C.AdmissionId
 WHERE C.CandidateId=@CandidateId AND C.State IN(''Pending'',''Prepared'')
  AND N.RevokedUtc IS NULL AND N.ExpiresUtc>SYSUTCDATETIME()
  AND (R.ActivationBaselineUtc IS NULL OR N.ApprovedUtc>=R.ActivationBaselineUtc)
  AND Placement.Status=N''Active'' AND (N.CampusId=0 OR Placement.CampusId=N.CampusId)
  AND (N.ClassId=0 OR Placement.ClassId=N.ClassId)
  AND Guardian.HasConsent=1 AND Guardian.PhoneE164 IS NOT NULL
  AND (R.Mode<>''Automatic'' OR C.ReviewOnly=1 OR EXISTS(SELECT 1 FROM msg.AutomaticAuthorization A
    WHERE A.AuthorizationId=R.AuthorizationId AND A.LanguageCode=N.LanguageCode))
  AND NOT EXISTS(SELECT 1 FROM msg.AutomationException X WITH(HOLDLOCK)
    WHERE X.EventCode=C.EventCode AND (X.AdmissionId=C.AdmissionId OR X.FamilyId=FM.FamilyId)
    AND X.RevokedUtc IS NULL AND (X.UntilUtc IS NULL OR X.UntilUtc>SYSUTCDATETIME()))
);');

EXEC(N'CREATE OR ALTER TRIGGER msg.TR_SchoolNoticeImmutable ON msg.SchoolNotice AFTER UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM deleted D LEFT JOIN inserted I ON I.NoticeId=D.NoticeId WHERE I.NoticeId IS NULL)
  THROW 51452,''Approved school notices cannot be deleted.'',1;
 IF EXISTS(SELECT 1 FROM deleted D JOIN inserted I ON I.NoticeId=D.NoticeId
  WHERE I.ClientRequestId<>D.ClientRequestId OR I.EventCode<>D.EventCode OR I.Title<>D.Title OR I.NoticeText<>D.NoticeText
   OR I.LanguageCode<>D.LanguageCode OR ISNULL(I.SourceReference,N'''')<>ISNULL(D.SourceReference,N'''')
   OR I.CampusId<>D.CampusId OR I.ClassId<>D.ClassId OR I.ExpiresUtc<>D.ExpiresUtc
   OR I.SourceHash<>D.SourceHash OR I.ApprovedBy<>D.ApprovedBy OR I.ApprovedUtc<>D.ApprovedUtc
   OR D.RevokedUtc IS NOT NULL
   OR (I.RevokedUtc IS NULL AND (I.RevokedBy IS NOT NULL OR I.RevocationReason IS NOT NULL))
   OR (I.RevokedUtc IS NOT NULL AND (I.RevokedBy IS NULL OR LEN(LTRIM(RTRIM(I.RevocationReason)))<8)))
  THROW 51452,''The approved source and revocation record are immutable.'',1;
END');

EXEC(N'CREATE OR ALTER TRIGGER msg.TR_SchoolNoticeMemberImmutable ON msg.SchoolNoticeMember AFTER UPDATE,DELETE AS
BEGIN SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM deleted)
  THROW 51452,''The approved notice roster is immutable.'',1;
END');

EXEC(N'CREATE OR ALTER FUNCTION msg.HasSchoolPermission(@UserId int,@Permission nvarchar(100))
RETURNS TABLE AS RETURN
(
 SELECT CONVERT(bit,1) Allowed WHERE EXISTS(SELECT 1 FROM dbo.Users U WHERE U.UserId=@UserId) AND
 (EXISTS(SELECT 1 FROM dbo.Roles O WHERE O.RoleName=N''Administrator'' AND
   (EXISTS(SELECT 1 FROM dbo.Users U WHERE U.UserId=@UserId AND U.RoleId=O.RoleId)
    OR EXISTS(SELECT 1 FROM dbo.UserRoles UR WHERE UR.UserId=@UserId AND UR.RoleId=O.RoleId)))
 OR EXISTS(SELECT 1 FROM dbo.RolePermissions RP JOIN dbo.Permissions P ON P.PermissionId=RP.PermissionId
   WHERE P.PermissionCode=@Permission AND (EXISTS(SELECT 1 FROM dbo.Users U WHERE U.UserId=@UserId AND U.RoleId=RP.RoleId)
    OR EXISTS(SELECT 1 FROM dbo.UserRoles UR WHERE UR.UserId=@UserId AND UR.RoleId=RP.RoleId))))
);');

EXEC(N'CREATE OR ALTER FUNCTION msg.FamilyBoundary(@AdmissionId int)
RETURNS binary(32)
AS BEGIN
 DECLARE @Token binary(32);
 SELECT @Token=CONVERT(binary(32),HASHBYTES(''SHA2_256'',CONCAT(S.AdmissionId,'':'',ISNULL(FM.FamilyId,0),'':'',S.Status,'':'',Members.Token)))
 FROM dbo.Students S WITH(HOLDLOCK) LEFT JOIN dbo.FamilyMembers FM WITH(HOLDLOCK) ON FM.AdmissionId=S.AdmissionId
 OUTER APPLY(SELECT STRING_AGG(CONVERT(nvarchar(max),CONCAT(F.AdmissionId,'':'',Child.Status)),''|'') WITHIN GROUP(ORDER BY F.AdmissionId) Token
  FROM dbo.FamilyMembers F WITH(HOLDLOCK) JOIN dbo.Students Child WITH(HOLDLOCK) ON Child.AdmissionId=F.AdmissionId
  WHERE F.FamilyId=FM.FamilyId) Members WHERE S.AdmissionId=@AdmissionId;
 RETURN @Token;
END;');

EXEC(N'CREATE OR ALTER FUNCTION msg.PaymentEventSource(@PaymentId bigint)
RETURNS TABLE AS RETURN
(
 SELECT P.FeePaymentId,P.AdmissionId,P.TotalAmount,P.IsCreditApplication,
  COALESCE(R.ReceiptNumber,P.ReceiptNumber) ReceiptNumber,
  CONVERT(binary(32),HASHBYTES(''SHA2_256'',CONCAT(P.FeePaymentId,'':'',P.AdmissionId,'':'',P.TotalAmount,'':'',CONVERT(varchar(33),P.PaymentDate,126),'':'',
    P.PaymentMethod,'':'',P.Status,'':'',P.IsCreditApplication,'':'',COALESCE(R.ReceiptNumber,P.ReceiptNumber)))) SourceHash
 FROM dbo.FeePayments P WITH(HOLDLOCK)
 OUTER APPLY(SELECT TOP(1) C.ReceiptNumber FROM dbo.CollectionReceiptPayments L WITH(HOLDLOCK)
   JOIN dbo.CollectionReceipts C WITH(HOLDLOCK) ON C.CollectionReceiptId=L.CollectionReceiptId
   WHERE L.FeePaymentId=P.FeePaymentId ORDER BY C.VisitSequence DESC) R
 WHERE P.FeePaymentId=@PaymentId AND P.Status=N''Completed'' AND P.TotalAmount>0 AND P.RelatedFeePaymentId IS NULL
 AND NOT EXISTS(SELECT 1 FROM dbo.FeePayments A WITH(HOLDLOCK) WHERE A.RelatedFeePaymentId=P.FeePaymentId)
);');

EXEC(N'CREATE OR ALTER FUNCTION msg.AutomaticAuthorized(@AuthorizationId uniqueidentifier)
RETURNS TABLE AS RETURN
(
 SELECT A.AuthorizationId,A.EventCode FROM msg.AutomaticAuthorization A WITH(HOLDLOCK)
 JOIN msg.AutomationRule R WITH(HOLDLOCK) ON R.EventCode=A.EventCode AND R.AuthorizationId=A.AuthorizationId AND R.Mode=''Automatic''
 JOIN msg.Template T WITH(HOLDLOCK) ON T.TemplateId=A.TemplateId AND T.Active=1 AND T.LanguageCode=A.LanguageCode AND T.Channel=A.Channel
 JOIN msg.ChannelSettings C WITH(HOLDLOCK) ON C.Channel=A.Channel AND C.ProviderCode=A.ProviderCode AND C.Enabled=1
 WHERE A.AuthorizationId=@AuthorizationId AND A.CampusId=R.CampusId AND A.ClassId=R.ClassId
 AND EXISTS(SELECT 1 FROM dbo.Roles O WHERE O.RoleName=N''Administrator'' AND
  (EXISTS(SELECT 1 FROM dbo.Users U WHERE U.UserId=A.ApprovedBy AND U.RoleId=O.RoleId)
   OR EXISTS(SELECT 1 FROM dbo.UserRoles UR WHERE UR.UserId=A.ApprovedBy AND UR.RoleId=O.RoleId)))
 AND A.TemplateRevision=(SELECT MAX(V.RevisionNumber) FROM msg.TemplateRevision V WITH(HOLDLOCK) WHERE V.TemplateId=A.TemplateId)
 AND ((A.RestoreBoundaryHash=msg.RestoreBoundary()) OR (A.RestoreBoundaryHash IS NULL AND msg.RestoreBoundary() IS NULL))
 AND (A.ClassId=0 OR A.AcademicToken=msg.AcademicSessionToken())
);');

EXEC(N'CREATE OR ALTER FUNCTION msg.EventSourceEligible(@CandidateId bigint)
RETURNS TABLE AS RETURN
(
 SELECT C.CandidateId FROM msg.AutomationCandidate C WITH(HOLDLOCK)
 JOIN msg.AutomationRule R WITH(HOLDLOCK) ON R.EventCode=C.EventCode AND (R.Mode IN(''Review'',''Automatic'') OR C.ReviewOnly=1)
 JOIN dbo.Students S WITH(HOLDLOCK) ON S.AdmissionId=C.AdmissionId AND S.Status=N''Active''
 CROSS APPLY msg.AutomationRecordInScope(C.AdmissionId,C.EventCode) Scope
 OUTER APPLY msg.RecordGuardian(C.AdmissionId) Guardian
 LEFT JOIN dbo.FamilyMembers FM WITH(HOLDLOCK) ON FM.AdmissionId=C.AdmissionId
 CROSS APPLY(SELECT CONVERT(date,SYSUTCDATETIME() AT TIME ZONE ''UTC'' AT TIME ZONE D.TimeZoneId) SchoolDate
   FROM msg.DispatchControl D WHERE D.ControlId=1) Today
 WHERE C.CandidateId=@CandidateId AND C.State IN(''Pending'',''Prepared'') AND Guardian.HasConsent=1 AND Guardian.PhoneE164 IS NOT NULL
 AND NOT EXISTS(SELECT 1 FROM msg.AutomationException X WITH(HOLDLOCK) WHERE X.EventCode=C.EventCode
   AND (X.AdmissionId=C.AdmissionId OR X.FamilyId=FM.FamilyId) AND X.RevokedUtc IS NULL AND (X.UntilUtc IS NULL OR X.UntilUtc>SYSUTCDATETIME()))
 AND ((C.EventCode=''Admission'' AND S.AdmissionDate>=DATEADD(day,-7,Today.SchoolDate) AND S.AdmissionDate<=Today.SchoolDate)
 OR (C.EventCode=''PaymentReceived'' AND EXISTS(SELECT 1 FROM msg.PaymentEventSource(C.SourceRecordId) P
    JOIN dbo.FeePayments SourcePayment ON SourcePayment.FeePaymentId=P.FeePaymentId
    WHERE P.AdmissionId=C.AdmissionId AND SourcePayment.PaymentDate>=DATEADD(day,-2,Today.SchoolDate) AND SourcePayment.PaymentDate<DATEADD(day,1,Today.SchoolDate)))
 OR (C.EventCode=''Birthday'' AND C.SourceKey=CONCAT(''BD:'',YEAR(Today.SchoolDate),'':'',C.AdmissionId)
    AND MONTH(S.DateOfBirth)=MONTH(Today.SchoolDate) AND DAY(S.DateOfBirth)=DAY(Today.SchoolDate))
 OR (C.EventCode IN(''OutstandingFees'',''UpcomingDue'',''OverdueFollowUp'') AND EXISTS(SELECT 1 FROM msg.LiveFeeEventEligible(C.AdmissionId,C.EventCode,Today.SchoolDate)))
 OR (C.EventCode IN(''Announcement'',''ExaminationResult'') AND EXISTS(SELECT 1 FROM msg.NoticeCandidateEligible(C.CandidateId))))
);');

EXEC(N'CREATE OR ALTER PROCEDURE msg.SaveAutomaticAuthorization
 @UserId int,@AuthorizationId uniqueidentifier,@EventCode varchar(32),@TemplateId int,@LanguageCode varchar(2),
 @Channel varchar(10),@ProviderCode varchar(40),@ProfileHash varchar(64),@Body nvarchar(1600),@TemplateJson nvarchar(max)=NULL,
 @DailyFamilyLimit int,@ExpiryHours int,@PaidApproval bit,@ExpectedCampusId int,@ExpectedClassId int
AS BEGIN
 SET NOCOUNT ON;SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 EXEC dbo.RequirePermission @UserId,N''Students.View'';EXEC dbo.RequirePermission @UserId,N''Fees.View'';
 IF NOT EXISTS(SELECT 1 FROM dbo.Roles O WHERE O.RoleName=N''Administrator'' AND
  (EXISTS(SELECT 1 FROM dbo.Users U WHERE U.UserId=@UserId AND U.RoleId=O.RoleId)
   OR EXISTS(SELECT 1 FROM dbo.UserRoles UR WHERE UR.UserId=@UserId AND UR.RoleId=O.RoleId)))
  THROW 51430,''An active administrator must authorize automatic provider charges.'',1;
 IF @PaidApproval IS NULL OR @PaidApproval<>1 OR @AuthorizationId IS NULL OR @AuthorizationId=''00000000-0000-0000-0000-000000000000''
 OR @EventCode IS NULL OR @EventCode NOT IN(''Admission'',''PaymentReceived'',''Birthday'',''OutstandingFees'',''UpcomingDue'',''OverdueFollowUp'',''Announcement'',''ExaminationResult'')
 OR @DailyFamilyLimit IS NULL OR @DailyFamilyLimit NOT BETWEEN 1 AND 20 OR @ExpiryHours IS NULL OR @ExpiryHours NOT BETWEEN 1 AND 168
 OR @Body IS NULL OR LEN(LTRIM(RTRIM(@Body)))=0 OR LEN(@Body)>1600
 OR (@EventCode IN(''Announcement'',''ExaminationResult'')
 AND (CHARINDEX(N''[NoticeTitle]'',@Body)=0 OR CHARINDEX(N''[NoticeText]'',@Body)=0))
 OR @ProfileHash IS NULL OR LEN(@ProfileHash)<>64 OR @ProfileHash LIKE ''%[^0-9A-F]%'' COLLATE Latin1_General_100_BIN2
  THROW 51430,''Explicit paid approval and a complete supported event/template are required.'',1;
 BEGIN TRANSACTION;
 DECLARE @Campus int,@Class int,@Revision int,@SchoolBody nvarchar(1600),@Purpose varchar(20);
 SELECT @Campus=CampusId,@Class=ClassId FROM msg.AutomationRule WITH(UPDLOCK,HOLDLOCK) WHERE EventCode=@EventCode;
 SELECT @Revision=MAX(RevisionNumber) FROM msg.TemplateRevision WITH(HOLDLOCK) WHERE TemplateId=@TemplateId;
 SELECT @SchoolBody=Body,@Purpose=Purpose FROM msg.Template WITH(HOLDLOCK)
 WHERE TemplateId=@TemplateId AND Active=1 AND Channel=@Channel AND LanguageCode=@LanguageCode;
 IF @Campus IS NULL OR @ExpectedCampusId IS NULL OR @ExpectedClassId IS NULL OR @Campus<>@ExpectedCampusId OR @Class<>@ExpectedClassId OR @Revision IS NULL OR @SchoolBody IS NULL OR @LanguageCode NOT IN(''en'',''ur'')
 OR (@EventCode IN(''OutstandingFees'',''UpcomingDue'',''OverdueFollowUp'') AND @Purpose<>''FeeReminder'')
 OR (@EventCode NOT IN(''OutstandingFees'',''UpcomingDue'',''OverdueFollowUp'') AND @Purpose NOT IN(''Individual'',''FeeReminder''))
 OR NOT EXISTS(SELECT 1 FROM msg.ChannelSettings WITH(HOLDLOCK) WHERE Channel=@Channel AND ProviderCode=@ProviderCode AND Enabled=1)
 OR NOT EXISTS(SELECT 1 FROM msg.ProviderQuota WITH(HOLDLOCK) WHERE Channel=@Channel AND ProviderCode=@ProviderCode AND DailyLimit>0)
 OR NOT EXISTS(SELECT 1 FROM msg.DispatchControl WHERE ControlId=1 AND UnattendedEnabled=1)
  THROW 51430,''Choose an active compatible template, configured API allowance and enrolled background processor.'',1;
 IF NOT((@Channel=''Sms'' AND @ProviderCode=''Twilio'' AND @TemplateJson IS NULL AND @Body COLLATE Latin1_General_100_BIN2=@SchoolBody COLLATE Latin1_General_100_BIN2)
 OR (@Channel=''WhatsApp'' AND @ProviderCode=''MetaCloud'' AND ISNULL(ISJSON(@TemplateJson),0)=1))
  THROW 51430,''Only an approved API method and reviewed template can be authorized.'',1;
 IF @Class>0 AND msg.AcademicSessionToken() IS NULL THROW 51430,''A class event needs the current academic year.'',1;
 IF EXISTS(SELECT 1 FROM msg.AutomaticAuthorization WHERE AuthorizationId=@AuthorizationId)
 BEGIN
  IF NOT EXISTS(SELECT 1 FROM msg.AutomaticAuthorization A JOIN msg.AutomationRule R ON R.AuthorizationId=A.AuthorizationId
   WHERE A.AuthorizationId=@AuthorizationId AND R.Mode=''Automatic'' AND A.EventCode=@EventCode AND A.TemplateId=@TemplateId
   AND A.TemplateRevision=@Revision AND A.Body COLLATE Latin1_General_100_BIN2=@Body COLLATE Latin1_General_100_BIN2 AND A.Channel=@Channel
   AND A.ProviderCode=@ProviderCode AND A.ProfileHash=@ProfileHash AND ISNULL(A.TemplateJson,N'''')=ISNULL(@TemplateJson,N'''')
   AND A.DailyFamilyLimit=@DailyFamilyLimit AND A.ExpiryHours=@ExpiryHours AND A.LanguageCode=@LanguageCode)
    THROW 51430,''This approval already belongs to other settings. Review a new authorization.'',1;
  COMMIT;RETURN;
 END;
 INSERT msg.AutomaticAuthorization(AuthorizationId,EventCode,TemplateId,TemplateRevision,Body,LanguageCode,Channel,ProviderCode,ProfileHash,TemplateJson,
 CampusId,ClassId,DailyFamilyLimit,ExpiryHours,Priority,ApprovedBy,RestoreBoundaryHash,AcademicToken)
 VALUES(@AuthorizationId,@EventCode,@TemplateId,@Revision,@Body,@LanguageCode,@Channel,@ProviderCode,@ProfileHash,@TemplateJson,
 @Campus,@Class,@DailyFamilyLimit,@ExpiryHours,CASE WHEN @EventCode=''PaymentReceived'' THEN ''High'' ELSE ''Normal'' END,@UserId,
 msg.RestoreBoundary(),CASE WHEN @Class>0 THEN msg.AcademicSessionToken() END);
 UPDATE msg.AutomationRule SET AuthorizationId=@AuthorizationId,Mode=''Automatic'',ActivationBaselineUtc=SYSUTCDATETIME(),
 BaselineAdmissionId=ISNULL((SELECT MAX(AdmissionId) FROM dbo.Students),0),BaselinePaymentId=ISNULL((SELECT MAX(FeePaymentId) FROM dbo.FeePayments),0),
 UpdatedBy=@UserId,UpdatedUtc=SYSUTCDATETIME() WHERE EventCode=@EventCode;
 UPDATE W SET State=''Held'',ReasonCode=''AuthorizationChanged'',UpdatedUtc=SYSUTCDATETIME() FROM msg.PlannedWork W
 JOIN msg.AutomationCandidate C ON C.CandidateId=W.CandidateId WHERE C.EventCode=@EventCode AND W.State IN(''Pending'',''Preparing'');
 UPDATE S SET HoldForReview=1,UpdatedBy=@UserId,UpdatedUtc=SYSUTCDATETIME() FROM msg.BatchSchedule S
 JOIN msg.PlannedWork W ON W.BatchId=S.BatchId JOIN msg.AutomationCandidate C ON C.CandidateId=W.CandidateId WHERE C.EventCode=@EventCode;
 COMMIT;
END;');

EXEC(N'CREATE OR ALTER PROCEDURE msg.CreateAutomaticWork
 @UserId int,@EnrollmentId uniqueidentifier,@WindowsSid nvarchar(185),@SecretHash varbinary(32),@LeaseNonce uniqueidentifier
AS BEGIN
 SET NOCOUNT ON;SET XACT_ABORT ON;
 BEGIN TRANSACTION;
 EXEC msg.VerifyPlanWorker @UserId,@EnrollmentId,@WindowsSid,@SecretHash,@LeaseNonce;
 DECLARE @Lock int;
 EXEC @Lock=sys.sp_getapplock @Resource=N''FMSC_msg_automation_intake'',@LockMode=''Exclusive'',@LockOwner=''Transaction'',@LockTimeout=10000;
 IF @Lock<0 THROW 51431,''Automatic preparation is busy.'',1;
 UPDATE S SET HoldForReview=1,UpdatedUtc=SYSUTCDATETIME() FROM msg.BatchSchedule S
 JOIN msg.PlannedWork W ON W.BatchId=S.BatchId WHERE W.SourceKind=''Event'' AND NOT EXISTS(SELECT 1 FROM msg.AutomaticAuthorized(W.AuthorizationId));
 UPDATE W SET State=''Held'',ReasonCode=''AuthorizationChanged'',UpdatedUtc=SYSUTCDATETIME()
 FROM msg.PlannedWork W WHERE W.SourceKind=''Event'' AND W.State IN(''Pending'',''Preparing'') AND NOT EXISTS(SELECT 1 FROM msg.AutomaticAuthorized(W.AuthorizationId));
 UPDATE W SET State=''Held'',ReasonCode=''SourceChanged'',UpdatedUtc=SYSUTCDATETIME()
 FROM msg.PlannedWork W WHERE W.SourceKind=''Event'' AND W.State IN(''Pending'',''Preparing'')
  AND NOT EXISTS(SELECT 1 FROM msg.EventSourceEligible(W.CandidateId));
 INSERT msg.PlannedWork(WorkId,SourceKind,CandidateId,AuthorizationId,DueUtc,ExpiresUtc,State,ReasonCode)
 SELECT TOP(100) NEWID(),''Event'',C.CandidateId,A.AuthorizationId,SYSUTCDATETIME(),DATEADD(hour,A.ExpiryHours,SYSUTCDATETIME()),
 CASE WHEN EXISTS(SELECT 1 FROM msg.AutomaticAuthorized(A.AuthorizationId)) THEN ''Pending'' ELSE ''Held'' END,
 CASE WHEN NOT EXISTS(SELECT 1 FROM msg.AutomaticAuthorized(A.AuthorizationId)) THEN ''AuthorizationChanged'' END
 FROM msg.AutomationCandidate C WITH(UPDLOCK,HOLDLOCK) JOIN msg.AutomationRule R ON R.EventCode=C.EventCode AND R.Mode=''Automatic''
 JOIN msg.AutomaticAuthorization A ON A.AuthorizationId=R.AuthorizationId
 CROSS APPLY msg.EventSourceEligible(C.CandidateId) Source
 WHERE C.State=''Pending'' AND C.ReviewOnly=0
 AND (C.EventCode NOT IN(''Admission'',''PaymentReceived'',''Announcement'',''ExaminationResult'') OR C.CreatedUtc>=A.ApprovedUtc)
 AND NOT EXISTS(SELECT 1 FROM msg.PlannedWork W WITH(UPDLOCK,HOLDLOCK) WHERE W.CandidateId=C.CandidateId)
 ORDER BY C.CreatedUtc,C.CandidateId;
 COMMIT;
END;');

EXEC(N'CREATE OR ALTER FUNCTION msg.EventDailyAllowed(@AdmissionId int,@AuthorizationId uniqueidentifier)
RETURNS TABLE AS RETURN
(
 SELECT CONVERT(bit,1) Allowed FROM msg.AutomaticAuthorization A CROSS JOIN msg.DispatchControl D
 WHERE A.AuthorizationId=@AuthorizationId AND D.ControlId=1 AND
 (SELECT COUNT_BIG(*) FROM msg.Attempt T WITH(HOLDLOCK) JOIN msg.Recipient P ON P.RecipientId=T.RecipientId
 JOIN msg.PlannedWork W ON W.BatchId=P.BatchId JOIN msg.AutomaticAuthorization Prior ON Prior.AuthorizationId=W.AuthorizationId
 WHERE Prior.EventCode=A.EventCode AND
 (P.AdmissionId=@AdmissionId OR EXISTS(SELECT 1 FROM dbo.FamilyMembers F JOIN dbo.FamilyMembers S ON S.FamilyId=F.FamilyId
   WHERE F.AdmissionId=@AdmissionId AND S.AdmissionId=P.AdmissionId))
 AND CONVERT(date,T.RequestUtc AT TIME ZONE ''UTC'' AT TIME ZONE D.TimeZoneId)=CONVERT(date,SYSUTCDATETIME() AT TIME ZONE ''UTC'' AT TIME ZONE D.TimeZoneId))<A.DailyFamilyLimit
);');

EXEC(N'CREATE OR ALTER PROCEDURE msg.OfferEventReview @UserId int,@CandidateId bigint
AS BEGIN
 SET NOCOUNT ON;SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 EXEC dbo.RequirePermission @UserId,N''Students.View'';EXEC dbo.RequirePermission @UserId,N''Fees.View'';
 BEGIN TRANSACTION;
 IF NOT EXISTS(SELECT 1 FROM msg.AutomationCandidate C WITH(UPDLOCK,HOLDLOCK)
   WHERE C.CandidateId=@CandidateId AND C.State=''Pending'') THROW 51431,''The event is already prepared or no longer available.'',1;
 IF EXISTS(SELECT 1 FROM msg.PlannedWork WHERE CandidateId=@CandidateId AND BatchId IS NOT NULL)
  THROW 51431,''Review the existing prepared batch rather than preparing it twice.'',1;
 UPDATE msg.AutomationCandidate SET ReviewOnly=1 WHERE CandidateId=@CandidateId;
 UPDATE msg.PlannedWork SET State=''Resolved'',ReasonCode=''ManualReview'',RequestedReviewBy=@UserId,RequestedReviewUtc=SYSUTCDATETIME(),UpdatedUtc=SYSUTCDATETIME()
 WHERE CandidateId=@CandidateId AND State IN(''Pending'',''Preparing'',''Held'');
 COMMIT;
END;');

EXEC(N'CREATE OR ALTER PROCEDURE msg.RefreshAutomaticSources
 @UserId int,@EnrollmentId uniqueidentifier,@WindowsSid nvarchar(185),@SecretHash varbinary(32),@LeaseNonce uniqueidentifier
AS BEGIN
 SET NOCOUNT ON;
 EXEC msg.VerifyPlanWorker @UserId,@EnrollmentId,@WindowsSid,@SecretHash,@LeaseNonce;
 IF EXISTS(SELECT 1 FROM msg.AutomationRule WHERE Mode=''Automatic'') BEGIN
  EXEC msg.ScanAutomationCandidates @UserId;
  EXEC msg.ScanFeeReviewCandidates @UserId;
 END;
END;');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadEventForReview @UserId int,@CandidateId bigint
AS BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 EXEC dbo.RequirePermission @UserId,N''Students.View'';EXEC dbo.RequirePermission @UserId,N''Fees.View'';
 SELECT C.CandidateId,C.EventCode,C.SourceRecordId,C.AdmissionId,S.StudentName,S.AdmissionNumber,C.CreatedUtc,
 Notice.Title NoticeTitle,Notice.NoticeText,Notice.LanguageCode NoticeLanguageCode
 FROM msg.AutomationCandidate C JOIN dbo.Students S ON S.AdmissionId=C.AdmissionId
 LEFT JOIN msg.SchoolNotice Notice ON Notice.NoticeId=C.SourceRecordId AND Notice.EventCode=C.EventCode
 WHERE C.CandidateId=@CandidateId AND C.State=''Pending'' AND C.ReviewOnly=1
 AND (C.EventCode NOT IN(''Announcement'',''ExaminationResult'') OR EXISTS(SELECT 1 FROM msg.NoticeCandidateEligible(C.CandidateId)));
END;');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadAutomaticScope @UserId int,@EventCode varchar(32)
AS BEGIN
 SET NOCOUNT ON;EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';EXEC dbo.RequirePermission @UserId,N''Students.View'';
 SELECT R.CampusId,R.ClassId,R.Mode,CONCAT(COALESCE(C.CampusName,N''All campuses''),N'' / '',COALESCE(K.ClassName,N''All classes'')) ScopeLabel
 FROM msg.AutomationRule R LEFT JOIN dbo.Campuses C ON C.CampusId=R.CampusId LEFT JOIN dbo.Classes K ON K.ClassId=R.ClassId WHERE R.EventCode=@EventCode;
END;');

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
   WHERE Existing.EventCode=C.EventCode AND Existing.SourceKey=C.SourceKey);
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
 WHERE R.EventCode=@EventCode AND D.ControlId=1
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
 ORDER BY C.EventCode,C.AdmissionId;
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
   S.StudentName,S.AdmissionNumber,C.CreatedUtc,
 Notice.Title NoticeTitle,Notice.NoticeText,Notice.LanguageCode NoticeLanguageCode
 FROM msg.AutomationCandidate C
 JOIN msg.AutomationRule R ON R.EventCode=C.EventCode AND (R.Mode=''Review'' OR C.ReviewOnly=1)
 JOIN dbo.Students S ON S.AdmissionId=C.AdmissionId AND S.Status=N''Active''
 LEFT JOIN msg.SchoolNotice Notice ON Notice.NoticeId=C.SourceRecordId AND Notice.EventCode=C.EventCode
 OUTER APPLY msg.RecordGuardian(S.AdmissionId) G
 LEFT JOIN dbo.FamilyMembers FM ON FM.AdmissionId=S.AdmissionId
 WHERE C.State=''Pending''
   AND EXISTS(SELECT 1 FROM msg.AutomationRecordInScope(C.AdmissionId,C.EventCode))
   AND C.EventCode NOT IN (''OutstandingFees'',''UpcomingDue'',''OverdueFollowUp'')
   AND (C.EventCode NOT IN(''Announcement'',''ExaminationResult'') OR EXISTS(SELECT 1 FROM msg.NoticeCandidateEligible(C.CandidateId)))
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
 JOIN msg.AutomationRule R ON R.EventCode=C.EventCode AND (R.Mode=''Review'' OR C.ReviewOnly=1)
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

EXEC(N'CREATE OR ALTER PROCEDURE msg.MarkAutomationCandidatePrepared @UserId int,
 @CandidateId bigint,@BatchId uniqueidentifier
AS BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 BEGIN TRANSACTION;
 DECLARE @StudentId int,@EventCode varchar(32),@SourceRecordId bigint,@ReviewOnly bit;
 SELECT @StudentId=AdmissionId,@EventCode=EventCode,@SourceRecordId=SourceRecordId,@ReviewOnly=ReviewOnly
 FROM msg.AutomationCandidate WITH(UPDLOCK,HOLDLOCK)
 WHERE CandidateId=@CandidateId AND State=''Pending'';
 IF @StudentId IS NULL
    THROW 51283,''This suggested event is already resolved.'',1;
 IF @EventCode IN(''Announcement'',''ExaminationResult'')
    AND NOT EXISTS(SELECT 1 FROM msg.NoticeCandidateEligible(@CandidateId))
    THROW 51453,''The official notice expired, was revoked, or its audience changed.'',1;
 DECLARE @Today date=CONVERT(date,
  (SYSUTCDATETIME() AT TIME ZONE ''UTC'') AT TIME ZONE
  (SELECT TimeZoneId FROM msg.DispatchControl WHERE ControlId=1));
 IF NOT EXISTS(SELECT 1 FROM dbo.Students S
   JOIN msg.AutomationRule R ON R.EventCode=@EventCode AND (R.Mode IN(''Review'',''Automatic'') OR @ReviewOnly=1)
   OUTER APPLY msg.RecordGuardian(S.AdmissionId) Guardian
   LEFT JOIN dbo.FamilyMembers Family ON Family.AdmissionId=S.AdmissionId
   WHERE S.AdmissionId=@StudentId AND S.Status=N''Active''
      AND EXISTS(SELECT 1 FROM msg.AutomationRecordInScope(@StudentId,@EventCode))
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
          AND (B.Audience IN (''Student'',''Family'') OR (@EventCode=''PaymentReceived'' AND B.Audience=''FeeStudent''))))
   AND Recipient.AdmissionId=@StudentId
   AND (@EventCode NOT IN(''Announcement'',''ExaminationResult'') OR EXISTS(SELECT 1 FROM msg.SchoolNotice N
     WHERE N.NoticeId=@SourceRecordId AND CHARINDEX(N.Title,B.Body)>0 AND CHARINDEX(N.NoticeText,B.Body)>0)))
    THROW 51284,''Only a confirmed message for the proposed school record can resolve this event.'',1;
 UPDATE msg.AutomationCandidate SET State=''Prepared'',ReviewedBy=@UserId,
  ReviewedUtc=SYSUTCDATETIME(),BatchId=@BatchId WHERE CandidateId=@CandidateId;
 COMMIT;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.SaveAutomationMode
 @UserId int,@EventCode varchar(32),@Mode varchar(10),@CampusId int=NULL,@ClassId int=NULL
AS BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 IF @Mode IS NULL OR @Mode NOT IN (''Off'',''Review'',''Automatic'') OR @CampusId<0 OR @ClassId<0
   OR (@ClassId>0 AND @CampusId=0)
  THROW 51246,''Invalid automation mode or audience.'',1;
 -- Automatic sending remains disabled until full verified event dispatch.
 IF @Mode=''Automatic'' OR
   (@Mode=''Review'' AND @EventCode NOT IN
      (''Admission'',''PaymentReceived'',''Birthday'',''OutstandingFees'',''UpcomingDue'',''OverdueFollowUp'',''Announcement'',''ExaminationResult''))
  THROW 51247,''This automatic event source is not yet available. Use manual messages for now.'',1;
 BEGIN TRANSACTION;
 IF NOT EXISTS(SELECT 1 FROM msg.AutomationRule WITH(UPDLOCK,HOLDLOCK)
     WHERE EventCode=@EventCode)
  THROW 51246,''Unknown automation event.'',1;
 -- A mode-only change must preserve the configured audience. An explicit
 -- zero still selects the whole school; null means keep the current scope.
 SELECT @CampusId=COALESCE(@CampusId,CampusId),@ClassId=COALESCE(@ClassId,ClassId)
 FROM msg.AutomationRule WITH(UPDLOCK,HOLDLOCK) WHERE EventCode=@EventCode;
 IF @ClassId>0 AND @CampusId=0
  THROW 51246,''A class-scoped rule requires its campus.'',1;
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
 UPDATE W SET State=''Held'',ReasonCode=''RuleChanged'',UpdatedUtc=SYSUTCDATETIME()
 FROM msg.PlannedWork W JOIN msg.AutomationCandidate C ON C.CandidateId=W.CandidateId
 WHERE C.EventCode=@EventCode AND W.State IN(''Pending'',''Preparing'');
 UPDATE S SET HoldForReview=1,UpdatedBy=@UserId,UpdatedUtc=SYSUTCDATETIME()
 FROM msg.BatchSchedule S JOIN msg.PlannedWork W ON W.BatchId=S.BatchId JOIN msg.AutomationCandidate C ON C.CandidateId=W.CandidateId
 WHERE C.EventCode=@EventCode;
 COMMIT;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadAutomationRules @UserId int
AS BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 SELECT EventCode,Mode,CampusId,ClassId,ActivationBaselineUtc,UpdatedUtc,
        FeeReminderGapDays,FeeRemindersPerMonth,AuthorizationId,
 CONVERT(bit,CASE WHEN EXISTS(SELECT 1 FROM msg.AutomaticAuthorized(msg.AutomationRule.AuthorizationId)) THEN 1 ELSE 0 END) AuthorizationReady
 FROM msg.AutomationRule ORDER BY EventCode;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ClaimPlannedWork
 @UserId int,@ClaimNonce uniqueidentifier,@EnrollmentId uniqueidentifier,@WindowsSid nvarchar(185),@SecretHash varbinary(32),@LeaseNonce uniqueidentifier,@SkipSms bit=0,@SkipWhatsApp bit=0
AS BEGIN
 SET NOCOUNT ON;SET XACT_ABORT ON;
 BEGIN TRANSACTION;
 EXEC msg.VerifyPlanWorker @UserId,@EnrollmentId,@WindowsSid,@SecretHash,@LeaseNonce;
 IF @ClaimNonce IS NULL THROW 51401,''A durable preparation identity is required.'',1;
 DECLARE @Work uniqueidentifier;
 SELECT TOP(1) @Work=W.WorkId FROM msg.PlannedWork W WITH(UPDLOCK,HOLDLOCK)
 WHERE W.State IN(''Pending'',''Preparing'')
 AND (@SkipSms=0 OR NOT EXISTS(SELECT 1 FROM msg.RecurringRule R JOIN msg.Batch B ON B.BatchId=R.OriginBatchId WHERE R.RuleId=W.RuleId AND B.Channel=''Sms'')
   AND NOT EXISTS(SELECT 1 FROM msg.AutomaticAuthorization A WHERE A.AuthorizationId=W.AuthorizationId AND A.Channel=''Sms''))
 AND (@SkipWhatsApp=0 OR NOT EXISTS(SELECT 1 FROM msg.RecurringRule R JOIN msg.Batch B ON B.BatchId=R.OriginBatchId WHERE R.RuleId=W.RuleId AND B.Channel=''WhatsApp'')
   AND NOT EXISTS(SELECT 1 FROM msg.AutomaticAuthorization A WHERE A.AuthorizationId=W.AuthorizationId AND A.Channel=''WhatsApp''))
 AND (W.State=''Pending'' OR W.ClaimUntilUtc<SYSUTCDATETIME()) AND W.DueUtc<=SYSUTCDATETIME() AND W.ExpiresUtc>SYSUTCDATETIME()
 AND ((W.SourceKind=''Recurrence'' AND EXISTS(SELECT 1 FROM msg.RecurringEligible(W.RuleId)))
  OR (W.SourceKind=''Event'' AND EXISTS(SELECT 1 FROM msg.AutomaticAuthorized(W.AuthorizationId))
    AND EXISTS(SELECT 1 FROM msg.EventSourceEligible(W.CandidateId))
    AND EXISTS(SELECT 1 FROM msg.AutomationCandidate C WHERE C.CandidateId=W.CandidateId AND C.State=''Pending'' AND C.ReviewOnly=0 AND EXISTS(SELECT 1 FROM msg.AutomaticAuthorization A WHERE A.AuthorizationId=W.AuthorizationId AND A.EventCode=C.EventCode)))) ORDER BY W.DueUtc,W.WorkId;
 IF @Work IS NOT NULL
 BEGIN
  UPDATE msg.PlannedWork SET State=''Preparing'',ClaimNonce=@ClaimNonce,ClaimUntilUtc=DATEADD(minute,2,SYSUTCDATETIME()),
    EnrollmentId=@EnrollmentId,UpdatedUtc=SYSUTCDATETIME() WHERE WorkId=@Work;
  IF EXISTS(SELECT 1 FROM msg.PlannedWork WHERE WorkId=@Work AND SourceKind=''Recurrence'')
  BEGIN
  SELECT W.WorkId,W.ClaimNonce,W.DueUtc,W.ExpiresUtc,W.SourceKind,W.RuleId,
   B.Audience,B.CampusId,B.ClassId,B.StudentId,B.StaffId,B.Channel,B.Body,R.CreatedBy AuthorizedBy,
   A.ProviderCode,A.ProfileHash,A.TemplateJson,S.Priority
  FROM msg.PlannedWork W JOIN msg.RecurringRule R ON R.RuleId=W.RuleId
  JOIN msg.Batch B ON B.BatchId=R.OriginBatchId JOIN msg.ApiBatch A ON A.BatchId=B.BatchId
  JOIN msg.BatchSchedule S ON S.BatchId=B.BatchId WHERE W.WorkId=@Work;
  END
  ELSE BEGIN
   UPDATE W SET EventDataHash=COALESCE(P.SourceHash,N.SourceHash),FamilyToken=msg.FamilyBoundary(C.AdmissionId)
   FROM msg.PlannedWork W JOIN msg.AutomationCandidate C ON C.CandidateId=W.CandidateId
   OUTER APPLY (SELECT * FROM msg.PaymentEventSource(C.SourceRecordId) WHERE C.EventCode=''PaymentReceived'') P
   LEFT JOIN msg.SchoolNotice N ON N.NoticeId=C.SourceRecordId AND N.EventCode=C.EventCode WHERE W.WorkId=@Work;
   SELECT W.WorkId,W.ClaimNonce,W.DueUtc,W.ExpiresUtc,W.SourceKind,W.RuleId,
    CASE WHEN C.EventCode IN(''OutstandingFees'',''UpcomingDue'',''OverdueFollowUp'') THEN ''FeeFamily''
         WHEN C.EventCode=''PaymentReceived'' THEN ''FeeStudent'' ELSE ''Student'' END Audience,
    CONVERT(int,0) CampusId,CONVERT(int,0) ClassId,C.AdmissionId StudentId,CONVERT(int,0) StaffId,A.Channel,A.Body,A.ApprovedBy AuthorizedBy,
    A.ProviderCode,A.ProfileHash,A.TemplateJson,A.Priority,A.LanguageCode,C.EventCode,
    P.TotalAmount PaymentAmount,P.IsCreditApplication,P.ReceiptNumber,
    N.Title NoticeTitle,N.NoticeText NoticeText
   FROM msg.PlannedWork W JOIN msg.AutomationCandidate C ON C.CandidateId=W.CandidateId
   JOIN msg.AutomaticAuthorization A ON A.AuthorizationId=W.AuthorizationId
   OUTER APPLY (SELECT * FROM msg.PaymentEventSource(C.SourceRecordId) WHERE C.EventCode=''PaymentReceived'') P
   LEFT JOIN msg.SchoolNotice N ON N.NoticeId=C.SourceRecordId AND N.EventCode=C.EventCode WHERE W.WorkId=@Work;
  END;
 END;
 COMMIT;
END;');

EXEC(N'CREATE OR ALTER PROCEDURE msg.BindPlannedWork
 @UserId int,@BatchId uniqueidentifier,@WorkId uniqueidentifier,@ClaimNonce uniqueidentifier,
 @EnrollmentId uniqueidentifier,@WindowsSid nvarchar(185),@SecretHash varbinary(32),@LeaseNonce uniqueidentifier
AS BEGIN
 SET NOCOUNT ON;
 IF @@TRANCOUNT=0 THROW 51401,''Preparation must bind inside the common batch confirmation transaction.'',1;
 EXEC msg.VerifyPlanWorker @UserId,@EnrollmentId,@WindowsSid,@SecretHash,@LeaseNonce;
 IF EXISTS(SELECT 1 FROM msg.PlannedWork WHERE WorkId=@WorkId AND State=''Prepared'' AND BatchId=@BatchId
   AND ClaimNonce=@ClaimNonce AND EnrollmentId=@EnrollmentId) RETURN;
 IF EXISTS(SELECT 1 FROM msg.PlannedWork WHERE WorkId=@WorkId AND SourceKind=''Event'')
 BEGIN
  DECLARE @Candidate bigint,@Auth uniqueidentifier,@Admission int,@Code varchar(32),@Author int,@DueEvent datetime2(3),@ExpiryEvent datetime2(3);
  SELECT @Candidate=W.CandidateId,@Auth=W.AuthorizationId,@Admission=C.AdmissionId,@Code=C.EventCode,@Author=A.ApprovedBy,
    @DueEvent=W.DueUtc,@ExpiryEvent=W.ExpiresUtc
  FROM msg.PlannedWork W WITH(UPDLOCK,HOLDLOCK) JOIN msg.AutomationCandidate C WITH(UPDLOCK,HOLDLOCK) ON C.CandidateId=W.CandidateId
  JOIN msg.AutomaticAuthorization A WITH(HOLDLOCK) ON A.AuthorizationId=W.AuthorizationId
  WHERE W.WorkId=@WorkId AND W.State=''Preparing'' AND W.ClaimNonce=@ClaimNonce AND W.EnrollmentId=@EnrollmentId
   AND A.EventCode=C.EventCode AND W.ClaimUntilUtc>SYSUTCDATETIME() AND W.ExpiresUtc>SYSUTCDATETIME() AND C.State=''Pending'' AND C.ReviewOnly=0
   AND W.FamilyToken=msg.FamilyBoundary(C.AdmissionId)
   AND (C.EventCode<>''PaymentReceived'' OR EXISTS(SELECT 1 FROM msg.PaymentEventSource(C.SourceRecordId) P WHERE P.SourceHash=W.EventDataHash))
   AND (C.EventCode NOT IN(''Announcement'',''ExaminationResult'') OR EXISTS(SELECT 1 FROM msg.SchoolNotice N
     WHERE N.NoticeId=C.SourceRecordId AND N.EventCode=C.EventCode AND N.SourceHash=W.EventDataHash));
  IF @Candidate IS NULL OR NOT EXISTS(SELECT 1 FROM msg.AutomaticAuthorized(@Auth)) OR NOT EXISTS(SELECT 1 FROM msg.EventSourceEligible(@Candidate))
    THROW 51431,''The event, family, template or automatic authorization changed. Review the source.'',1;
  EXEC dbo.RequirePermission @Author,N''Communication.Configure'';EXEC dbo.RequirePermission @Author,N''Communication.Send'';
  EXEC dbo.RequirePermission @Author,N''Students.View'';EXEC dbo.RequirePermission @Author,N''Fees.View'';
  IF NOT EXISTS(SELECT 1 FROM msg.Batch B JOIN msg.ApiBatch A ON A.BatchId=B.BatchId JOIN msg.AutomaticAuthorization Approval ON Approval.AuthorizationId=@Auth
    WHERE B.BatchId=@BatchId AND B.ClientRequestId=@WorkId AND B.CreatedBy=@UserId AND B.StudentId=@Admission AND B.StaffId=0 AND B.CampusId=0 AND B.ClassId=0
      AND B.Channel=Approval.Channel AND A.ProviderCode=Approval.ProviderCode AND A.ProfileHash=Approval.ProfileHash
      AND B.Audience=CASE WHEN @Code IN(''OutstandingFees'',''UpcomingDue'',''OverdueFollowUp'') THEN ''FeeFamily''
          WHEN @Code=''PaymentReceived'' THEN ''FeeStudent'' ELSE ''Student'' END
      AND (@Code=''PaymentReceived''
     OR (@Code IN(''Announcement'',''ExaminationResult'') AND EXISTS(SELECT 1 FROM msg.SchoolNotice N
        JOIN msg.AutomationCandidate Source ON Source.SourceRecordId=N.NoticeId
        WHERE Source.CandidateId=@Candidate AND N.EventCode=@Code
        AND B.Body COLLATE Latin1_General_100_BIN2=
         REPLACE(REPLACE(Approval.Body,N''[NoticeTitle]'',N.Title),N''[NoticeText]'',N.NoticeText) COLLATE Latin1_General_100_BIN2))
     OR (@Code NOT IN(''Announcement'',''ExaminationResult'')
        AND B.Body COLLATE Latin1_General_100_BIN2=Approval.Body COLLATE Latin1_General_100_BIN2)))
    THROW 51431,''Prepared content or audience differs from the approved automatic rule.'',1;
  EXEC msg.MarkAutomationCandidatePrepared @UserId,@Candidate,@BatchId;
  UPDATE msg.PlannedWork SET State=''Prepared'',BatchId=@BatchId,UpdatedUtc=SYSUTCDATETIME() WHERE WorkId=@WorkId;
  INSERT msg.BatchSchedule(BatchId,DueUtc,ExpiresUtc,Priority,UpdatedBy)
   SELECT @BatchId,@DueEvent,@ExpiryEvent,Priority,@UserId FROM msg.AutomaticAuthorization WHERE AuthorizationId=@Auth;
  RETURN;
 END;
 DECLARE @Rule uniqueidentifier,@Expiry datetime2(3),@Due datetime2(3),@Owner int;
 SELECT @Rule=W.RuleId,@Expiry=W.ExpiresUtc,@Due=W.DueUtc,@Owner=R.CreatedBy
 FROM msg.PlannedWork W WITH(UPDLOCK,HOLDLOCK) JOIN msg.RecurringRule R WITH(HOLDLOCK) ON R.RuleId=W.RuleId
 WHERE W.WorkId=@WorkId AND W.SourceKind=''Recurrence'' AND W.State=''Preparing''
 AND W.ClaimNonce=@ClaimNonce AND W.EnrollmentId=@EnrollmentId AND W.ClaimUntilUtc>SYSUTCDATETIME();
 IF @Rule IS NULL OR @Expiry<=SYSUTCDATETIME() OR NOT EXISTS(SELECT 1 FROM msg.RecurringEligible(@Rule))
  THROW 51401,''The approved occurrence changed or expired; nothing was scheduled.'',1;
 EXEC dbo.RequirePermission @Owner,N''Communication.Send'';
 IF NOT EXISTS(SELECT 1 FROM msg.RecurringRule R JOIN msg.Batch B ON B.BatchId=R.OriginBatchId
   CROSS APPLY msg.CanViewAudience(@Owner,B.Audience) Scope WHERE R.RuleId=@Rule)
  THROW 51401,''The original recurring approver lost school record permissions.'',1;
 IF NOT EXISTS(SELECT 1 FROM msg.Batch B JOIN msg.ApiBatch A ON A.BatchId=B.BatchId
   JOIN msg.RecurringRule R ON R.RuleId=@Rule JOIN msg.Batch O ON O.BatchId=R.OriginBatchId JOIN msg.ApiBatch OA ON OA.BatchId=O.BatchId
   WHERE B.BatchId=@BatchId AND B.CreatedBy=@UserId AND B.ClientRequestId=@WorkId
   AND B.Channel=O.Channel AND B.Audience=O.Audience AND B.CampusId=O.CampusId AND B.ClassId=O.ClassId
   AND B.StudentId=O.StudentId AND B.StaffId=O.StaffId AND B.Body COLLATE Latin1_General_100_BIN2=O.Body COLLATE Latin1_General_100_BIN2
   AND A.ProviderCode=OA.ProviderCode AND A.ProfileHash=OA.ProfileHash AND ISNULL(A.TemplateJson,N'''')=ISNULL(OA.TemplateJson,N''''))
  THROW 51401,''The prepared message differs from its authorized recurring content or audience.'',1;
 UPDATE msg.PlannedWork SET State=''Prepared'',BatchId=@BatchId,UpdatedUtc=SYSUTCDATETIME() WHERE WorkId=@WorkId;
 INSERT msg.BatchSchedule(BatchId,DueUtc,ExpiresUtc,Priority,UpdatedBy)
 SELECT @BatchId,@Due,@Expiry,S.Priority,@UserId FROM msg.RecurringRule R JOIN msg.BatchSchedule S ON S.BatchId=R.OriginBatchId WHERE R.RuleId=@Rule;
END;');

EXEC(N'CREATE OR ALTER FUNCTION msg.PlanBatchEligible(@BatchId uniqueidentifier)
RETURNS TABLE AS RETURN
(
 SELECT CONVERT(bit,1) Eligible WHERE NOT EXISTS(
 SELECT 1 FROM msg.PlannedWork W WITH(HOLDLOCK) LEFT JOIN msg.AutomationCandidate C WITH(HOLDLOCK) ON C.CandidateId=W.CandidateId
 WHERE W.BatchId=@BatchId AND (W.State<>''Prepared'' OR W.ExpiresUtc<=SYSUTCDATETIME()
  OR (W.SourceKind=''Recurrence'' AND NOT EXISTS(SELECT 1 FROM msg.RecurringEligible(W.RuleId)))
  OR (W.SourceKind=''Event'' AND (NOT EXISTS(SELECT 1 FROM msg.AutomaticAuthorized(W.AuthorizationId))
    OR NOT EXISTS(SELECT 1 FROM msg.EventSourceEligible(W.CandidateId))
    OR W.FamilyToken IS NULL OR W.FamilyToken<>msg.FamilyBoundary(C.AdmissionId)
    OR (C.EventCode=''PaymentReceived'' AND NOT EXISTS(SELECT 1 FROM msg.PaymentEventSource(C.SourceRecordId) P WHERE P.SourceHash=W.EventDataHash))
    OR (C.EventCode IN(''Announcement'',''ExaminationResult'') AND NOT EXISTS(SELECT 1 FROM msg.SchoolNotice N
      WHERE N.NoticeId=C.SourceRecordId AND N.EventCode=C.EventCode AND N.SourceHash=W.EventDataHash))))))
);');

EXEC(N'CREATE OR ALTER FUNCTION msg.RecurringEligible(@RuleId uniqueidentifier)
RETURNS TABLE AS RETURN
(
 SELECT R.RuleId FROM msg.RecurringRule R WITH(HOLDLOCK)
 JOIN msg.Batch B WITH(HOLDLOCK) ON B.BatchId=R.OriginBatchId
 WHERE EXISTS(SELECT 1 FROM msg.HasSchoolPermission(R.CreatedBy,N''Communication.Send'')) AND EXISTS(SELECT 1 FROM msg.CanViewAudience(R.CreatedBy,B.Audience)) AND R.RuleId=@RuleId AND R.State IN(''Active'',''Completed'')
 AND ((R.RestoreBoundaryHash=msg.RestoreBoundary()) OR
      (R.RestoreBoundaryHash IS NULL AND msg.RestoreBoundary() IS NULL))
 AND (B.ClassId=0 OR R.AcademicToken=msg.AcademicSessionToken())
);');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadUnpreparedWork @UserId int
AS BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 SELECT TOP(200) R.RuleId QueueId,R.RuleId PlanRuleId,CONVERT(bigint,NULL) PlanCandidateId,''Recurrence'' SourceRowKind,B.Channel,B.Audience,R.CreatedUtc,
 CONVERT(bigint,1) InProgress,A.ProviderCode Method,
 COALESCE(W.DueUtc,R.NextDueUtc) DueUtc,
 CONVERT(bit,CASE WHEN msg.BatchRecoveryBlocked(B.BatchId)=1 THEN 1 ELSE 0 END) RecoveryBlocked,
 CONVERT(datetime2(3),COALESCE(W.DueUtc,R.NextDueUtc) AT TIME ZONE ''UTC'' AT TIME ZONE R.TimeZoneId) DueSchool,
 CONVERT(varchar(20),CASE WHEN R.State=''Held'' OR W.State=''Held'' THEN ''Needs Attention''
    WHEN COALESCE(W.DueUtc,R.NextDueUtc)>SYSUTCDATETIME() THEN ''Upcoming'' ELSE ''All Active'' END) QueueStatus,
 CONCAT(COALESCE(S.StudentName,T.StaffName,C.CampusName,N''School''),N'' · '',COALESCE(K.ClassName,N''''),N'' · Recurring schedule · '',R.State) RecipientText
 FROM msg.RecurringRule R JOIN msg.Batch B ON B.BatchId=R.OriginBatchId JOIN msg.ApiBatch A ON A.BatchId=B.BatchId
 LEFT JOIN dbo.Students S ON S.AdmissionId=B.StudentId LEFT JOIN dbo.Staff T ON T.StaffId=B.StaffId
 LEFT JOIN dbo.Campuses C ON C.CampusId=B.CampusId LEFT JOIN dbo.Classes K ON K.ClassId=B.ClassId
 OUTER APPLY(SELECT TOP(1) X.DueUtc,X.State FROM msg.PlannedWork X WHERE X.RuleId=R.RuleId AND X.BatchId IS NULL
   AND X.State IN(''Pending'',''Preparing'',''Held'') ORDER BY X.DueUtc) W
 WHERE R.State IN(''Active'',''Paused'',''Held'') AND EXISTS(SELECT 1 FROM msg.CanViewAudience(@UserId,B.Audience))
 AND (R.CreatedBy=@UserId OR EXISTS(SELECT 1 FROM dbo.UserRoles UR JOIN dbo.Roles O ON O.RoleId=UR.RoleId WHERE UR.UserId=@UserId AND O.RoleName=N''Administrator'')
   OR EXISTS(SELECT 1 FROM dbo.Users U JOIN dbo.Roles O ON O.RoleId=U.RoleId WHERE U.UserId=@UserId AND O.RoleName=N''Administrator''))
 AND (R.State IN(''Paused'',''Held'') OR W.State IS NOT NULL OR NOT EXISTS(SELECT 1 FROM msg.PlannedWork PW JOIN msg.Recipient P ON P.BatchId=PW.BatchId
   WHERE PW.RuleId=R.RuleId AND P.State IN(''Pending'',''Unknown'',''Submitted'')))
 UNION ALL
 SELECT W.WorkId,CONVERT(uniqueidentifier,NULL),C.CandidateId,''Event'',A.Channel,
  CASE WHEN C.EventCode IN(''OutstandingFees'',''UpcomingDue'',''OverdueFollowUp'') THEN ''FeeFamily''
       WHEN C.EventCode=''PaymentReceived'' THEN ''FeeStudent'' ELSE ''Student'' END,W.CreatedUtc,CONVERT(bigint,1),A.ProviderCode,W.DueUtc,
  CONVERT(bit,CASE WHEN (A.RestoreBoundaryHash=msg.RestoreBoundary()) OR (A.RestoreBoundaryHash IS NULL AND msg.RestoreBoundary() IS NULL) THEN 0 ELSE 1 END),
  CONVERT(datetime2(3),W.DueUtc AT TIME ZONE ''UTC'' AT TIME ZONE D.TimeZoneId),
  CONVERT(varchar(20),CASE WHEN W.State=''Held'' THEN ''Needs Attention'' ELSE ''All Active'' END),
  CONCAT(S.StudentName,N'' · Automatic · '',C.EventCode,N'' · '',COALESCE(W.ReasonCode,W.State))
 FROM msg.PlannedWork W JOIN msg.AutomaticAuthorization A ON A.AuthorizationId=W.AuthorizationId
 JOIN msg.AutomationCandidate C ON C.CandidateId=W.CandidateId JOIN dbo.Students S ON S.AdmissionId=C.AdmissionId
 CROSS JOIN msg.DispatchControl D
 WHERE W.SourceKind=''Event'' AND W.BatchId IS NULL AND W.State IN(''Pending'',''Preparing'',''Held'') AND D.ControlId=1
 AND EXISTS(SELECT 1 FROM msg.CanViewAudience(@UserId,CASE WHEN C.EventCode IN(''PaymentReceived'',''OutstandingFees'',''UpcomingDue'',''OverdueFollowUp'') THEN ''FeeStudent'' ELSE ''Student'' END))
 AND (A.ApprovedBy=@UserId OR EXISTS(SELECT 1 FROM dbo.Roles O WHERE O.RoleName=N''Administrator'' AND
   (EXISTS(SELECT 1 FROM dbo.Users U WHERE U.UserId=@UserId AND U.RoleId=O.RoleId) OR EXISTS(SELECT 1 FROM dbo.UserRoles UR WHERE UR.UserId=@UserId AND UR.RoleId=O.RoleId))))
 ORDER BY CreatedUtc DESC;
END;');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ClaimRecipient
    @UserId int,
    @RecipientId bigint,
    @AttemptKey uniqueidentifier,
    @ExpectedProviderCode varchar(40),@IsWorker bit=0,
    @EnrollmentId uniqueidentifier=NULL,@WindowsSid nvarchar(185)=NULL,
    @SecretHash varbinary(32)=NULL,@LeaseNonce uniqueidentifier=NULL
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
    IF @RecipientId IS NULL OR @RecipientId<=0
       OR @AttemptKey IS NULL OR @AttemptKey=''00000000-0000-0000-0000-000000000000''
       OR NULLIF(LTRIM(RTRIM(@ExpectedProviderCode)),'''') IS NULL
        THROW 51090,''A valid recipient, attempt key and approved provider are required.'',1;

    BEGIN TRANSACTION;

    IF @IsWorker IS NULL OR (@IsWorker=0 AND (@EnrollmentId IS NOT NULL
      OR @WindowsSid IS NOT NULL OR @SecretHash IS NOT NULL OR @LeaseNonce IS NOT NULL))
      THROW 51261,''Choose one authenticated interactive or worker claim.'',1;
    IF @IsWorker=1
    BEGIN
      IF @EnrollmentId IS NULL OR @WindowsSid IS NULL OR @SecretHash IS NULL OR @LeaseNonce IS NULL
        THROW 51261,''Complete background processor proof is required at claim.'',1;
      EXEC msg.VerifyBackgroundWorker @UserId,@EnrollmentId,@WindowsSid,@SecretHash,@ReturnState=0;
      IF NOT EXISTS(SELECT 1 FROM msg.BackgroundWorkerLease WITH(UPDLOCK,HOLDLOCK)
        WHERE LeaseId=1 AND EnrollmentId=@EnrollmentId AND LeaseNonce=@LeaseNonce AND LeaseUntilUtc>SYSUTCDATETIME())
        THROW 51263,''The background processor lease expired or belongs to another invocation.'',1;
    END;

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
    -- Explicit administrator takeover remains separate from original approval.
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

    IF msg.BatchRecoveryBlocked(@BatchId)=1
        THROW 51341,''The approval predates database recovery; reconcile before sending.'',1;

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
        SELECT @CurrentPhone=Guardian.PhoneE164
        FROM dbo.Students S WITH(HOLDLOCK)
        OUTER APPLY msg.RecordGuardian(S.AdmissionId) Guardian
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
          AND Guardian.HasConsent=1
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
        WHEN EXISTS(SELECT 1 FROM msg.AutomationCandidate Notice WITH(HOLDLOCK)
          WHERE Notice.BatchId=@BatchId AND Notice.EventCode IN(''Announcement'',''ExaminationResult'')
           AND NOT EXISTS(SELECT 1 FROM msg.NoticeCandidateEligible(Notice.CandidateId))) THEN ''SourceChanged''
        WHEN NOT EXISTS(SELECT 1 FROM msg.PreparedEventEligible(@BatchId)) THEN ''SourceChanged''
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
    IF EXISTS(SELECT 1 FROM msg.PlannedWork W LEFT JOIN msg.RecurringRule Rec ON Rec.RuleId=W.RuleId
      LEFT JOIN msg.AutomaticAuthorization Approval ON Approval.AuthorizationId=W.AuthorizationId
      JOIN msg.Batch B ON B.BatchId=W.BatchId
      WHERE W.BatchId=@BatchId AND (NOT EXISTS(SELECT 1 FROM msg.HasSchoolPermission(COALESCE(Approval.ApprovedBy,Rec.CreatedBy),N''Communication.Send''))
       OR NOT EXISTS(SELECT 1 FROM msg.CanViewAudience(COALESCE(Approval.ApprovedBy,Rec.CreatedBy),B.Audience))
       OR (W.SourceKind=''Event'' AND NOT EXISTS(SELECT 1 FROM msg.HasSchoolPermission(Approval.ApprovedBy,N''Communication.Configure'')))))
      THROW 51431,''The original sending authorization changed. Review this work.'',1;
    IF @Kind=''Student'' AND @Purpose IN (''Announcement'',''FeeReminder'')
       AND NOT EXISTS(SELECT 1 FROM msg.PlannedWork W JOIN msg.AutomationCandidate C ON C.CandidateId=W.CandidateId WHERE W.BatchId=@BatchId AND C.EventCode=''PaymentReceived'')
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

    IF EXISTS(SELECT 1 FROM msg.PlannedWork W JOIN msg.AutomationCandidate C ON C.CandidateId=W.CandidateId WHERE W.BatchId=@BatchId
      AND NOT EXISTS(SELECT 1 FROM msg.EventDailyAllowed(@AdmissionId,W.AuthorizationId)))
      THROW 51420,''This family reached the approved daily event allowance.'',1;
    IF @Purpose=''FeeReminder'' AND EXISTS(SELECT 1 FROM msg.PlannedWork WHERE BatchId=@BatchId)
       AND NOT EXISTS(SELECT 1 FROM msg.PlannedWork W JOIN msg.AutomationCandidate C ON C.CandidateId=W.CandidateId WHERE W.BatchId=@BatchId AND C.EventCode=''PaymentReceived'')
       AND NOT EXISTS(SELECT 1 FROM msg.PlannedFeeAllowed(@AdmissionId,@BatchId))
        THROW 51420,''The family fee-reminder spacing or monthly allowance requires waiting.'',1;
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
    INSERT msg.Attempt(RecipientId,AttemptNumber,AttemptKey,Channel,ProviderCode,Outcome,ClaimedBy,ClaimKind,WorkerEnrollmentId)
    VALUES(@RecipientId,@Ordinal,@AttemptKey,@Channel,@ProviderCode,''Unknown'',@UserId,CASE WHEN @IsWorker=1 THEN ''Worker'' ELSE ''Interactive'' END,@EnrollmentId);
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
      -- School-issued notice approvals remain source-bound after human review.
      -- A revoked, expired, withdrawn or no-longer-consenting recipient cannot
      -- open an old approved WhatsApp message by resuming its saved batch.
      WHEN EXISTS(SELECT 1 FROM msg.AutomationCandidate C WITH(HOLDLOCK)
        WHERE C.BatchId=@BatchId AND C.EventCode IN(''Announcement'',''ExaminationResult'')
        AND NOT EXISTS(SELECT 1 FROM msg.NoticeCandidateEligible(C.CandidateId)))
        THEN ''SourceChanged''
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

EXEC(N'CREATE OR ALTER TRIGGER msg.TR_AutomaticAuthorizationImmutable ON msg.AutomaticAuthorization AFTER UPDATE,DELETE
AS BEGIN SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM deleted) THROW 51430,''Automatic authorization history is immutable. Approve a new revision.'',1;
END;');


 INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
 VALUES(N'35',SYSDATETIME(),SUSER_SNAME(),N'Explicit event-specific API authorization and atomic source-bound preparation through the common Queue');
 COMMIT;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK;
 THROW;
END CATCH;
