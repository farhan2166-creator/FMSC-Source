-- Communication audit corrections. The installer owns the verified backup
-- and outer transaction. Existing batches, consent and financial data are retained.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF EXISTS(SELECT 1 FROM dbo.DatabaseVersion WHERE TRY_CONVERT(int,VersionNumber)=18) RETURN;
IF ISNULL((SELECT TOP(1) TRY_CONVERT(int,VersionNumber)
    FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0)<>17
    THROW 51000,'Communication corrections require the preceding migration.',1;
IF OBJECT_ID(N'msg.OpenManualHandoff',N'P') IS NULL
    OR OBJECT_ID(N'msg.NormalizeMobile',N'FN') IS NULL
    THROW 51000,'Required communication objects are missing. Inspect before upgrading.',1;

-- Existing fee batches used StudentId only as unused UI context. Preserve
-- their original bulk audience; only new confirmations opt into single-student scope.
ALTER TABLE msg.Batch ADD SingleStudentScope bit NOT NULL
    CONSTRAINT DF_MsgBatchSingleStudentScope DEFAULT(0);

EXEC(N'CREATE OR ALTER FUNCTION msg.NormalizeMobile(@Value nvarchar(100))
RETURNS varchar(16)
AS
BEGIN
    -- Match the Admission/C# mobile rules, including balanced parentheses,
    -- plus placement and the Unicode whitespace admitted by char.IsWhiteSpace.
    IF @Value IS NULL RETURN NULL;
    DECLARE @Digits varchar(100)='''',@I int=1,@Code int,@C nchar(1),
        @First bit=1,@Plus bit=0,@Depth int=0;
    WHILE @I<=DATALENGTH(@Value)/2
    BEGIN
        SET @C=SUBSTRING(@Value,@I,1); SET @Code=UNICODE(@C); SET @I+=1;
        IF @Code BETWEEN 9 AND 13 OR @Code IN (32,133,160,5760,8232,8233,8239,8287,12288)
            OR @Code BETWEEN 8192 AND 8202 CONTINUE;
        IF @Code=43
        BEGIN
            IF @First=0 OR @Plus=1 RETURN NULL;
            SET @Plus=1;
        END
        ELSE IF @Code BETWEEN 48 AND 57 SET @Digits+=CONVERT(char(1),@C);
        ELSE IF @Code=40
        BEGIN SET @Depth+=1; IF @Depth>1 RETURN NULL; END
        ELSE IF @Code=41
        BEGIN SET @Depth-=1; IF @Depth<0 RETURN NULL; END
        ELSE IF @Code<>45 RETURN NULL;
        SET @First=0;
    END;
    IF @Depth<>0 RETURN NULL;
    IF @Plus=1
    BEGIN
        IF LEFT(@Digits,2)<>''92'' RETURN NULL;
        SET @Digits=SUBSTRING(@Digits,3,100);
    END
    ELSE IF LEFT(@Digits,4)=''0092'' SET @Digits=SUBSTRING(@Digits,5,100);
    ELSE IF LEFT(@Digits,1)=''0'' SET @Digits=SUBSTRING(@Digits,2,100);
    ELSE RETURN NULL;
    IF LEN(@Digits)<>10 OR LEFT(@Digits,1)<>''3''
       OR SUBSTRING(@Digits,2,1) NOT BETWEEN ''0'' AND ''6'' RETURN NULL;
    RETURN ''+92''+@Digits;
END;');

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
    IF @Audience=N''OutstandingFees''
        EXEC dbo.RequirePermission @UserId,N''Fees.View'';
    IF @ClientRequestId IS NULL OR @Channel IS NULL OR @Purpose IS NULL OR @Audience IS NULL
       OR @Body IS NULL OR @Channel NOT IN (''Sms'',''WhatsApp'')
       OR @Purpose NOT IN (''Announcement'',''FeeReminder'',''Individual'',''Staff'')
       OR @Audience NOT IN (''School'',''Campus'',''Class'',''OutstandingFees'',''Student'',''Family'',''Staff'')
       OR LEN(LTRIM(RTRIM(ISNULL(@Body,N''''))))=0 OR LEN(@Body)>1600
       OR @Body LIKE N''%{{%'' OR @Body LIKE N''%}}%''
       OR (@Audience=''Campus'' AND @CampusId<=0)
       OR (@Audience=''Class'' AND (@CampusId<=0 OR @ClassId<=0))
       OR (@Audience IN (''Student'',''Family'') AND @StudentId<=0)
       OR (@Audience=''Staff'' AND @StaffId<=0)
       OR (@Audience=''OutstandingFees'' AND @Purpose<>''FeeReminder'')
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
            OR (@Audience=''Student'' AND S.AdmissionId=@StudentId)
            OR (@Audience=''Family'' AND
                (S.AdmissionId=@StudentId OR EXISTS (
                    SELECT 1 FROM dbo.FamilyMembers FM
                    WHERE FM.AdmissionId=S.AdmissionId AND FM.FamilyId IN
                        (SELECT FM2.FamilyId FROM dbo.FamilyMembers FM2 WHERE FM2.AdmissionId=@StudentId))))
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
    IF @Audience=''OutstandingFees''
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
                AND (@StudentId=0 OR S.AdmissionId=@StudentId) AND F.Outstanding>0)
            OR (@Audience=''Student'' AND S.AdmissionId=@StudentId)
            OR (@Audience=''Family'' AND
                (S.AdmissionId=@StudentId OR EXISTS(
                    SELECT 1 FROM dbo.FamilyMembers FM WITH(HOLDLOCK)
                    WHERE FM.AdmissionId=S.AdmissionId AND FM.FamilyId IN (
                        SELECT X.FamilyId FROM dbo.FamilyMembers X WITH(HOLDLOCK)
                        WHERE X.AdmissionId=@StudentId)))));
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
          CONVERT(nvarchar(1600),NULL) Body,@Excluded ExclusionCode;
        COMMIT TRANSACTION; RETURN;
    END;
    UPDATE msg.ManualHandoff SET State=''Opened'',OpenCount=OpenCount+1,
        LastOpenedUtc=SYSUTCDATETIME(),ChangedBy=@UserId,UpdatedUtc=SYSUTCDATETIME()
    WHERE RecipientId=@RecipientId;
    SELECT CONVERT(bit,1) Opened,@Live PhoneE164,@Body Body,
        CONVERT(varchar(32),NULL) ExclusionCode;
    COMMIT TRANSACTION;
END;');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadHistory @UserId int,@Limit int=100
AS
BEGIN
  SET NOCOUNT ON;
  EXEC dbo.RequirePermission @UserId,N''Communication.View'';
  IF @Limit<1 OR @Limit>200 THROW 51087,''History page size must be between 1 and 200.'',1;
  SELECT TOP(@Limit) B.BatchId,B.Channel,B.Purpose,B.State,
    B.CreatedUtc,B.ConfirmedUtc,B.CreatedBy,
    COUNT(R.RecipientId) Destinations,
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
  FROM msg.Batch B
  LEFT JOIN msg.Recipient R ON R.BatchId=B.BatchId
  LEFT JOIN msg.ManualBatch M ON M.BatchId=B.BatchId
  LEFT JOIN msg.ManualHandoff H ON H.RecipientId=R.RecipientId
  GROUP BY B.BatchId,B.Channel,B.Purpose,B.State,
      B.CreatedUtc,B.ConfirmedUtc,B.CreatedBy,M.BatchId
  ORDER BY B.CreatedUtc DESC;
END;');

INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
VALUES(N'18',SYSDATETIME(),SUSER_SNAME(),N'Align communication eligibility, selected-student fee notices and manual completion history');
