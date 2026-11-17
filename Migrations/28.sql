-- Bind bill and receipt communication to the exact reviewed financial source. This is
-- communication evidence only; no fee, receipt or accounting row is changed.
SET NOCOUNT ON; SET XACT_ABORT ON;
IF EXISTS(SELECT 1 FROM dbo.DatabaseVersion WHERE TRY_CONVERT(int,VersionNumber)=28) RETURN;
IF ISNULL((SELECT TOP(1) TRY_CONVERT(int,VersionNumber) FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0)<>27
 THROW 51000,'Financial communication review requires the preceding migration.',1;
BEGIN TRY
BEGIN TRANSACTION;

CREATE TABLE msg.FinancialReviewProof(
 ClientRequestId uniqueidentifier NOT NULL CONSTRAINT PK_MsgFinancialReviewProof PRIMARY KEY,
 SourceKind varchar(10) NOT NULL,
 CollectionReceiptId uniqueidentifier NULL CONSTRAINT FK_MsgFinancialReviewProofReceipt REFERENCES dbo.CollectionReceipts(CollectionReceiptId),
 AdmissionId int NOT NULL,
 WholeFamily bit NOT NULL,
 SourceHash binary(32) NOT NULL,
 ReviewedBy int NOT NULL,
 CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_MsgFinancialReviewProofCreated DEFAULT SYSUTCDATETIME(),
 LastCheckedUtc datetime2(3) NOT NULL CONSTRAINT DF_MsgFinancialReviewProofChecked DEFAULT SYSUTCDATETIME(),
 CONSTRAINT CK_MsgFinancialReviewProofSource CHECK((SourceKind='Bill' AND CollectionReceiptId IS NULL) OR (SourceKind='Receipt' AND CollectionReceiptId IS NOT NULL)),
 CONSTRAINT CK_MsgFinancialReviewProofStudent CHECK(AdmissionId>0),
 CONSTRAINT CK_MsgFinancialReviewProofKey CHECK(ClientRequestId<>'00000000-0000-0000-0000-000000000000')
);
CREATE INDEX IX_MsgFinancialReviewProofChecked ON msg.FinancialReviewProof(LastCheckedUtc);
ALTER TABLE msg.Batch ADD FinancialReviewKey uniqueidentifier NULL;
EXEC(N'ALTER TABLE msg.Batch ADD CONSTRAINT FK_MsgBatchFinancialReview
 FOREIGN KEY(FinancialReviewKey) REFERENCES msg.FinancialReviewProof(ClientRequestId)');
EXEC(N'ALTER TABLE msg.Batch ADD CONSTRAINT CK_MsgBatchFinancialReview
 CHECK(FinancialReviewKey IS NULL OR FinancialReviewKey=ClientRequestId)');


EXEC(N'CREATE OR ALTER FUNCTION dbo.CollectionReviewToken(@AdmissionId int)
RETURNS TABLE AS RETURN
(
 SELECT CONVERT(varchar(64),HASHBYTES(''SHA2_256'',Raw.Json),2) ReviewToken
 FROM (SELECT CONCAT(
  (SELECT B.* FROM dbo.BillingCharges B WITH(HOLDLOCK) WHERE AdmissionId=@AdmissionId ORDER BY BillingChargeId FOR JSON PATH,INCLUDE_NULL_VALUES),
  (SELECT P.* FROM dbo.FeePayments P WITH(HOLDLOCK) WHERE AdmissionId=@AdmissionId ORDER BY FeePaymentId FOR JSON PATH,INCLUDE_NULL_VALUES),
  (SELECT A.* FROM dbo.FeePaymentAllocations A WITH(HOLDLOCK) WHERE AdmissionId=@AdmissionId ORDER BY FeePaymentAllocationId FOR JSON PATH,INCLUDE_NULL_VALUES),
  (SELECT A.* FROM dbo.StudentFeeAssignments A WITH(HOLDLOCK) WHERE AdmissionId=@AdmissionId ORDER BY StudentFeeAssignmentId FOR JSON PATH,INCLUDE_NULL_VALUES),
  (SELECT C.* FROM dbo.StudentCreditEntries C WITH(HOLDLOCK) WHERE AdmissionId=@AdmissionId ORDER BY CreditEntryId FOR JSON PATH,INCLUDE_NULL_VALUES),
  (SELECT C.* FROM dbo.FeeBillCorrections C WITH(HOLDLOCK) JOIN dbo.BillingCharges B WITH(HOLDLOCK) ON B.BillingChargeId=C.BillingChargeId WHERE B.AdmissionId=@AdmissionId ORDER BY C.FeeBillCorrectionId FOR JSON PATH,INCLUDE_NULL_VALUES),
  (SELECT R.CollectionReceiptId,R.VisitSequence,R.SnapshotJson FROM dbo.CollectionReceipts R WITH(HOLDLOCK)
   WHERE EXISTS(SELECT 1 FROM OPENJSON(R.SnapshotJson,''$.Students'') WITH(AdmissionId int) S WHERE S.AdmissionId=@AdmissionId)
   ORDER BY R.VisitSequence FOR JSON PATH,INCLUDE_NULL_VALUES)) Json) Raw
);');

EXEC(N'CREATE OR ALTER PROCEDURE dbo.GetCollectionReviewState
 @UserId int,@AdmissionId int,@ExpectedToken varchar(64)=NULL
AS BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Fees.View'';
 DECLARE @Token varchar(64)=(SELECT ReviewToken FROM dbo.CollectionReviewToken(@AdmissionId));
 IF @ExpectedToken IS NOT NULL AND @ExpectedToken<>@Token
  THROW 53104,''Fees, concessions, payments or credit changed after loading. Reload the family before collecting; nothing was saved.'',1;
 SELECT @Token ReviewToken;
END;');

EXEC(N'CREATE OR ALTER FUNCTION msg.BillSource(@AdmissionId int,@WholeFamily bit)
RETURNS TABLE AS RETURN
(
 SELECT CONVERT(binary(32),HASHBYTES(''SHA2_256'',CONCAT(Members.Json,NCHAR(31),Fees.Json))) SourceHash
 FROM (SELECT (SELECT S.AdmissionId,S.StudentName,S.FatherName,S.Gender,S.AdmissionNumber,S.QuickReference,
   S.RegistrationNumber,S.Status,FM.FamilyId,Token.ReviewToken,
   JSON_QUERY((SELECT E.*,AY.YearName,AY.StartDate,AY.EndDate,C.ClassName,Sec.SectionName,CP.CampusName
     FROM dbo.Enrollments E WITH(HOLDLOCK)
     LEFT JOIN dbo.AcademicYears AY WITH(HOLDLOCK) ON AY.AcademicYearId=E.AcademicYearId
     LEFT JOIN dbo.CampusClassSections CCS WITH(HOLDLOCK) ON CCS.ClassSectionId=E.ClassSectionId
     LEFT JOIN dbo.Classes C WITH(HOLDLOCK) ON C.ClassId=CCS.ClassId
     LEFT JOIN dbo.Sections Sec WITH(HOLDLOCK) ON Sec.SectionId=CCS.SectionId
     LEFT JOIN dbo.Campuses CP WITH(HOLDLOCK) ON CP.CampusId=CCS.CampusId
     WHERE E.AdmissionId=S.AdmissionId ORDER BY E.EnrollmentId FOR JSON PATH,INCLUDE_NULL_VALUES)) Placements
   FROM dbo.Students S WITH(HOLDLOCK)
   LEFT JOIN dbo.FamilyMembers FM WITH(HOLDLOCK) ON FM.AdmissionId=S.AdmissionId
   CROSS APPLY dbo.CollectionReviewToken(S.AdmissionId) Token
   WHERE S.AdmissionId=@AdmissionId OR (@WholeFamily=1 AND FM.FamilyId IN
       (SELECT FamilyId FROM dbo.FamilyMembers WITH(HOLDLOCK) WHERE AdmissionId=@AdmissionId))
   ORDER BY S.AdmissionId FOR JSON PATH,INCLUDE_NULL_VALUES) Json) Members
 CROSS APPLY(SELECT (SELECT F.* FROM dbo.FeeTypes F WITH(HOLDLOCK)
   WHERE EXISTS(SELECT 1 FROM dbo.BillingCharges Charge WITH(HOLDLOCK)
    LEFT JOIN dbo.FamilyMembers FM WITH(HOLDLOCK) ON FM.AdmissionId=Charge.AdmissionId
    WHERE Charge.FeeTypeId=F.FeeTypeId AND (Charge.AdmissionId=@AdmissionId OR
      (@WholeFamily=1 AND FM.FamilyId IN(SELECT FamilyId FROM dbo.FamilyMembers WITH(HOLDLOCK) WHERE AdmissionId=@AdmissionId))))
   ORDER BY F.FeeTypeId FOR JSON PATH,INCLUDE_NULL_VALUES) Json) Fees
 WHERE @AdmissionId>0 AND @WholeFamily IS NOT NULL
  AND EXISTS(SELECT 1 FROM dbo.Students WITH(HOLDLOCK) WHERE AdmissionId=@AdmissionId)
);');

EXEC(N'CREATE OR ALTER FUNCTION msg.ReceiptSource(@ReceiptId uniqueidentifier,@AdmissionId int,@WholeFamily bit)
RETURNS TABLE AS RETURN
(
 SELECT R.CollectionReceiptId,R.ReceiptNumber,
   CONVERT(binary(32),HASHBYTES(''SHA2_256'',CONCAT(CONVERT(nvarchar(max),R.SnapshotJson),NCHAR(31),Ledger.Json))) SourceHash
 FROM dbo.CollectionReceipts R WITH(HOLDLOCK)
 CROSS APPLY(SELECT (
   SELECT P.FeePaymentId,P.AdmissionId,P.Status,P.TotalAmount,P.PaymentMethod,
     P.PaymentDate,P.IsCreditApplication,
     JSON_QUERY((SELECT A.FeePaymentId,A.Status,A.TotalAmount,A.PaymentMethod,A.PaymentDate
        FROM dbo.FeePayments A WITH(HOLDLOCK) WHERE A.RelatedFeePaymentId=P.FeePaymentId
        ORDER BY A.FeePaymentId FOR JSON PATH)) Adjustments
   FROM dbo.CollectionReceiptPayments Link WITH(HOLDLOCK)
   JOIN dbo.FeePayments P WITH(HOLDLOCK) ON P.FeePaymentId=Link.FeePaymentId
   WHERE Link.CollectionReceiptId=R.CollectionReceiptId AND (@WholeFamily=1 OR P.AdmissionId=@AdmissionId)
   ORDER BY P.FeePaymentId FOR JSON PATH) Json) Ledger
 WHERE R.CollectionReceiptId=@ReceiptId
 AND EXISTS(SELECT 1 FROM dbo.CollectionReceiptPayments L WITH(HOLDLOCK)
   JOIN dbo.FeePayments P WITH(HOLDLOCK) ON P.FeePaymentId=L.FeePaymentId
   WHERE L.CollectionReceiptId=R.CollectionReceiptId AND P.RelatedFeePaymentId IS NULL
     AND (@WholeFamily=1 OR P.AdmissionId=@AdmissionId))
 AND @AdmissionId>0 AND @WholeFamily IS NOT NULL AND R.PaymentMethod<>N''Fee changes''
 AND ((@WholeFamily=0 AND EXISTS(SELECT 1 FROM OPENJSON(R.SnapshotJson,''$.Students'') WITH(AdmissionId int) S WHERE S.AdmissionId=@AdmissionId))
   OR (@WholeFamily=1 AND (SELECT COUNT(*) FROM OPENJSON(R.SnapshotJson,''$.Students''))>1 AND NOT EXISTS(
      SELECT AdmissionId FROM OPENJSON(R.SnapshotJson,''$.Students'') WITH(AdmissionId int)
      EXCEPT SELECT Related.AdmissionId FROM dbo.FamilyMembers Anchor WITH(HOLDLOCK)
       JOIN dbo.FamilyMembers Related WITH(HOLDLOCK) ON Related.FamilyId=Anchor.FamilyId
       WHERE Anchor.AdmissionId=@AdmissionId)))
);');

EXEC(N'CREATE OR ALTER FUNCTION msg.FinancialSource(@SourceKind varchar(10),@ReceiptId uniqueidentifier,@AdmissionId int,@WholeFamily bit)
RETURNS TABLE AS RETURN
(
 SELECT Source.CollectionReceiptId,Source.ReceiptNumber,
  CONVERT(binary(32),HASHBYTES(''SHA2_256'',CONCAT(CONVERT(varchar(64),Source.SourceHash,2),NCHAR(31),Profile.Json))) SourceHash
 FROM (
  SELECT R.CollectionReceiptId,R.ReceiptNumber,R.SourceHash
   FROM msg.ReceiptSource(@ReceiptId,@AdmissionId,@WholeFamily) R WHERE @SourceKind=''Receipt''
  UNION ALL
  SELECT CONVERT(uniqueidentifier,NULL),CONVERT(nvarchar(60),N''''),B.SourceHash
   FROM msg.BillSource(@AdmissionId,@WholeFamily) B WHERE @SourceKind=''Bill''
 ) Source
 CROSS APPLY(SELECT (SELECT SchoolName,AddressLine,ContactNumber FROM dbo.SchoolProfile WITH(HOLDLOCK)
   ORDER BY SchoolProfileId FOR JSON PATH,INCLUDE_NULL_VALUES) Json) Profile
);');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadFinancialReviewSource
 @UserId int,@ReceiptNumber nvarchar(60)=NULL,@AdmissionId int,@WholeFamily bit,@SourceKind varchar(10)=''Receipt''
AS BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 EXEC dbo.RequirePermission @UserId,N''Students.View'';
 EXEC dbo.RequirePermission @UserId,N''Fees.View'';
 SELECT Source.ReceiptNumber,Source.CollectionReceiptId,Source.SourceHash
 FROM msg.FinancialSource(@SourceKind,(SELECT CollectionReceiptId FROM dbo.CollectionReceipts WHERE ReceiptNumber=@ReceiptNumber),@AdmissionId,@WholeFamily) Source;
END;');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReserveFinancialReview
 @UserId int,@ClientRequestId uniqueidentifier,@ReceiptNumber nvarchar(60)=NULL,
 @AdmissionId int,@WholeFamily bit,@SourceHash varbinary(32),@SourceKind varchar(10)=''Receipt''
AS BEGIN
 SET NOCOUNT ON;SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 EXEC dbo.RequirePermission @UserId,N''Students.View'';
 EXEC dbo.RequirePermission @UserId,N''Fees.View'';
 IF @ClientRequestId IS NULL OR @ClientRequestId=''00000000-0000-0000-0000-000000000000''
  OR @SourceHash IS NULL OR DATALENGTH(@SourceHash)<>32
  THROW 51330,''A reviewed financial document and stable request reference are required.'',1;
 BEGIN TRANSACTION;
 DECLARE @Source TABLE(ReceiptNumber nvarchar(60),CollectionReceiptId uniqueidentifier,SourceHash binary(32));
 INSERT @Source EXEC msg.ReadFinancialReviewSource @UserId,@ReceiptNumber,@AdmissionId,@WholeFamily,@SourceKind;
 DECLARE @ReceiptId uniqueidentifier=(SELECT CollectionReceiptId FROM @Source WHERE SourceHash=@SourceHash);
 IF NOT EXISTS(SELECT 1 FROM @Source WHERE SourceHash=@SourceHash) THROW 51330,''The bill or receipt changed. Review it again before sending.'',1;
 IF EXISTS(SELECT 1 FROM msg.FinancialReviewProof WITH(UPDLOCK,HOLDLOCK) WHERE ClientRequestId=@ClientRequestId)
 BEGIN
  IF NOT EXISTS(SELECT 1 FROM msg.FinancialReviewProof WHERE ClientRequestId=@ClientRequestId AND ReviewedBy=@UserId
    AND SourceKind=@SourceKind AND (CollectionReceiptId=@ReceiptId OR (CollectionReceiptId IS NULL AND @ReceiptId IS NULL)) AND AdmissionId=@AdmissionId AND WholeFamily=@WholeFamily AND SourceHash=@SourceHash)
   THROW 51331,''This request already belongs to a different financial review. Start a new review.'',1;
  UPDATE msg.FinancialReviewProof SET LastCheckedUtc=SYSUTCDATETIME() WHERE ClientRequestId=@ClientRequestId;
 END
 ELSE
  INSERT msg.FinancialReviewProof(ClientRequestId,SourceKind,CollectionReceiptId,AdmissionId,WholeFamily,SourceHash,ReviewedBy)
  VALUES(@ClientRequestId,@SourceKind,@ReceiptId,@AdmissionId,@WholeFamily,@SourceHash,@UserId);
 -- Unconfirmed, abandoned previews contain no sending evidence. Bound proofs
 -- have an FK from Batch and are never deleted by this bounded cleanup.
 DELETE TOP(100) P FROM msg.FinancialReviewProof P
 WHERE P.LastCheckedUtc<DATEADD(day,-7,SYSUTCDATETIME())
  AND NOT EXISTS(SELECT 1 FROM msg.Batch B WITH(HOLDLOCK) WHERE B.ClientRequestId=P.ClientRequestId);
 COMMIT;
END;');

EXEC(N'CREATE OR ALTER PROCEDURE msg.BindFinancialReview
 @UserId int,@ClientRequestId uniqueidentifier,@Required bit
AS BEGIN
 SET NOCOUNT ON;SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 -- Called within the ordinary reviewed confirmation transaction. This is
 -- source evidence only: it never changes accounting or starts dispatch.
 IF @@TRANCOUNT=0 OR @Required IS NULL THROW 51331,''Financial document binding requires the reviewed confirmation transaction.'',1;
 DECLARE @BatchId uniqueidentifier,@StudentId int,@Audience varchar(20),@ExistingKey uniqueidentifier;
 SELECT @BatchId=BatchId,@StudentId=StudentId,@Audience=Audience,@ExistingKey=FinancialReviewKey
 FROM msg.Batch WITH(UPDLOCK,HOLDLOCK) WHERE ClientRequestId=@ClientRequestId AND CreatedBy=@UserId;
 IF @BatchId IS NULL THROW 51331,''The confirmed financial message was not found.'',1;
 IF @Required=0
 BEGIN
  IF @ExistingKey IS NOT NULL OR EXISTS(SELECT 1 FROM msg.FinancialReviewProof WHERE ClientRequestId=@ClientRequestId)
   THROW 51331,''A financial review cannot become an unbound message.'',1;
  RETURN;
 END;
 EXEC dbo.RequirePermission @UserId,N''Students.View'';
 EXEC dbo.RequirePermission @UserId,N''Fees.View'';
 IF @Audience NOT IN (''FeeStudent'',''FeeFamily'') OR NOT EXISTS(
  SELECT 1 FROM msg.FinancialReviewProof P WITH(UPDLOCK,HOLDLOCK)
  CROSS APPLY msg.FinancialSource(P.SourceKind,P.CollectionReceiptId,P.AdmissionId,P.WholeFamily) Source
  WHERE P.ClientRequestId=@ClientRequestId AND P.ReviewedBy=@UserId
   AND P.AdmissionId=@StudentId AND P.WholeFamily=CASE WHEN @Audience=''FeeFamily'' THEN 1 ELSE 0 END
   AND P.SourceHash=Source.SourceHash)
  THROW 51330,''The reviewed financial document changed or belongs to another scope. Review it again; nothing was sent.'',1;
 UPDATE msg.Batch SET FinancialReviewKey=@ClientRequestId WHERE BatchId=@BatchId;
END;');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ConfirmReviewedManualBatch
    @UserId int,@ClientRequestId uniqueidentifier,@Channel varchar(10),
    @Purpose varchar(20),@Body nvarchar(1600),@Audience varchar(20),
    @CampusId int,@ClassId int,@StudentId int,@StaffId int,
    @Recipients msg.RecipientSelection READONLY,@ReviewedFees msg.RecipientSelection READONLY,
    @FinancialReviewRequired bit=0
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    BEGIN TRANSACTION;
    EXEC msg.ConfirmManualBatch @UserId=@UserId,@ClientRequestId=@ClientRequestId,@Channel=@Channel,
        @Purpose=@Purpose,@Body=@Body,@Audience=@Audience,@CampusId=@CampusId,
        @ClassId=@ClassId,@StudentId=@StudentId,@StaffId=@StaffId,@Recipients=@Recipients;
    IF @Audience IN (''OutstandingFees'',''FeeFamily'')
    BEGIN
        DECLARE @BatchId uniqueidentifier=(SELECT BatchId FROM msg.Batch WHERE ClientRequestId=@ClientRequestId);
        IF EXISTS(SELECT 1 FROM @ReviewedFees F WHERE F.RecipientKind<>''Student'' OR F.RecordId<=0
            OR NOT EXISTS(SELECT 1 FROM msg.Recipient R WHERE R.BatchId=@BatchId AND R.PhoneE164=F.PhoneE164))
            OR EXISTS(SELECT RecipientKind,RecordId,PhoneE164 FROM @Recipients
                EXCEPT SELECT RecipientKind,RecordId,PhoneE164 FROM @ReviewedFees)
            THROW 51120,''Fee reminder membership is incomplete. Review the recipients again.'',1;
        IF EXISTS(SELECT 1 FROM msg.ReviewedFeeStudent F JOIN msg.Recipient R ON R.RecipientId=F.RecipientId
            WHERE R.BatchId=@BatchId)
        BEGIN
            IF EXISTS(SELECT R.PhoneE164,F.AdmissionId FROM msg.ReviewedFeeStudent F
                JOIN msg.Recipient R ON R.RecipientId=F.RecipientId WHERE R.BatchId=@BatchId
                EXCEPT SELECT PhoneE164,RecordId FROM @ReviewedFees)
                OR EXISTS(SELECT PhoneE164,RecordId FROM @ReviewedFees
                EXCEPT SELECT R.PhoneE164,F.AdmissionId FROM msg.ReviewedFeeStudent F
                JOIN msg.Recipient R ON R.RecipientId=F.RecipientId WHERE R.BatchId=@BatchId)
                THROW 51120,''This confirmation already has a different fee review. Start a new review.'',1;
        END
        ELSE
        BEGIN
            DECLARE @LiveFees TABLE(PhoneE164 varchar(16),AdmissionId int PRIMARY KEY);
            INSERT @LiveFees(PhoneE164,AdmissionId)
            SELECT R.PhoneE164,S.AdmissionId
            FROM msg.Recipient R
            CROSS JOIN dbo.Students S WITH(HOLDLOCK)
            CROSS APPLY msg.RecordGuardian(S.AdmissionId) Guardian
            OUTER APPLY (
                SELECT TOP(1) E.ClassSectionId,E.Status
                FROM dbo.Enrollments E WITH(HOLDLOCK) WHERE E.AdmissionId=S.AdmissionId
                ORDER BY CASE WHEN E.Status=N''Active'' THEN 0 ELSE 1 END,E.EnrollmentId DESC
            ) E
            LEFT JOIN dbo.CampusClassSections CCS WITH(HOLDLOCK) ON CCS.ClassSectionId=E.ClassSectionId
            OUTER APPLY (
                SELECT SUM(dbo.GetFeeChargeBalance(C.BillingChargeId)) Outstanding
                FROM dbo.BillingCharges C WITH(HOLDLOCK)
                WHERE C.AdmissionId=S.AdmissionId AND C.Status<>N''Cancelled''
            ) F
            WHERE @Audience=''OutstandingFees'' AND Guardian.PhoneE164=R.PhoneE164 AND R.BatchId=@BatchId AND S.Status=N''Active'' AND Guardian.HasConsent=1
                AND E.Status=N''Active'' AND F.Outstanding>0
                AND (@CampusId=0 OR CCS.CampusId=@CampusId)
                AND (@ClassId=0 OR CCS.ClassId=@ClassId)
                AND (@StudentId=0 OR S.AdmissionId=@StudentId)
                AND NOT EXISTS(SELECT 1 FROM msg.Suppression O WITH(HOLDLOCK)
                    WHERE O.Channel=''WhatsApp'' AND O.PhoneE164=R.PhoneE164);
            IF @Audience=''FeeFamily''
                INSERT @LiveFees(PhoneE164,AdmissionId)
                SELECT R.PhoneE164,F.AdmissionId FROM msg.Recipient R
                CROSS APPLY msg.ActiveFamilyFeeMembers(@StudentId) F
                WHERE R.BatchId=@BatchId;
            IF EXISTS(SELECT PhoneE164,AdmissionId FROM @LiveFees EXCEPT SELECT PhoneE164,RecordId FROM @ReviewedFees)
                OR EXISTS(SELECT PhoneE164,RecordId FROM @ReviewedFees EXCEPT SELECT PhoneE164,AdmissionId FROM @LiveFees)
                THROW 51120,''Fee reminder membership changed. Preview the recipients again.'',1;
            INSERT msg.ReviewedFeeStudent(RecipientId,AdmissionId)
            SELECT R.RecipientId,F.RecordId FROM @ReviewedFees F
            JOIN msg.Recipient R ON R.BatchId=@BatchId AND R.PhoneE164=F.PhoneE164;
        END;
    END;
    EXEC msg.BindFinancialReview @UserId,@ClientRequestId,@FinancialReviewRequired;
    COMMIT TRANSACTION;
END;');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ConfirmReviewedApiBatch
 @UserId int,@ClientRequestId uniqueidentifier,@Channel varchar(10),@Purpose varchar(20),
 @Body nvarchar(1600),@Audience varchar(20),@CampusId int,@ClassId int,@StudentId int,@StaffId int,
 @Recipients msg.RecipientSelection READONLY,@ProviderCode varchar(40),@ProfileHash varchar(64),
 @ReviewJson nvarchar(max),@TemplateJson nvarchar(max)=NULL,@FinancialReviewRequired bit=0
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
 EXEC msg.BindFinancialReview @UserId,@ClientRequestId,@FinancialReviewRequired;
 SELECT * FROM @Result;
 COMMIT;
END');

EXEC(N'CREATE OR ALTER FUNCTION msg.PreparedEventEligible(@BatchId uniqueidentifier)
RETURNS TABLE AS RETURN
(
 SELECT CONVERT(bit,1) Eligible
 WHERE NOT EXISTS(
  SELECT 1 FROM msg.Batch B WITH(HOLDLOCK)
  JOIN msg.FinancialReviewProof P WITH(HOLDLOCK) ON P.ClientRequestId=B.FinancialReviewKey
  OUTER APPLY msg.FinancialSource(P.SourceKind,P.CollectionReceiptId,P.AdmissionId,P.WholeFamily) Source
  WHERE B.BatchId=@BatchId AND (Source.SourceHash IS NULL OR Source.SourceHash<>P.SourceHash))
 AND NOT EXISTS(
  SELECT 1 FROM msg.AutomationCandidate C WITH(HOLDLOCK)
  LEFT JOIN dbo.Students S WITH(HOLDLOCK) ON S.AdmissionId=C.AdmissionId
  LEFT JOIN dbo.FamilyMembers FM WITH(HOLDLOCK) ON FM.AdmissionId=C.AdmissionId
  CROSS APPLY(SELECT CONVERT(date,(SYSUTCDATETIME() AT TIME ZONE ''UTC'') AT TIME ZONE
    (SELECT TimeZoneId FROM msg.DispatchControl WHERE ControlId=1)) SchoolDate) Today
  WHERE C.BatchId=@BatchId AND C.State=''Prepared'' AND
   (S.AdmissionId IS NULL OR S.Status<>N''Active''
    OR EXISTS(SELECT 1 FROM msg.AutomationException X WITH(HOLDLOCK)
      WHERE X.EventCode=C.EventCode AND (X.AdmissionId=C.AdmissionId OR X.FamilyId=FM.FamilyId)
       AND X.RevokedUtc IS NULL AND (X.UntilUtc IS NULL OR X.UntilUtc>SYSUTCDATETIME()))
    OR (C.EventCode=''PaymentReceived'' AND NOT EXISTS(
      SELECT 1 FROM dbo.FeePayments P WITH(HOLDLOCK)
      WHERE P.FeePaymentId=C.SourceRecordId AND P.AdmissionId=C.AdmissionId
       AND P.Status=N''Completed'' AND P.RelatedFeePaymentId IS NULL
       AND NOT EXISTS(SELECT 1 FROM dbo.FeePayments A WITH(HOLDLOCK)
         WHERE A.RelatedFeePaymentId=P.FeePaymentId)))
    OR (C.EventCode=''Birthday'' AND
       (S.DateOfBirth IS NULL OR MONTH(S.DateOfBirth)<>MONTH(Today.SchoolDate)
        OR DAY(S.DateOfBirth)<>DAY(Today.SchoolDate)
        OR C.SourceKey<>CONCAT(''BD:'',YEAR(Today.SchoolDate),'':'',C.AdmissionId)))
    OR (C.EventCode IN (''OutstandingFees'',''UpcomingDue'',''OverdueFollowUp'')
       AND NOT EXISTS(SELECT 1 FROM msg.LiveFeeEventEligible(C.AdmissionId,C.EventCode,Today.SchoolDate)))))
);');

INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
VALUES(N'28',SYSDATETIME(),SUSER_SNAME(),N'Frozen source evidence for reviewed financial document communication and safe abandoned-preview cleanup');
COMMIT;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK;
 THROW;
END CATCH;
