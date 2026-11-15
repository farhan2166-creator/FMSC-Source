-- Resolve one contact for a saved student or their explicitly linked family.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF EXISTS(SELECT 1 FROM dbo.DatabaseVersion WHERE TRY_CONVERT(int,VersionNumber)=22) RETURN;
IF ISNULL((SELECT TOP(1) TRY_CONVERT(int,VersionNumber)
    FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0)<>21
    THROW 51000,'Family messaging requires the preceding migration.',1;

ALTER TABLE msg.Batch DROP CONSTRAINT CK_MsgBatchAudience;
ALTER TABLE msg.Batch WITH CHECK ADD CONSTRAINT CK_MsgBatchAudience CHECK
    (Audience IN ('School','Campus','Class','OutstandingFees','Student','Family','Staff','FeeStudent','FeeFamily'));

EXEC(N'CREATE FUNCTION msg.RecordGuardian(@AdmissionId int)
RETURNS TABLE
AS RETURN
(
    SELECT Anchor.AdmissionId,Chosen.PhoneE164,Consent.HasConsent
    FROM dbo.Students Anchor WITH(HOLDLOCK)
    LEFT JOIN dbo.FamilyMembers Link WITH(HOLDLOCK) ON Link.AdmissionId=Anchor.AdmissionId
    OUTER APPLY (
        SELECT CONVERT(bit,MAX(CONVERT(tinyint,S.SchoolMessageConsent))) HasConsent
        FROM dbo.Students S WITH(HOLDLOCK)
        LEFT JOIN dbo.FamilyMembers Related WITH(HOLDLOCK) ON Related.AdmissionId=S.AdmissionId
        WHERE S.Status=N''Active'' AND (S.AdmissionId=Anchor.AdmissionId OR Related.FamilyId=Link.FamilyId)
    ) Consent
    OUTER APPLY (
        SELECT TOP(1) Phone.PhoneE164
        FROM dbo.Students S WITH(HOLDLOCK)
        LEFT JOIN dbo.FamilyMembers Related WITH(HOLDLOCK) ON Related.AdmissionId=S.AdmissionId
        CROSS APPLY (VALUES(msg.NormalizeMobile(S.ContactNumber))) Phone(PhoneE164)
        CROSS APPLY (VALUES(LTRIM(RTRIM(S.AdmissionNumber)))) Number(TextValue)
        CROSS APPLY (VALUES(PATINDEX(N''%[^0-9]%'',COALESCE(Number.TextValue,N'''')+N''x''))) Suffix(StartAt)
        WHERE S.Status=N''Active'' AND (S.AdmissionId=Anchor.AdmissionId OR Related.FamilyId=Link.FamilyId)
            AND Phone.PhoneE164 IS NOT NULL
        ORDER BY TRY_CONVERT(bigint,LEFT(Number.TextValue,Suffix.StartAt-1)) DESC,
            SUBSTRING(Number.TextValue,Suffix.StartAt,40) DESC,S.AdmissionDate DESC,S.AdmissionId DESC
    ) Chosen
    WHERE Anchor.AdmissionId=@AdmissionId AND Anchor.Status=N''Active''
);');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ConfirmBatch
    @UserId int,
    @ClientRequestId uniqueidentifier,
    @Channel varchar(10),
    @Purpose varchar(20),
    @Body nvarchar(1600),
    @Audience varchar(20),
    @CampusId int=0,
    @ClassId int=0,
    @StudentId int=0,
    @StaffId int=0,
    @Recipients msg.RecipientSelection READONLY
AS
BEGIN
    SET NOCOUNT ON; SET XACT_ABORT ON;
    EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
    IF @Audience=N''Staff''
        EXEC dbo.RequirePermission @UserId,N''Staff.View'';
    ELSE
        EXEC dbo.RequirePermission @UserId,N''Students.View'';
    IF @Audience IN (''OutstandingFees'',''FeeStudent'',''FeeFamily'')
        EXEC dbo.RequirePermission @UserId,N''Fees.View'';
    IF @ClientRequestId IS NULL OR @Channel IS NULL OR @Purpose IS NULL OR @Audience IS NULL
       OR @Body IS NULL OR @Channel NOT IN (''Sms'',''WhatsApp'')
       OR @Purpose NOT IN (''Announcement'',''FeeReminder'',''Individual'',''Staff'')
       OR @Audience NOT IN (''School'',''Campus'',''Class'',''OutstandingFees'',''Student'',''Family'',''Staff'',''FeeStudent'',''FeeFamily'')
       OR LEN(LTRIM(RTRIM(ISNULL(@Body,N''''))))=0 OR LEN(@Body)>1600
       OR @Body LIKE N''%{{%'' OR @Body LIKE N''%}}%''
       OR (@Audience=''Campus'' AND @CampusId<=0)
       OR (@Audience=''Class'' AND (@CampusId<=0 OR @ClassId<=0))
       OR (@Audience IN (''Student'',''Family'',''FeeStudent'',''FeeFamily'') AND @StudentId<=0)
       OR (@Audience=''Staff'' AND @StaffId<=0)
       OR (@Audience IN (''OutstandingFees'',''FeeStudent'',''FeeFamily'') AND @Purpose<>''FeeReminder'')
       OR (@Audience=''Staff'' AND @Purpose<>''Staff'')
       OR (@Audience IN (''School'',''Campus'',''Class'') AND @Purpose<>''Announcement'')
       OR (@Audience IN (''Student'',''Family'') AND @Purpose<>''Individual'')
       THROW 51080,''Invalid communication target, purpose or message.'',1;
    IF NOT EXISTS(SELECT 1 FROM @Recipients)
       THROW 51081,''No eligible destinations remain. Review recipients again.'',1;
    IF EXISTS (SELECT 1 FROM @Recipients WHERE RecordId<=0 OR PhoneE164 NOT LIKE ''+923[0-6]%''
       OR LEN(PhoneE164)<>13)
       THROW 51082,''Invalid destination snapshot.'',1;
    IF EXISTS(SELECT PhoneE164 FROM @Recipients GROUP BY PhoneE164 HAVING COUNT(*)>1)
       THROW 51082,''Duplicate destinations are not permitted.'',1;

    BEGIN TRANSACTION;
    DECLARE @LockResult int, @Key nvarchar(100)=N''FMSC_msg_''+CONVERT(nvarchar(36),@ClientRequestId);
    EXEC @LockResult=sys.sp_getapplock
        @Resource=@Key,@LockMode=''Exclusive'',@LockOwner=''Transaction'',@LockTimeout=10000;
    IF @LockResult<0 THROW 51083,''Communication confirmation is busy. Retry the same request.'',1;
    DECLARE @OldBatch uniqueidentifier;
    SELECT @OldBatch=BatchId FROM msg.Batch WITH(UPDLOCK,HOLDLOCK)
    WHERE ClientRequestId=@ClientRequestId;
    IF @OldBatch IS NOT NULL
    BEGIN
        IF NOT EXISTS(SELECT 1 FROM msg.Batch WHERE BatchId=@OldBatch AND CreatedBy=@UserId
          AND Channel=@Channel AND Purpose=@Purpose AND Body=@Body
          AND Audience=@Audience AND CampusId=@CampusId AND ClassId=@ClassId
          AND StudentId=@StudentId AND StaffId=@StaffId)
            THROW 51084,''Confirmation key already belongs to a different request.'',1;
        SELECT @OldBatch BatchId,COUNT_BIG(*) RecipientCount,CONVERT(bit,1) AlreadyConfirmed
        FROM msg.Recipient WHERE BatchId=@OldBatch;
        COMMIT TRANSACTION; RETURN;
    END;

    IF NOT EXISTS(SELECT 1 FROM msg.ChannelSettings WITH(HOLDLOCK)
      WHERE Channel=@Channel AND Enabled=1 AND NULLIF(LTRIM(RTRIM(ProviderCode)),'''') IS NOT NULL)
        THROW 51085,''This channel is paused or has no approved provider.'',1;

    DECLARE @Live TABLE(
        RecipientKind varchar(8) NOT NULL, RecordId int NOT NULL,
        PhoneE164 varchar(16) NULL, RecordName nvarchar(250) NULL
    );
    IF @Audience=''Staff''
    BEGIN
        INSERT @Live(RecipientKind,RecordId,PhoneE164,RecordName)
        SELECT ''Staff'',S.StaffId,msg.NormalizeMobile(S.ContactNumber),S.StaffName
        FROM dbo.Staff S WITH(HOLDLOCK)
        WHERE S.StaffId=@StaffId AND S.Status=N''Active'';
    END
    ELSE IF @Audience IN (''Student'',''Family'',''FeeStudent'',''FeeFamily'')
    BEGIN
        INSERT @Live(RecipientKind,RecordId,PhoneE164,RecordName)
        SELECT ''Student'',G.AdmissionId,G.PhoneE164,S.StudentName
        FROM msg.RecordGuardian(@StudentId) G
        JOIN dbo.Students S WITH(HOLDLOCK) ON S.AdmissionId=G.AdmissionId
        WHERE G.HasConsent=1;
    END
    ELSE
    BEGIN
        INSERT @Live(RecipientKind,RecordId,PhoneE164,RecordName)
        SELECT ''Student'',S.AdmissionId,msg.NormalizeMobile(S.ContactNumber),S.StudentName
        FROM dbo.Students S WITH(HOLDLOCK)
        OUTER APPLY (
            SELECT TOP(1) E.ClassSectionId,E.Status
            FROM dbo.Enrollments E WHERE E.AdmissionId=S.AdmissionId
            ORDER BY CASE WHEN E.Status=N''Active'' THEN 0 ELSE 1 END,E.EnrollmentId DESC
        ) E
        LEFT JOIN dbo.CampusClassSections CCS ON CCS.ClassSectionId=E.ClassSectionId
        OUTER APPLY (
            SELECT SUM(dbo.GetFeeChargeBalance(BC.BillingChargeId)) Outstanding
            FROM dbo.BillingCharges BC
            WHERE @Audience=''OutstandingFees''
              AND BC.AdmissionId=S.AdmissionId AND BC.Status<>N''Cancelled''
        ) B
        WHERE S.Status=N''Active'' AND S.SchoolMessageConsent=1
          AND (
            @Audience=''School''
            OR (@Audience=''Campus'' AND E.Status=N''Active'' AND CCS.CampusId=@CampusId)
            OR (@Audience=''Class'' AND E.Status=N''Active'' AND CCS.CampusId=@CampusId AND CCS.ClassId=@ClassId)
            OR (@Audience=''OutstandingFees'' AND E.Status=N''Active''
                AND (@CampusId=0 OR CCS.CampusId=@CampusId)
                AND (@ClassId=0 OR CCS.ClassId=@ClassId)
                AND (@StudentId=0 OR S.AdmissionId=@StudentId) AND B.Outstanding>0)
          );
    END;
    DECLARE @Eligible TABLE(
        RecipientKind varchar(8) NOT NULL, RecordId int NOT NULL,
        PhoneE164 varchar(16) NOT NULL,
        PRIMARY KEY(RecipientKind,RecordId)
    );
    ;WITH Ranked AS (
        SELECT L.RecipientKind,L.RecordId,L.PhoneE164,
            ROW_NUMBER() OVER (PARTITION BY L.PhoneE164
               ORDER BY L.RecordName,L.RecordId) RankWithinPhone
        FROM @Live L
        WHERE L.PhoneE164 IS NOT NULL AND NOT EXISTS(
            SELECT 1 FROM msg.Suppression O WITH(HOLDLOCK)
            WHERE O.Channel=@Channel AND O.PhoneE164=L.PhoneE164)
    )
    INSERT @Eligible(RecipientKind,RecordId,PhoneE164)
    SELECT RecipientKind,RecordId,PhoneE164 FROM Ranked WHERE RankWithinPhone=1;
    -- Never send a partial or expanded audience when records changed after Preview.
    IF EXISTS(SELECT RecipientKind,RecordId,PhoneE164 FROM @Eligible
              EXCEPT SELECT RecipientKind,RecordId,PhoneE164 FROM @Recipients)
       OR EXISTS(SELECT RecipientKind,RecordId,PhoneE164 FROM @Recipients
                 EXCEPT SELECT RecipientKind,RecordId,PhoneE164 FROM @Eligible)
       THROW 51086,''Recipients changed since preview. Refresh before confirming.'',1;

    DECLARE @BatchId uniqueidentifier=NEWID();
    INSERT msg.Batch(BatchId,ClientRequestId,Channel,Purpose,Body,
        Audience,CampusId,ClassId,StudentId,StaffId,SingleStudentScope,State,
        CreatedBy,ConfirmedBy,ConfirmedUtc)
    VALUES(@BatchId,@ClientRequestId,@Channel,@Purpose,@Body,
        @Audience,@CampusId,@ClassId,@StudentId,@StaffId,CASE WHEN @Audience=''OutstandingFees'' AND @StudentId>0 THEN 1 ELSE 0 END,''Confirmed'',
        @UserId,@UserId,SYSUTCDATETIME());
    INSERT msg.Recipient(BatchId,RecipientKind,AdmissionId,StaffId,PhoneE164)
    SELECT @BatchId,RecipientKind,
        CASE WHEN RecipientKind=''Student'' THEN RecordId END,
        CASE WHEN RecipientKind=''Staff'' THEN RecordId END,PhoneE164
    FROM @Eligible;

    SELECT @BatchId BatchId,COUNT_BIG(*) RecipientCount,CONVERT(bit,0) AlreadyConfirmed
    FROM msg.Recipient WHERE BatchId=@BatchId;
    COMMIT TRANSACTION;
END;');

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

    DECLARE @Ordinal int;
    SELECT @Ordinal=COUNT(*)+1 FROM msg.Attempt WITH(HOLDLOCK) WHERE RecipientId=@RecipientId;
    IF @Ordinal>3 OR EXISTS(
        SELECT 1 FROM msg.Attempt WITH(HOLDLOCK)
        WHERE RecipientId=@RecipientId AND (Outcome<>''Failed'' OR RetryApprovedBy IS NULL))
        THROW 51093,''The prior attempt requires definitive failure and explicit retry approval.'',1;

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
                AND (@StudentId=0 OR S.AdmissionId=@StudentId) AND F.Outstanding>0));
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
        H.OpenCount,H.LastOpenedUtc,H.ReportedUtc,H.ShellHandoffUtc
    FROM msg.ManualHandoff H
    JOIN msg.Recipient R ON R.RecipientId=H.RecipientId
    LEFT JOIN dbo.Students S ON S.AdmissionId=R.AdmissionId
    LEFT JOIN dbo.Staff T ON T.StaffId=R.StaffId
    WHERE R.BatchId=@BatchId ORDER BY R.RecipientId;
END;');

EXEC(N'CREATE OR ALTER PROCEDURE msg.RecordShellHandoff @UserId int,@RecipientId bigint
AS
BEGIN
    SET NOCOUNT ON;
    EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
    DECLARE @Audience varchar(20);
    SELECT @Audience=B.Audience FROM msg.Recipient R
        JOIN msg.Batch B ON B.BatchId=R.BatchId WHERE R.RecipientId=@RecipientId;
    IF @Audience=''Staff'' EXEC dbo.RequirePermission @UserId,N''Staff.View'';
    ELSE EXEC dbo.RequirePermission @UserId,N''Students.View'';
    IF @Audience IN (''OutstandingFees'',''FeeStudent'',''FeeFamily'') EXEC dbo.RequirePermission @UserId,N''Fees.View'';
    UPDATE msg.ManualHandoff SET ShellHandoffUtc=SYSUTCDATETIME()
    WHERE RecipientId=@RecipientId AND State=''Opened'' AND ChangedBy=@UserId;
    IF @@ROWCOUNT<>1 THROW 51114,''The WhatsApp handoff changed. Review the current session.'',1;
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
    IF @Audience IN (''OutstandingFees'',''FeeStudent'',''FeeFamily'') EXEC dbo.RequirePermission @UserId,N''Fees.View'';
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
 R.RecipientId,R.RecipientKind,COALESCE(R.AdmissionId,R.StaffId) RecordId,R.PhoneE164,R.State,J.Text RenderedBody,
 COALESCE(S.StudentName,T.StaffName,N''School record'') RecipientName,
 LastAttempt.AttemptId,LastAttempt.ProviderMessageId,LastAttempt.FailureCode
 FROM msg.ApiBatch A JOIN msg.Batch B ON B.BatchId=A.BatchId
 JOIN msg.Recipient R ON R.BatchId=B.BatchId
 CROSS APPLY OPENJSON(A.ReviewJson) WITH(Kind varchar(8),RecordId int,Phone varchar(16),Text nvarchar(1600)) J
 LEFT JOIN dbo.Students S ON S.AdmissionId=R.AdmissionId
 LEFT JOIN dbo.Staff T ON T.StaffId=R.StaffId
 OUTER APPLY(SELECT TOP(1) AttemptId,ProviderMessageId,FailureCode FROM msg.Attempt X
  WHERE X.RecipientId=R.RecipientId ORDER BY X.AttemptNumber DESC) LastAttempt
 WHERE B.BatchId=@BatchId AND J.Kind=R.RecipientKind AND J.RecordId=COALESCE(R.AdmissionId,R.StaffId) AND J.Phone=R.PhoneE164 ORDER BY R.RecipientId;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.CancelPendingApiMessages @UserId int,@BatchId uniqueidentifier
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 BEGIN TRANSACTION;
 DECLARE @Owner int,@Audience varchar(20);
 SELECT @Owner=B.CreatedBy,@Audience=B.Audience FROM msg.Batch B WITH(UPDLOCK,HOLDLOCK)
  JOIN msg.ApiBatch A ON A.BatchId=B.BatchId WHERE B.BatchId=@BatchId;
 IF @Owner IS NULL THROW 51132,''A reviewed API batch is required.'',1;
 IF @Owner<>@UserId EXEC dbo.RequirePermission @UserId,N''Communication.View'';
 IF @Audience=''Staff'' EXEC dbo.RequirePermission @UserId,N''Staff.View'';
 ELSE EXEC dbo.RequirePermission @UserId,N''Students.View'';
 IF @Audience IN (''OutstandingFees'',''FeeStudent'',''FeeFamily'') EXEC dbo.RequirePermission @UserId,N''Fees.View'';
 -- Attempted and uncertain messages retain their evidence and state.
 UPDATE msg.Recipient SET State=''Excluded'',ExclusionCode=''OperatorCancelled'',UpdatedUtc=SYSUTCDATETIME()
 WHERE BatchId=@BatchId AND State=''Pending'';
 IF NOT EXISTS(SELECT 1 FROM msg.Recipient WHERE BatchId=@BatchId AND State IN (''Pending'',''Unknown'',''Submitted'',''Accepted''))
  UPDATE msg.Batch SET State=''Completed'' WHERE BatchId=@BatchId;
 COMMIT;
END');

INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
VALUES(N'22',SYSDATETIME(),SUSER_SNAME(),N'Resolve consent and a single guardian number across linked admissions');
