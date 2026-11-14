-- Preserve reviewed fee membership and distinguish preparation from shell handoff.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF EXISTS(SELECT 1 FROM dbo.DatabaseVersion WHERE TRY_CONVERT(int,VersionNumber)=19) RETURN;
IF ISNULL((SELECT TOP(1) TRY_CONVERT(int,VersionNumber)
    FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0)<>18
    THROW 51000,'Communication handoff changes require schema 18.',1;

CREATE TABLE msg.ReviewedFeeStudent
(
    RecipientId bigint NOT NULL REFERENCES msg.ManualHandoff(RecipientId),
    AdmissionId int NOT NULL,
    CONSTRAINT PK_MsgReviewedFeeStudent PRIMARY KEY(RecipientId,AdmissionId)
);
ALTER TABLE msg.ManualHandoff ADD ShellHandoffUtc datetime2(3) NULL;

EXEC(N'CREATE PROCEDURE msg.ConfirmReviewedManualBatch
    @UserId int,@ClientRequestId uniqueidentifier,@Channel varchar(10),
    @Purpose varchar(20),@Body nvarchar(1600),@Audience varchar(20),
    @CampusId int,@ClassId int,@StudentId int,@StaffId int,
    @Recipients msg.RecipientSelection READONLY,@ReviewedFees msg.RecipientSelection READONLY
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    BEGIN TRANSACTION;
    EXEC msg.ConfirmManualBatch @UserId=@UserId,@ClientRequestId=@ClientRequestId,@Channel=@Channel,
        @Purpose=@Purpose,@Body=@Body,@Audience=@Audience,@CampusId=@CampusId,
        @ClassId=@ClassId,@StudentId=@StudentId,@StaffId=@StaffId,@Recipients=@Recipients;
    IF @Audience=''OutstandingFees''
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
            JOIN dbo.Students S WITH(HOLDLOCK) ON msg.NormalizeMobile(S.ContactNumber)=R.PhoneE164
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
            WHERE R.BatchId=@BatchId AND S.Status=N''Active'' AND S.SchoolMessageConsent=1
                AND E.Status=N''Active'' AND F.Outstanding>0
                AND (@CampusId=0 OR CCS.CampusId=@CampusId)
                AND (@ClassId=0 OR CCS.ClassId=@ClassId)
                AND (@StudentId=0 OR S.AdmissionId=@StudentId)
                AND NOT EXISTS(SELECT 1 FROM msg.Suppression O WITH(HOLDLOCK)
                    WHERE O.Channel=''WhatsApp'' AND O.PhoneE164=R.PhoneE164);
            IF EXISTS(SELECT PhoneE164,AdmissionId FROM @LiveFees EXCEPT SELECT PhoneE164,RecordId FROM @ReviewedFees)
                OR EXISTS(SELECT PhoneE164,RecordId FROM @ReviewedFees EXCEPT SELECT PhoneE164,AdmissionId FROM @LiveFees)
                THROW 51120,''Fee reminder membership changed. Preview the recipients again.'',1;
            INSERT msg.ReviewedFeeStudent(RecipientId,AdmissionId)
            SELECT R.RecipientId,F.RecordId FROM @ReviewedFees F
            JOIN msg.Recipient R ON R.BatchId=@BatchId AND R.PhoneE164=F.PhoneE164;
        END;
    END;
    COMMIT TRANSACTION;
END;');

EXEC(N'CREATE PROCEDURE msg.RecordShellHandoff @UserId int,@RecipientId bigint
AS
BEGIN
    SET NOCOUNT ON;
    EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
    DECLARE @Audience varchar(20);
    SELECT @Audience=B.Audience FROM msg.Recipient R
        JOIN msg.Batch B ON B.BatchId=R.BatchId WHERE R.RecipientId=@RecipientId;
    IF @Audience=''Staff'' EXEC dbo.RequirePermission @UserId,N''Staff.View'';
    ELSE EXEC dbo.RequirePermission @UserId,N''Students.View'';
    IF @Audience=''OutstandingFees'' EXEC dbo.RequirePermission @UserId,N''Fees.View'';
    UPDATE msg.ManualHandoff SET ShellHandoffUtc=SYSUTCDATETIME()
    WHERE RecipientId=@RecipientId AND State=''Opened'' AND ChangedBy=@UserId;
    IF @@ROWCOUNT<>1 THROW 51114,''The WhatsApp handoff changed. Review the current session.'',1;
END;');

-- An operator can resume an owned batch without access to school-wide history.
EXEC(N'CREATE PROCEDURE msg.ReadMyUnfinishedBatches @UserId int
AS
BEGIN
    SET NOCOUNT ON;
    EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
    SELECT B.BatchId,B.Audience,B.CreatedUtc,COUNT_BIG(*) InProgress, CONVERT(varchar(40),''WhatsApp Business (manual)'') Method
    FROM msg.Batch B JOIN msg.ManualBatch M ON M.BatchId=B.BatchId
    JOIN msg.Recipient R ON R.BatchId=B.BatchId
    JOIN msg.ManualHandoff H ON H.RecipientId=R.RecipientId
    WHERE M.CreatedBy=@UserId AND H.State IN (''Pending'',''Opened'')
      AND B.State IN (''Confirmed'',''Sending'')
    GROUP BY B.BatchId,B.Audience,B.CreatedUtc ORDER BY B.CreatedUtc DESC,B.BatchId;
END;');

EXEC(N'CREATE PROCEDURE msg.RecordWhatsAppOptOut @UserId int,@Phone nvarchar(100)
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
    DECLARE @Number varchar(16)=msg.NormalizeMobile(@Phone);
    IF @Number IS NULL THROW 51120,''Enter a valid Pakistan mobile number.'',1;
    BEGIN TRANSACTION;
    IF NOT EXISTS(SELECT 1 FROM msg.Suppression WITH(UPDLOCK,HOLDLOCK)
        WHERE Channel=''WhatsApp'' AND PhoneE164=@Number)
        INSERT msg.Suppression(Channel,PhoneE164,Source,Reason)
        VALUES(''WhatsApp'',@Number,''OptOut'',N''Phone contact requested no WhatsApp messages.'');
    COMMIT TRANSACTION;
END;');

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
    IF @Audience=''OutstandingFees'' EXEC dbo.RequirePermission @UserId,N''Fees.View'';
    SELECT R.RecipientId,H.State,
        COALESCE(S.StudentName,T.StaffName,N''School record'') RecipientName,
        RIGHT(R.PhoneE164,4) PhoneLast4,
        H.OpenCount,H.LastOpenedUtc,H.ReportedUtc,H.ShellHandoffUtc
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
        @Body nvarchar(1600),@Excluded varchar(32);
    SELECT @BatchId=R.BatchId,@State=H.State,@Kind=R.RecipientKind,
        @AdmissionId=R.AdmissionId,@StaffId=R.StaffId,
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
    IF NOT EXISTS(SELECT 1 FROM msg.ChannelSettings WITH(HOLDLOCK)
       WHERE Channel=''WhatsApp'' AND ProviderCode=''WhatsAppBusinessManual'' AND Enabled=1)
        THROW 51114,''Free WhatsApp handoff is paused or no longer configured.'',1;
    IF @Kind=''Staff'' EXEC dbo.RequirePermission @UserId,N''Staff.View'';
    ELSE EXEC dbo.RequirePermission @UserId,N''Students.View'';
    IF @Audience=''OutstandingFees'' EXEC dbo.RequirePermission @UserId,N''Fees.View'';

    -- Source and scope checks mirror msg.ClaimRecipient. No snapshot is
    -- usable when admission consent, placement, phone or opt-out changes.
    IF @Kind=''Staff''
    BEGIN
        IF @Audience=''Staff'' AND @TargetStaff=@StaffId
            SELECT @Live=msg.NormalizeMobile(T.ContactNumber)
            FROM dbo.Staff T WITH(HOLDLOCK)
            WHERE T.StaffId=@StaffId AND T.Status=N''Active'';
    END
    ELSE
    BEGIN
        SELECT @Live=msg.NormalizeMobile(S.ContactNumber)
        FROM dbo.Students S WITH(HOLDLOCK)
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
          AND S.SchoolMessageConsent=1 AND
          (@Audience=''School''
            OR (@Audience=''Campus'' AND E.Status=N''Active'' AND CCS.CampusId=@CampusId)
            OR (@Audience=''Class'' AND E.Status=N''Active'' AND CCS.CampusId=@CampusId
                AND CCS.ClassId=@ClassId)
            OR (@Audience=''OutstandingFees'' AND E.Status=N''Active''
                AND (@CampusId=0 OR CCS.CampusId=@CampusId)
                AND (@ClassId=0 OR CCS.ClassId=@ClassId)
                AND (@StudentId=0 OR S.AdmissionId=@StudentId) AND F.Outstanding>0)
            OR (@Audience=''Student'' AND S.AdmissionId=@StudentId)
            OR (@Audience=''Family'' AND
                (S.AdmissionId=@StudentId OR EXISTS(
                 SELECT 1 FROM dbo.FamilyMembers FM WITH(HOLDLOCK)
                 WHERE FM.AdmissionId=S.AdmissionId AND FM.FamilyId IN (
                    SELECT X.FamilyId FROM dbo.FamilyMembers X WITH(HOLDLOCK)
                    WHERE X.AdmissionId=@StudentId)))));
    END;
    SET @Excluded=CASE
      WHEN @Live IS NULL THEN ''NoLongerEligible''
      WHEN @Live<>@Snapshot THEN ''DestinationChanged''
      WHEN EXISTS(SELECT 1 FROM msg.Suppression O WITH(HOLDLOCK)
                  WHERE O.Channel=''WhatsApp'' AND O.PhoneE164=@Live)
           THEN ''ChannelOptOut''
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
        CONVERT(varchar(32),NULL) ExclusionCode,@Audience Audience;
    COMMIT TRANSACTION;
END;');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadHistory @UserId int,@Limit int=100
AS
BEGIN
  SET NOCOUNT ON;
  EXEC dbo.RequirePermission @UserId,N''Communication.View'';
  IF @Limit<1 OR @Limit>200 THROW 51087,''History page size must be between 1 and 200.'',1;
  SELECT TOP(@Limit) B.BatchId,B.Channel,B.Purpose,B.State,
    B.CreatedUtc,B.ConfirmedUtc,B.CreatedBy,B.Audience,
    COALESCE(U.Username,CONVERT(nvarchar(30),B.CreatedBy)) OperatorName, COUNT(R.RecipientId) Destinations,
    SUM(CASE WHEN M.BatchId IS NULL AND R.State=''Delivered'' THEN 1 ELSE 0 END) Delivered,
    SUM(CASE WHEN M.BatchId IS NULL AND R.State=''Failed'' THEN 1 ELSE 0 END) Failed,
    SUM(CASE WHEN M.BatchId IS NULL AND R.State=''Unknown'' THEN 1 ELSE 0 END) Unknown,
    SUM(CASE WHEN M.BatchId IS NULL AND R.State IN (''Pending'',''Submitted'',''Accepted'')
       OR M.BatchId IS NOT NULL AND H.State IN (''Pending'',''Opened'') THEN 1 ELSE 0 END) InProgress,
    CASE WHEN M.BatchId IS NOT NULL THEN ''WhatsApp Business (manual)''
         ELSE ''Direct provider'' END Method,
    SUM(CASE WHEN H.State=''ReportedSent'' THEN 1 ELSE 0 END) ManualReported,
    SUM(CASE WHEN H.State=''Opened'' THEN 1 ELSE 0 END) ManualOpened,
    SUM(CASE WHEN H.State IN (''Skipped'',''Excluded'') THEN 1 ELSE 0 END) ManualSkipped
  FROM msg.Batch B LEFT JOIN dbo.Users U ON U.UserId=B.CreatedBy
  LEFT JOIN msg.Recipient R ON R.BatchId=B.BatchId
  LEFT JOIN msg.ManualBatch M ON M.BatchId=B.BatchId
  LEFT JOIN msg.ManualHandoff H ON H.RecipientId=R.RecipientId
  GROUP BY B.BatchId,B.Channel,B.Purpose,B.State,
      B.CreatedUtc,B.ConfirmedUtc,B.CreatedBy,B.Audience,U.Username,M.BatchId
  ORDER BY B.CreatedUtc DESC;
END;');

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
    IF @Audience=''OutstandingFees'' EXEC dbo.RequirePermission @UserId,N''Fees.View'';
    IF @State=''Opened'' AND @Outcome=''ReportedSent'' AND @ShellHandoffUtc IS NULL AND ISNULL(@RecoveredReport,0)=0
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

INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
VALUES(N'19',SYSDATETIME(),SUSER_SNAME(),N'Reviewed manual fee recipients and recoverable WhatsApp handoff');
