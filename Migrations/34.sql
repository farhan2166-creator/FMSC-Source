-- Shared preparation plans feed the existing reviewed batch/dispatch pipeline.
SET NOCOUNT ON; SET XACT_ABORT ON;
IF EXISTS(SELECT 1 FROM dbo.DatabaseVersion WHERE TRY_CONVERT(int,VersionNumber)=34) RETURN;
IF ISNULL((SELECT TOP(1) TRY_CONVERT(int,VersionNumber) FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0)<>33
 THROW 51000,'Communication planning requires the preceding migration.',1;
BEGIN TRY
 BEGIN TRANSACTION;
CREATE TABLE msg.RecurringRule(
 RuleId uniqueidentifier NOT NULL CONSTRAINT PK_MsgRecurringRule PRIMARY KEY,
 OriginBatchId uniqueidentifier NOT NULL CONSTRAINT FK_MsgRecurringOrigin REFERENCES msg.Batch(BatchId),
 CreatedBy int NOT NULL,CreatedUtc datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
 Frequency varchar(8) NOT NULL,Every int NOT NULL,FirstLocal datetime2(3) NOT NULL,TimeZoneId sysname NOT NULL,
 NextDueUtc datetime2(3) NOT NULL,EndUtc datetime2(3) NOT NULL,ExpiryHours int NOT NULL,
 MaxOccurrences int NOT NULL,Occurrences int NOT NULL DEFAULT(1),
 State varchar(10) NOT NULL DEFAULT('Active'),ReasonCode varchar(50) NULL,
 RestoreBoundaryHash binary(32) NULL,AcademicToken binary(32) NULL,
 UpdatedBy int NOT NULL,UpdatedUtc datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
 CONSTRAINT UQ_MsgRecurringOrigin UNIQUE(OriginBatchId),
 CONSTRAINT CK_MsgRecurringFrequency CHECK((Frequency='Days' AND Every BETWEEN 1 AND 365) OR
  (Frequency='Weeks' AND Every BETWEEN 1 AND 52) OR (Frequency='Months' AND Every BETWEEN 1 AND 12)),
 CONSTRAINT CK_MsgRecurringBounds CHECK(MaxOccurrences BETWEEN 2 AND 366 AND Occurrences BETWEEN 1 AND MaxOccurrences
  AND ExpiryHours BETWEEN 1 AND 168 AND EndUtc>CreatedUtc AND EndUtc<=DATEADD(day,367,CreatedUtc)),
 CONSTRAINT CK_MsgRecurringState CHECK(State IN('Active','Paused','Held','Completed','Cancelled'))
);
CREATE INDEX IX_MsgRecurringDue ON msg.RecurringRule(State,NextDueUtc);
CREATE TABLE msg.PlannedWork(
 WorkId uniqueidentifier NOT NULL CONSTRAINT PK_MsgPlannedWork PRIMARY KEY,
 SourceKind varchar(10) NOT NULL,RuleId uniqueidentifier NULL CONSTRAINT FK_MsgWorkRecurring REFERENCES msg.RecurringRule(RuleId),
 CandidateId bigint NULL CONSTRAINT FK_MsgWorkCandidate REFERENCES msg.AutomationCandidate(CandidateId),
 AuthorizationId uniqueidentifier NULL,
 DueUtc datetime2(3) NOT NULL,ExpiresUtc datetime2(3) NOT NULL,
 State varchar(10) NOT NULL DEFAULT('Pending'),ReasonCode varchar(50) NULL,
 BatchId uniqueidentifier NULL CONSTRAINT FK_MsgWorkBatch REFERENCES msg.Batch(BatchId),
 ClaimNonce uniqueidentifier NULL,ClaimUntilUtc datetime2(3) NULL,EnrollmentId uniqueidentifier NULL,
 CreatedUtc datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),UpdatedUtc datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
 CONSTRAINT CK_MsgWorkSource CHECK((SourceKind='Recurrence' AND RuleId IS NOT NULL AND CandidateId IS NULL AND AuthorizationId IS NULL)
  OR (SourceKind='Event' AND RuleId IS NULL AND CandidateId IS NOT NULL AND AuthorizationId IS NOT NULL)),
 CONSTRAINT CK_MsgWorkState CHECK(State IN('Pending','Preparing','Prepared','Held','Expired','Cancelled','Resolved')),
 CONSTRAINT CK_MsgWorkExpiry CHECK(ExpiresUtc>DueUtc),
 CONSTRAINT CK_MsgWorkBatch CHECK((State='Prepared' AND BatchId IS NOT NULL) OR State<>'Prepared')
);
CREATE UNIQUE INDEX UX_MsgWorkOccurrence ON msg.PlannedWork(RuleId,DueUtc) WHERE RuleId IS NOT NULL;
CREATE UNIQUE INDEX UX_MsgWorkEvent ON msg.PlannedWork(CandidateId) WHERE CandidateId IS NOT NULL;
CREATE UNIQUE INDEX UX_MsgWorkBatch ON msg.PlannedWork(BatchId) WHERE BatchId IS NOT NULL;
CREATE INDEX IX_MsgWorkPending ON msg.PlannedWork(State,DueUtc);
EXEC(N'CREATE OR ALTER FUNCTION msg.AcademicSessionToken()
RETURNS binary(32)
AS BEGIN
 DECLARE @Token binary(32);
 SELECT @Token=CONVERT(binary(32),HASHBYTES(''SHA2_256'',(SELECT AcademicYearId,StartDate,EndDate
 FROM dbo.AcademicYears WHERE IsCurrent=1 ORDER BY AcademicYearId FOR JSON PATH)))
 WHERE EXISTS(SELECT 1 FROM dbo.AcademicYears WHERE IsCurrent=1);
 RETURN @Token;
END;');

EXEC(N'CREATE OR ALTER FUNCTION msg.CanViewAudience(@UserId int,@Audience varchar(20))
RETURNS TABLE AS RETURN
(
 SELECT CONVERT(bit,1) Allowed WHERE
 EXISTS(SELECT 1 FROM dbo.Roles O WHERE O.RoleName=N''Administrator'' AND
   (EXISTS(SELECT 1 FROM dbo.Users U WHERE U.UserId=@UserId AND U.RoleId=O.RoleId)
    OR EXISTS(SELECT 1 FROM dbo.UserRoles UR WHERE UR.UserId=@UserId AND UR.RoleId=O.RoleId)))
 OR (EXISTS(SELECT 1 FROM dbo.RolePermissions RP JOIN dbo.Permissions P ON P.PermissionId=RP.PermissionId
   WHERE P.PermissionCode=CASE WHEN @Audience=''Staff'' THEN N''Staff.View'' ELSE N''Students.View'' END
    AND (EXISTS(SELECT 1 FROM dbo.Users U WHERE U.UserId=@UserId AND U.RoleId=RP.RoleId)
      OR EXISTS(SELECT 1 FROM dbo.UserRoles UR WHERE UR.UserId=@UserId AND UR.RoleId=RP.RoleId)))
  AND (@Audience NOT IN(''OutstandingFees'',''FeeStudent'',''FeeFamily'') OR EXISTS(
   SELECT 1 FROM dbo.RolePermissions RP JOIN dbo.Permissions P ON P.PermissionId=RP.PermissionId
   WHERE P.PermissionCode=N''Fees.View'' AND
    (EXISTS(SELECT 1 FROM dbo.Users U WHERE U.UserId=@UserId AND U.RoleId=RP.RoleId)
     OR EXISTS(SELECT 1 FROM dbo.UserRoles UR WHERE UR.UserId=@UserId AND UR.RoleId=RP.RoleId)))))
);');

EXEC(N'CREATE OR ALTER FUNCTION msg.RecurringEligible(@RuleId uniqueidentifier)
RETURNS TABLE AS RETURN
(
 SELECT R.RuleId FROM msg.RecurringRule R WITH(HOLDLOCK)
 JOIN msg.Batch B WITH(HOLDLOCK) ON B.BatchId=R.OriginBatchId
 WHERE R.RuleId=@RuleId AND R.State IN(''Active'',''Completed'')
 AND ((R.RestoreBoundaryHash=msg.RestoreBoundary()) OR
      (R.RestoreBoundaryHash IS NULL AND msg.RestoreBoundary() IS NULL))
 AND (B.ClassId=0 OR R.AcademicToken=msg.AcademicSessionToken())
);');

EXEC(N'CREATE OR ALTER FUNCTION msg.PlanBatchEligible(@BatchId uniqueidentifier)
RETURNS TABLE AS RETURN
(
 SELECT CONVERT(bit,1) Eligible WHERE NOT EXISTS(
 SELECT 1 FROM msg.PlannedWork W WITH(HOLDLOCK) WHERE W.BatchId=@BatchId AND
  (W.State<>''Prepared'' OR W.ExpiresUtc<=SYSUTCDATETIME() OR W.SourceKind<>''Recurrence''
    OR NOT EXISTS(SELECT 1 FROM msg.RecurringEligible(W.RuleId))))
);');

EXEC(N'CREATE OR ALTER PROCEDURE msg.VerifyPlanWorker
 @UserId int,@EnrollmentId uniqueidentifier,@WindowsSid nvarchar(185),@SecretHash varbinary(32),@LeaseNonce uniqueidentifier
AS BEGIN
 SET NOCOUNT ON;
 EXEC msg.VerifyBackgroundWorker @UserId,@EnrollmentId,@WindowsSid,@SecretHash,@ReturnState=0;
 IF NOT EXISTS(SELECT 1 FROM msg.BackgroundWorkerLease WITH(UPDLOCK,HOLDLOCK)
  WHERE LeaseId=1 AND EnrollmentId=@EnrollmentId AND LeaseNonce=@LeaseNonce AND LeaseUntilUtc>SYSUTCDATETIME())
  THROW 51263,''The school preparation lease is no longer owned by this processor.'',1;
END;');

EXEC(N'CREATE OR ALTER PROCEDURE msg.RegisterRecurrence
 @UserId int,@OriginBatchId uniqueidentifier,@Frequency varchar(8),@Every int,@FirstLocal datetime2(3),
 @TimeZoneId sysname,@NextDueUtc datetime2(3),@EndUtc datetime2(3),@ExpiryHours int,@MaxOccurrences int
AS BEGIN
 SET NOCOUNT ON;SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 BEGIN TRANSACTION;
 DECLARE @Request uniqueidentifier,@Due datetime2(3),@Class int;
 SELECT @Request=B.ClientRequestId,@Due=S.DueUtc,@Class=B.ClassId FROM msg.Batch B WITH(UPDLOCK,HOLDLOCK)
 JOIN msg.ApiBatch A ON A.BatchId=B.BatchId JOIN msg.BatchSchedule S WITH(UPDLOCK,HOLDLOCK) ON S.BatchId=B.BatchId
 WHERE B.BatchId=@OriginBatchId AND B.CreatedBy=@UserId AND B.State=''Confirmed'' AND B.FinancialReviewKey IS NULL;
 IF @Request IS NULL OR msg.BatchRecoveryBlocked(@OriginBatchId)=1
  THROW 51400,''Review a current dynamic API message before setting recurrence; historical bills and receipts cannot repeat.'',1;
 IF @Frequency IS NULL OR @Every IS NULL OR NOT((@Frequency=''Days'' AND @Every BETWEEN 1 AND 365) OR
   (@Frequency=''Weeks'' AND @Every BETWEEN 1 AND 52) OR (@Frequency=''Months'' AND @Every BETWEEN 1 AND 12))
  OR @FirstLocal IS NULL OR @TimeZoneId IS NULL OR @TimeZoneId<>(SELECT TimeZoneId FROM msg.DispatchControl WHERE ControlId=1)
  OR @EndUtc IS NULL OR @EndUtc<=@Due OR @EndUtc>DATEADD(day,366,SYSUTCDATETIME())
  OR @NextDueUtc IS NULL OR @NextDueUtc<=@Due OR @ExpiryHours IS NULL OR @ExpiryHours NOT BETWEEN 1 AND 168
  OR @MaxOccurrences IS NULL OR @MaxOccurrences NOT BETWEEN 2 AND 366
  THROW 51400,''Choose a valid bounded recurrence in the school timezone.'',1;
 IF ABS(DATEDIFF_BIG(millisecond,@FirstLocal,CONVERT(datetime2(3),@Due AT TIME ZONE ''UTC'' AT TIME ZONE @TimeZoneId)))>4
  THROW 51400,''The recurrence anchor must match the reviewed first sending time.'',1;
 IF @Class>0 AND msg.AcademicSessionToken() IS NULL THROW 51400,''A class recurrence requires the current academic year.'',1;
 IF EXISTS(SELECT 1 FROM msg.Attempt T JOIN msg.Recipient P ON P.RecipientId=T.RecipientId WHERE P.BatchId=@OriginBatchId)
  THROW 51400,''Recurrence must be authorized before the first attempt.'',1;
 IF EXISTS(SELECT 1 FROM msg.RecurringRule WHERE RuleId=@Request)
 BEGIN
  IF NOT EXISTS(SELECT 1 FROM msg.RecurringRule WHERE RuleId=@Request AND OriginBatchId=@OriginBatchId AND Frequency=@Frequency
   AND Every=@Every AND FirstLocal=@FirstLocal AND TimeZoneId=@TimeZoneId AND EndUtc=@EndUtc AND ExpiryHours=@ExpiryHours AND MaxOccurrences=@MaxOccurrences)
   THROW 51400,''This recurrence approval already belongs to different settings.'',1;
 END
 ELSE BEGIN
  INSERT msg.RecurringRule(RuleId,OriginBatchId,CreatedBy,Frequency,Every,FirstLocal,TimeZoneId,NextDueUtc,EndUtc,ExpiryHours,
    MaxOccurrences,RestoreBoundaryHash,AcademicToken,UpdatedBy)
  VALUES(@Request,@OriginBatchId,@UserId,@Frequency,@Every,@FirstLocal,@TimeZoneId,@NextDueUtc,@EndUtc,@ExpiryHours,
    @MaxOccurrences,msg.RestoreBoundary(),CASE WHEN @Class>0 THEN msg.AcademicSessionToken() END,@UserId);
  INSERT msg.PlannedWork(WorkId,SourceKind,RuleId,DueUtc,ExpiresUtc,State,BatchId)
  VALUES(NEWID(),''Recurrence'',@Request,@Due,DATEADD(hour,@ExpiryHours,@Due),''Prepared'',@OriginBatchId);
  UPDATE msg.BatchSchedule SET ExpiresUtc=DATEADD(hour,@ExpiryHours,@Due) WHERE BatchId=@OriginBatchId;
 END;
 SELECT @Request RuleId;
 COMMIT;
END;');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadDueRecurrences
 @UserId int,@EnrollmentId uniqueidentifier,@WindowsSid nvarchar(185),@SecretHash varbinary(32),@LeaseNonce uniqueidentifier
AS BEGIN
 SET NOCOUNT ON;SET XACT_ABORT ON;
 BEGIN TRANSACTION;
 EXEC msg.VerifyPlanWorker @UserId,@EnrollmentId,@WindowsSid,@SecretHash,@LeaseNonce;
 UPDATE R SET State=''Held'',ReasonCode=''RecoveryOrAcademicYear'',UpdatedUtc=SYSUTCDATETIME()
 FROM msg.RecurringRule R WHERE R.State=''Active'' AND NOT EXISTS(SELECT 1 FROM msg.RecurringEligible(R.RuleId));
 UPDATE S SET HoldForReview=1,UpdatedUtc=SYSUTCDATETIME()
 FROM msg.BatchSchedule S JOIN msg.PlannedWork W ON W.BatchId=S.BatchId JOIN msg.RecurringRule R ON R.RuleId=W.RuleId WHERE R.State=''Held'';
 UPDATE msg.RecurringRule SET State=''Completed'',UpdatedUtc=SYSUTCDATETIME()
 WHERE State=''Active'' AND (EndUtc<SYSUTCDATETIME() OR Occurrences>=MaxOccurrences OR NextDueUtc>EndUtc)
 AND NOT EXISTS(SELECT 1 FROM msg.PlannedWork W WHERE W.RuleId=msg.RecurringRule.RuleId AND
  (W.State IN(''Pending'',''Preparing'',''Held'') OR EXISTS(SELECT 1 FROM msg.Recipient P WHERE P.BatchId=W.BatchId
    AND (P.State IN(''Unknown'',''Submitted'') OR (P.State=''Pending'' AND W.ExpiresUtc>SYSUTCDATETIME())))));
 -- Expired unattempted work may stop blocking the next meaningful occurrence.
 UPDATE W SET State=''Expired'',ReasonCode=''Expired'',UpdatedUtc=SYSUTCDATETIME()
 FROM msg.PlannedWork W WHERE W.State IN(''Pending'',''Preparing'') AND W.ExpiresUtc<=SYSUTCDATETIME();
 UPDATE P SET State=''Cancelled'',UpdatedUtc=SYSUTCDATETIME()
 FROM msg.Recipient P JOIN msg.PlannedWork W ON W.BatchId=P.BatchId
 WHERE W.SourceKind=''Recurrence'' AND W.ExpiresUtc<=SYSUTCDATETIME() AND P.State=''Pending'';
 SELECT TOP(20) R.* FROM msg.RecurringRule R WHERE R.State=''Active'' AND R.NextDueUtc<=SYSUTCDATETIME()
 AND R.NextDueUtc<=R.EndUtc AND R.Occurrences<R.MaxOccurrences
 AND NOT EXISTS(SELECT 1 FROM msg.PlannedWork W WHERE W.RuleId=R.RuleId AND
  (W.State IN(''Pending'',''Preparing'',''Held'') OR EXISTS(SELECT 1 FROM msg.Recipient P WHERE P.BatchId=W.BatchId
   AND (P.State IN(''Unknown'',''Submitted'') OR (P.State=''Pending'' AND W.ExpiresUtc>SYSUTCDATETIME())))))
 ORDER BY R.NextDueUtc,R.RuleId;
 COMMIT;
END;');

EXEC(N'CREATE OR ALTER PROCEDURE msg.CreateRecurringOccurrence
 @UserId int,@RuleId uniqueidentifier,@ExpectedDueUtc datetime2(3),@NextDueUtc datetime2(3),
 @EnrollmentId uniqueidentifier,@WindowsSid nvarchar(185),@SecretHash varbinary(32),@LeaseNonce uniqueidentifier
AS BEGIN
 SET NOCOUNT ON;SET XACT_ABORT ON;
 BEGIN TRANSACTION;
 EXEC msg.VerifyPlanWorker @UserId,@EnrollmentId,@WindowsSid,@SecretHash,@LeaseNonce;
 IF EXISTS(SELECT 1 FROM msg.PlannedWork WHERE RuleId=@RuleId AND DueUtc=@ExpectedDueUtc)
 BEGIN COMMIT;RETURN;END;
 DECLARE @Due datetime2(3),@Expiry int,@End datetime2(3);
 SELECT @Due=NextDueUtc,@Expiry=ExpiryHours,@End=EndUtc FROM msg.RecurringRule WITH(UPDLOCK,HOLDLOCK)
 WHERE RuleId=@RuleId AND State=''Active'' AND Occurrences<MaxOccurrences AND NextDueUtc<=SYSUTCDATETIME();
 IF @Due IS NULL OR @Due<>@ExpectedDueUtc OR NOT EXISTS(SELECT 1 FROM msg.RecurringEligible(@RuleId))
  THROW 51401,''The recurring rule changed or needs review.'',1;
 IF @NextDueUtc IS NULL OR @NextDueUtc<=SYSUTCDATETIME() OR @NextDueUtc<=@Due
  THROW 51401,''The next occurrence must be in the future; historical replay is forbidden.'',1;
 IF NOT EXISTS(SELECT 1 FROM msg.PlannedWork WITH(UPDLOCK,HOLDLOCK) WHERE RuleId=@RuleId AND DueUtc=@Due)
 BEGIN
  INSERT msg.PlannedWork(WorkId,SourceKind,RuleId,DueUtc,ExpiresUtc,State,ReasonCode)
  VALUES(NEWID(),''Recurrence'',@RuleId,@Due,DATEADD(hour,@Expiry,@Due),
   CASE WHEN DATEADD(hour,@Expiry,@Due)<=SYSUTCDATETIME() THEN ''Expired'' ELSE ''Pending'' END,
   CASE WHEN DATEADD(hour,@Expiry,@Due)<=SYSUTCDATETIME() THEN ''Expired'' END);
  UPDATE msg.RecurringRule SET NextDueUtc=@NextDueUtc,Occurrences=Occurrences+1,
   UpdatedUtc=SYSUTCDATETIME() WHERE RuleId=@RuleId;
 END;
 COMMIT;
END;');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ClaimPlannedWork
 @UserId int,@ClaimNonce uniqueidentifier,@EnrollmentId uniqueidentifier,@WindowsSid nvarchar(185),@SecretHash varbinary(32),@LeaseNonce uniqueidentifier
AS BEGIN
 SET NOCOUNT ON;SET XACT_ABORT ON;
 BEGIN TRANSACTION;
 EXEC msg.VerifyPlanWorker @UserId,@EnrollmentId,@WindowsSid,@SecretHash,@LeaseNonce;
 IF @ClaimNonce IS NULL THROW 51401,''A durable preparation identity is required.'',1;
 DECLARE @Work uniqueidentifier;
 SELECT TOP(1) @Work=W.WorkId FROM msg.PlannedWork W WITH(UPDLOCK,HOLDLOCK)
 WHERE W.SourceKind=''Recurrence'' AND W.State IN(''Pending'',''Preparing'')
 AND (W.State=''Pending'' OR W.ClaimUntilUtc<SYSUTCDATETIME()) AND W.DueUtc<=SYSUTCDATETIME() AND W.ExpiresUtc>SYSUTCDATETIME()
 AND EXISTS(SELECT 1 FROM msg.RecurringEligible(W.RuleId)) ORDER BY W.DueUtc,W.WorkId;
 IF @Work IS NOT NULL
 BEGIN
  UPDATE msg.PlannedWork SET State=''Preparing'',ClaimNonce=@ClaimNonce,ClaimUntilUtc=DATEADD(minute,2,SYSUTCDATETIME()),
    EnrollmentId=@EnrollmentId,UpdatedUtc=SYSUTCDATETIME() WHERE WorkId=@Work;
  SELECT W.WorkId,W.ClaimNonce,W.DueUtc,W.ExpiresUtc,W.SourceKind,W.RuleId,
   B.Audience,B.CampusId,B.ClassId,B.StudentId,B.StaffId,B.Channel,B.Body,R.CreatedBy AuthorizedBy,
   A.ProviderCode,A.ProfileHash,A.TemplateJson,S.Priority
  FROM msg.PlannedWork W JOIN msg.RecurringRule R ON R.RuleId=W.RuleId
  JOIN msg.Batch B ON B.BatchId=R.OriginBatchId JOIN msg.ApiBatch A ON A.BatchId=B.BatchId
  JOIN msg.BatchSchedule S ON S.BatchId=B.BatchId WHERE W.WorkId=@Work;
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

EXEC(N'CREATE OR ALTER PROCEDURE msg.HoldPlannedWork
 @UserId int,@WorkId uniqueidentifier,@ClaimNonce uniqueidentifier,@ReasonCode varchar(50),
 @EnrollmentId uniqueidentifier,@WindowsSid nvarchar(185),@SecretHash varbinary(32),@LeaseNonce uniqueidentifier
AS BEGIN
 SET NOCOUNT ON;
 EXEC msg.VerifyPlanWorker @UserId,@EnrollmentId,@WindowsSid,@SecretHash,@LeaseNonce;
 IF @ReasonCode NOT IN(''SourceChanged'',''ProviderChanged'',''PreparationFailed'',''NoEligibleRecipients'',''AmbiguousTime'')
  THROW 51401,''Use a supported preparation hold reason.'',1;
 UPDATE msg.PlannedWork SET State=CASE WHEN @ReasonCode=''NoEligibleRecipients'' THEN ''Resolved'' ELSE ''Held'' END,
  ReasonCode=@ReasonCode,UpdatedUtc=SYSUTCDATETIME()
 WHERE WorkId=@WorkId AND State=''Preparing'' AND ClaimNonce=@ClaimNonce AND EnrollmentId=@EnrollmentId;
 IF @@ROWCOUNT=1 AND @ReasonCode<>''NoEligibleRecipients''
  UPDATE R SET State=''Held'',ReasonCode=@ReasonCode,UpdatedUtc=SYSUTCDATETIME()
  FROM msg.RecurringRule R JOIN msg.PlannedWork W ON W.RuleId=R.RuleId WHERE W.WorkId=@WorkId AND R.State=''Active'';
END;');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadRecurringRules @UserId int
AS BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 SELECT TOP(200) R.RuleId,R.CreatedUtc,R.CreatedBy,R.Frequency,R.Every,R.FirstLocal,R.TimeZoneId,R.NextDueUtc,
 R.EndUtc,R.ExpiryHours,R.MaxOccurrences,R.Occurrences,R.State,R.ReasonCode,Student.StudentName,Staff.StaffName,B.Audience,B.CampusId,B.ClassId,B.StudentId,B.StaffId,B.Channel,B.Body,
 CONVERT(datetime2(3),R.NextDueUtc AT TIME ZONE ''UTC'' AT TIME ZONE R.TimeZoneId) NextSchool,
 A.ProviderCode,A.ProfileHash,A.TemplateJson,
 (SELECT COUNT_BIG(*) FROM msg.Recipient P JOIN msg.PlannedWork W ON W.BatchId=P.BatchId WHERE W.RuleId=R.RuleId AND P.State IN(''Unknown'',''Submitted'')) UncertainAttempts,
 (SELECT COUNT_BIG(*) FROM msg.PlannedWork W WHERE W.RuleId=R.RuleId AND W.State=''Held'') HeldOccurrences
 FROM msg.RecurringRule R JOIN msg.Batch B ON B.BatchId=R.OriginBatchId JOIN msg.ApiBatch A ON A.BatchId=B.BatchId
 LEFT JOIN dbo.Students Student ON Student.AdmissionId=B.StudentId LEFT JOIN dbo.Staff Staff ON Staff.StaffId=B.StaffId
 WHERE EXISTS(SELECT 1 FROM msg.CanViewAudience(@UserId,B.Audience)) AND (R.CreatedBy=@UserId OR EXISTS(SELECT 1 FROM dbo.UserRoles UR JOIN dbo.Roles O ON O.RoleId=UR.RoleId WHERE UR.UserId=@UserId AND O.RoleName=N''Administrator'')
 OR EXISTS(SELECT 1 FROM dbo.Users U JOIN dbo.Roles O ON O.RoleId=U.RoleId WHERE U.UserId=@UserId AND O.RoleName=N''Administrator''))
 ORDER BY R.CreatedUtc DESC;
END;');

EXEC(N'CREATE OR ALTER PROCEDURE msg.SetRecurringState @UserId int,@RuleId uniqueidentifier,@State varchar(10),@NextDueUtc datetime2(3)=NULL
AS BEGIN
 SET NOCOUNT ON;SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 IF @State NOT IN(''Active'',''Paused'',''Cancelled'') THROW 51400,''Choose a supported recurring action.'',1;
 BEGIN TRANSACTION;
 DECLARE @Owner int,@Origin uniqueidentifier,@Current varchar(10),@End datetime2(3),@Count int,@Max int;
 SELECT @Owner=CreatedBy,@Origin=OriginBatchId,@Current=State,@End=EndUtc,@Count=Occurrences,@Max=MaxOccurrences
 FROM msg.RecurringRule WITH(UPDLOCK,HOLDLOCK) WHERE RuleId=@RuleId;
 IF @Owner IS NULL THROW 51400,''The recurring rule does not exist.'',1;
 IF NOT EXISTS(SELECT 1 FROM msg.Batch B CROSS APPLY msg.CanViewAudience(@UserId,B.Audience) Scope WHERE B.BatchId=@Origin)
  THROW 50003,''School record permission is required for this recurring audience.'',1;
 IF @Owner<>@UserId EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 IF @State=''Active'' BEGIN
  IF @Current IN(''Cancelled'',''Completed'') OR @Count>=@Max OR @End<=SYSUTCDATETIME() OR msg.BatchRecoveryBlocked(@Origin)=1
   THROW 51400,''Review a new recurrence; this approval is completed, cancelled or predates recovery.'',1;
  IF EXISTS(SELECT 1 FROM msg.Batch WHERE BatchId=@Origin AND ClassId>0)
   AND NOT EXISTS(SELECT 1 FROM msg.RecurringRule WHERE RuleId=@RuleId AND AcademicToken=msg.AcademicSessionToken())
   THROW 51400,''The academic year changed. Review this class audience in a new recurrence.'',1;
  IF EXISTS(SELECT 1 FROM msg.PlannedWork W JOIN msg.Recipient P ON P.BatchId=W.BatchId WHERE W.RuleId=@RuleId AND P.State IN(''Unknown'',''Submitted''))
   THROW 51400,''Reconcile uncertain submissions before resuming this recurrence.'',1;
  IF @NextDueUtc IS NULL OR @NextDueUtc<=SYSUTCDATETIME() OR @NextDueUtc>@End
   THROW 51400,''Choose the next future occurrence within the approved end date.'',1;
 END;
 UPDATE msg.RecurringRule SET State=@State,NextDueUtc=COALESCE(@NextDueUtc,NextDueUtc),ReasonCode=NULL,UpdatedBy=@UserId,UpdatedUtc=SYSUTCDATETIME() WHERE RuleId=@RuleId;
 IF @State=''Active'' BEGIN
  UPDATE msg.PlannedWork SET State=CASE WHEN ExpiresUtc<=SYSUTCDATETIME() THEN ''Expired'' ELSE ''Pending'' END,
    ReasonCode=NULL,ClaimNonce=NULL,ClaimUntilUtc=NULL,UpdatedUtc=SYSUTCDATETIME()
  WHERE RuleId=@RuleId AND State=''Held'' AND BatchId IS NULL;
  UPDATE S SET HoldForReview=0,UpdatedBy=@UserId,UpdatedUtc=SYSUTCDATETIME() FROM msg.BatchSchedule S
  JOIN msg.PlannedWork W ON W.BatchId=S.BatchId WHERE W.RuleId=@RuleId AND W.State=''Prepared'' AND W.ExpiresUtc>SYSUTCDATETIME()
   AND msg.BatchRecoveryBlocked(S.BatchId)=0;
 END;
 IF @State<>''Active'' BEGIN
  UPDATE S SET HoldForReview=1,UpdatedBy=@UserId,UpdatedUtc=SYSUTCDATETIME() FROM msg.BatchSchedule S
  JOIN msg.PlannedWork W ON W.BatchId=S.BatchId WHERE W.RuleId=@RuleId;
  IF @State=''Cancelled'' BEGIN
   UPDATE P SET State=''Cancelled'',UpdatedUtc=SYSUTCDATETIME() FROM msg.Recipient P
   JOIN msg.PlannedWork W ON W.BatchId=P.BatchId WHERE W.RuleId=@RuleId AND P.State=''Pending'';
   UPDATE msg.PlannedWork SET State=''Cancelled'',UpdatedUtc=SYSUTCDATETIME() WHERE RuleId=@RuleId AND State IN(''Pending'',''Preparing'',''Held'');
  END;
 END;
 COMMIT;
END;');

EXEC(N'CREATE OR ALTER PROCEDURE msg.HoldRecurringPreparation
 @UserId int,@RuleId uniqueidentifier,@EnrollmentId uniqueidentifier,@WindowsSid nvarchar(185),@SecretHash varbinary(32),@LeaseNonce uniqueidentifier
AS BEGIN
 SET NOCOUNT ON;
 EXEC msg.VerifyPlanWorker @UserId,@EnrollmentId,@WindowsSid,@SecretHash,@LeaseNonce;
 UPDATE msg.RecurringRule SET State=''Held'',ReasonCode=''AmbiguousTime'',UpdatedUtc=SYSUTCDATETIME() WHERE RuleId=@RuleId AND State=''Active'';
END;');

EXEC(N'CREATE OR ALTER FUNCTION msg.PlannedFeeAllowed(@AdmissionId int,@BatchId uniqueidentifier)
RETURNS TABLE AS RETURN
(
 SELECT CONVERT(bit,1) Allowed FROM msg.AutomationRule Policy CROSS JOIN msg.DispatchControl D
 WHERE Policy.EventCode=''OutstandingFees'' AND D.ControlId=1
 AND NOT EXISTS(SELECT 1 FROM msg.Attempt T WITH(HOLDLOCK) JOIN msg.Recipient P ON P.RecipientId=T.RecipientId
   JOIN msg.Batch B ON B.BatchId=P.BatchId WHERE B.Purpose=''FeeReminder''
   AND (P.AdmissionId=@AdmissionId OR EXISTS(SELECT 1 FROM dbo.FamilyMembers A JOIN dbo.FamilyMembers F ON F.FamilyId=A.FamilyId
        WHERE A.AdmissionId=@AdmissionId AND F.AdmissionId=P.AdmissionId))
   AND T.RequestUtc>DATEADD(day,-Policy.FeeReminderGapDays,SYSUTCDATETIME()))
 AND (SELECT COUNT_BIG(*) FROM msg.Attempt T WITH(HOLDLOCK) JOIN msg.Recipient P ON P.RecipientId=T.RecipientId
   JOIN msg.Batch B ON B.BatchId=P.BatchId WHERE B.Purpose=''FeeReminder''
   AND (P.AdmissionId=@AdmissionId OR EXISTS(SELECT 1 FROM dbo.FamilyMembers A JOIN dbo.FamilyMembers F ON F.FamilyId=A.FamilyId
        WHERE A.AdmissionId=@AdmissionId AND F.AdmissionId=P.AdmissionId))
   AND YEAR(CONVERT(date,T.RequestUtc AT TIME ZONE ''UTC'' AT TIME ZONE D.TimeZoneId))=YEAR(CONVERT(date,SYSUTCDATETIME() AT TIME ZONE ''UTC'' AT TIME ZONE D.TimeZoneId))
   AND MONTH(CONVERT(date,T.RequestUtc AT TIME ZONE ''UTC'' AT TIME ZONE D.TimeZoneId))=MONTH(CONVERT(date,SYSUTCDATETIME() AT TIME ZONE ''UTC'' AT TIME ZONE D.TimeZoneId)))<Policy.FeeRemindersPerMonth
);');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReadUnpreparedWork @UserId int
AS BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 SELECT TOP(200) R.RuleId QueueId,R.RuleId PlanRuleId,''Recurrence'' SourceRowKind,B.Channel,B.Audience,R.CreatedUtc,
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
 ORDER BY R.CreatedUtc DESC;
END;');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ConfirmReviewedApiBatch
 @UserId int,@ClientRequestId uniqueidentifier,@Channel varchar(10),@Purpose varchar(20),
 @Body nvarchar(1600),@Audience varchar(20),@CampusId int,@ClassId int,@StudentId int,@StaffId int,
 @Recipients msg.RecipientSelection READONLY,@ProviderCode varchar(40),@ProfileHash varchar(64),
 @ReviewJson nvarchar(max),@TemplateJson nvarchar(max)=NULL,@FinancialReviewRequired bit=0,
 @PlannedWorkId uniqueidentifier=NULL,@WorkClaim uniqueidentifier=NULL,
 @EnrollmentId uniqueidentifier=NULL,@WindowsSid nvarchar(185)=NULL,@SecretHash varbinary(32)=NULL,@LeaseNonce uniqueidentifier=NULL
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
 IF @PlannedWorkId IS NOT NULL
  EXEC msg.BindPlannedWork @UserId,@BatchId,@PlannedWorkId,@WorkClaim,@EnrollmentId,@WindowsSid,@SecretHash,@LeaseNonce;
 ELSE IF @WorkClaim IS NOT NULL OR @EnrollmentId IS NOT NULL OR @WindowsSid IS NOT NULL OR @SecretHash IS NOT NULL OR @LeaseNonce IS NOT NULL
  THROW 51401,''Unattended preparation requires a complete approved work item.'',1;

 SELECT * FROM @Result;
 COMMIT;
END');

EXEC(N'CREATE OR ALTER FUNCTION msg.PreparedEventEligible(@BatchId uniqueidentifier)
RETURNS TABLE AS RETURN
(
 SELECT CONVERT(bit,1) Eligible
 WHERE EXISTS(SELECT 1 FROM msg.PlanBatchEligible(@BatchId))
 AND NOT EXISTS(
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
    OR NOT EXISTS(SELECT 1 FROM msg.AutomationRecordInScope(C.AdmissionId,C.EventCode))
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

    IF @Purpose=''FeeReminder'' AND EXISTS(SELECT 1 FROM msg.PlannedWork WHERE BatchId=@BatchId)
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


EXEC(N'CREATE OR ALTER TRIGGER msg.TR_RecurringIntentImmutable ON msg.RecurringRule AFTER UPDATE
AS BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT RuleId,OriginBatchId,CreatedBy,Frequency,Every,FirstLocal,TimeZoneId,EndUtc,ExpiryHours,MaxOccurrences,RestoreBoundaryHash,AcademicToken FROM inserted
  EXCEPT SELECT RuleId,OriginBatchId,CreatedBy,Frequency,Every,FirstLocal,TimeZoneId,EndUtc,ExpiryHours,MaxOccurrences,RestoreBoundaryHash,AcademicToken FROM deleted)
  THROW 51400,''Recurring content, audience and authorization are immutable; review a new rule.'',1;
 IF EXISTS(SELECT 1 FROM inserted I JOIN deleted D ON D.RuleId=I.RuleId
   WHERE I.Occurrences<D.Occurrences OR (D.State=''Cancelled'' AND I.State<>''Cancelled''))
  THROW 51400,''A recurrence cannot regain consumed capacity or reopen after cancellation.'',1;

END;');
EXEC(N'CREATE OR ALTER TRIGGER msg.TR_PlannedWorkSourceImmutable ON msg.PlannedWork AFTER UPDATE
AS BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT WorkId,SourceKind,RuleId,CandidateId,AuthorizationId,DueUtc,ExpiresUtc FROM inserted
  EXCEPT SELECT WorkId,SourceKind,RuleId,CandidateId,AuthorizationId,DueUtc,ExpiresUtc FROM deleted)
  OR EXISTS(SELECT 1 FROM inserted I JOIN deleted D ON D.WorkId=I.WorkId WHERE D.BatchId IS NOT NULL AND (I.BatchId IS NULL OR I.BatchId<>D.BatchId))
  THROW 51401,''Prepared source identity and an existing batch link are immutable.'',1;
END;');
INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
VALUES(N'34',SYSDATETIME(),SUSER_SNAME(),N'Bounded recurring approvals and shared atomic preparation into the existing communication Queue');
 COMMIT;
END TRY
BEGIN CATCH
 IF @@TRANCOUNT>0 ROLLBACK;
 THROW;
END CATCH;
