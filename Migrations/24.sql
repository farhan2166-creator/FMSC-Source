-- Opt-in authorized background queue worker infrastructure.
-- No provider request occurs during migration; all schedules held during enrollment.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF EXISTS(SELECT 1 FROM dbo.DatabaseVersion WHERE TRY_CONVERT(int,VersionNumber)=24) RETURN;
IF ISNULL((SELECT TOP(1) TRY_CONVERT(int,VersionNumber)
 FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0)<>23
 THROW 51280,'Background processor requires schema 23.',1;

CREATE TABLE msg.BackgroundWorkerEnrollment (
 EnrollmentId uniqueidentifier NOT NULL CONSTRAINT PK_MsgWorkerEnrollment PRIMARY KEY,
 UserId int NOT NULL,
 WindowsSid nvarchar(185) NOT NULL,
 SecretHash varbinary(32) NOT NULL,
 Enabled bit NOT NULL CONSTRAINT DF_MsgWorkerEnabled DEFAULT(1),
 EnrolledUtc datetime2(3) NOT NULL CONSTRAINT DF_MsgWorkerEnrolled DEFAULT SYSUTCDATETIME(),
 RevokedUtc datetime2(3) NULL,
 CONSTRAINT FK_MsgWorkerUser FOREIGN KEY(UserId) REFERENCES dbo.Users(UserId),
 CONSTRAINT CK_MsgWorkerSecret CHECK(DATALENGTH(SecretHash)=32),
 CONSTRAINT CK_MsgWorkerSid CHECK(WindowsSid LIKE N'S-1-%')
);
CREATE UNIQUE INDEX UX_MsgWorkerSid ON msg.BackgroundWorkerEnrollment(WindowsSid)
 WHERE Enabled=1;

CREATE TABLE msg.BackgroundWorkerLease (
 LeaseId tinyint NOT NULL CONSTRAINT PK_MsgWorkerLease PRIMARY KEY,
 EnrollmentId uniqueidentifier NULL,
 LeaseNonce uniqueidentifier NULL,
 LeaseUntilUtc datetime2(3) NOT NULL,
 UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_MsgWorkerLeaseUpdated DEFAULT SYSUTCDATETIME(),
 CONSTRAINT CK_MsgWorkerLeaseId CHECK(LeaseId=1),
 CONSTRAINT CK_MsgWorkerLeasePair CHECK((EnrollmentId IS NULL AND LeaseNonce IS NULL) OR
 (EnrollmentId IS NOT NULL AND LeaseNonce IS NOT NULL))
);
INSERT msg.BackgroundWorkerLease(LeaseId,LeaseUntilUtc) VALUES(1,CONVERT(datetime2(3),'20000101'));

CREATE TABLE msg.NonWorkingWeekday(
 IsoWeekday tinyint NOT NULL CONSTRAINT PK_MsgWorkingWeekday PRIMARY KEY,
 Enabled bit NOT NULL,
 CONSTRAINT CK_MsgWeekdayRange CHECK(IsoWeekday BETWEEN 1 AND 7)
);
INSERT msg.NonWorkingWeekday(IsoWeekday,Enabled)
 VALUES(1,1),(2,1),(3,1),(4,1),(5,1),(6,1),(7,0);
CREATE TABLE msg.SchoolHoliday(
 HolidayDate date NOT NULL CONSTRAINT PK_MsgHoliday PRIMARY KEY,
 Description nvarchar(150) NOT NULL,
 UpdatedBy int NOT NULL,
 UpdatedUtc datetime2(3) NOT NULL CONSTRAINT DF_MsgHolidayUpdated DEFAULT SYSUTCDATETIME()
);
EXEC(N'CREATE OR ALTER PROCEDURE msg.RegisterBackgroundWorker
 @UserId int,@EnrollmentId uniqueidentifier,@WindowsSid nvarchar(185),@SecretHash varbinary(32)
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 IF NOT EXISTS(SELECT 1 FROM dbo.Users U JOIN dbo.Roles O ON O.RoleId=U.RoleId
   WHERE U.UserId=@UserId AND O.RoleName=N''Administrator'')
   THROW 51260,''An active school administrator must authorize background sending.'',1;
 IF @EnrollmentId IS NULL OR @EnrollmentId=''00000000-0000-0000-0000-000000000000''
    OR @WindowsSid NOT LIKE N''S-1-%'' OR LEN(@WindowsSid)>185
    OR DATALENGTH(@SecretHash)<>32
    THROW 51260,''Invalid background processor enrollment.'',1;
 BEGIN TRANSACTION;
 UPDATE msg.BackgroundWorkerEnrollment
 SET Enabled=0,RevokedUtc=SYSUTCDATETIME()
 WHERE WindowsSid=@WindowsSid AND Enabled=1;
 INSERT msg.BackgroundWorkerEnrollment(EnrollmentId,UserId,WindowsSid,SecretHash)
 VALUES(@EnrollmentId,@UserId,@WindowsSid,@SecretHash);
 -- Existing schedules may originate before the verified enrollment (including
 -- after a previous restore). Never start dispatching them silently.
 UPDATE S SET HoldForReview=1,UpdatedBy=@UserId,UpdatedUtc=SYSUTCDATETIME()
 FROM msg.BatchSchedule S JOIN msg.Batch B ON B.BatchId=S.BatchId
 WHERE B.CreatedUtc<SYSUTCDATETIME() AND
       EXISTS(SELECT 1 FROM msg.Recipient R WHERE R.BatchId=S.BatchId AND R.State=''Pending'');
 UPDATE msg.DispatchControl SET UnattendedEnabled=0,UpdatedBy=@UserId,
  UpdatedUtc=SYSUTCDATETIME() WHERE ControlId=1;
 COMMIT;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.VerifyBackgroundWorker
 @UserId int,@EnrollmentId uniqueidentifier,@WindowsSid nvarchar(185),@SecretHash varbinary(32)
AS
BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 DECLARE @Created datetime2(3);
 SELECT @Created=EnrolledUtc FROM msg.BackgroundWorkerEnrollment WITH(HOLDLOCK)
 WHERE EnrollmentId=@EnrollmentId AND UserId=@UserId AND WindowsSid=@WindowsSid
 AND SecretHash=@SecretHash AND Enabled=1;
 IF @Created IS NULL
  THROW 51261,''Background worker authorization expired or was revoked.'',1;
 IF NOT EXISTS(SELECT 1 FROM dbo.Users U JOIN dbo.Roles O ON O.RoleId=U.RoleId
     WHERE U.UserId=@UserId AND O.RoleName=N''Administrator'')
  THROW 51261,''Background worker school administrator access was revoked.'',1;
 IF NOT EXISTS(SELECT 1 FROM msg.DispatchControl
  WHERE ControlId=1 AND UnattendedEnabled=1 AND MasterPaused=0)
  THROW 51261,''Unattended sending is not currently enabled.'',1;
 -- SQL Server keeps restorehistory outside the restored school database.
 -- If the school was restored since enrollment, require fresh administrator
 -- approval. If restore history cannot be verified, fail closed.
 DECLARE @LastRestore datetime;
 BEGIN TRY
  SELECT @LastRestore=MAX(restore_date) FROM msdb.dbo.restorehistory
    WHERE destination_database_name=DB_NAME();
 END TRY
 BEGIN CATCH
  THROW 51262,''Database restore history could not be verified. Unattended sending is blocked.'',1;
 END CATCH;
 IF @LastRestore IS NOT NULL AND @LastRestore>@Created
  THROW 51262,''This school database was restored after worker enrollment. Reauthorize sending after review.'',1;
 SELECT CONVERT(bit,1) Authorized,
  ISNULL((SELECT MAX(AttemptId) FROM msg.Attempt),CONVERT(bigint,0)) MaxAttemptId;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.AcquireBackgroundLease
 @UserId int,@EnrollmentId uniqueidentifier,@WindowsSid nvarchar(185),
 @SecretHash varbinary(32),@LeaseNonce uniqueidentifier
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC msg.VerifyBackgroundWorker @UserId,@EnrollmentId,@WindowsSid,@SecretHash;
 IF @LeaseNonce IS NULL OR @LeaseNonce=''00000000-0000-0000-0000-000000000000''
   THROW 51263,''A unique worker lease token is required.'',1;
 BEGIN TRANSACTION;
 UPDATE msg.BackgroundWorkerLease WITH(UPDLOCK,HOLDLOCK)
 SET EnrollmentId=@EnrollmentId,LeaseNonce=@LeaseNonce,
     LeaseUntilUtc=DATEADD(second,120,SYSUTCDATETIME()),
     UpdatedUtc=SYSUTCDATETIME()
 WHERE LeaseId=1 AND LeaseUntilUtc<=SYSUTCDATETIME();
 DECLARE @Acquired bit=CONVERT(bit,CASE WHEN @@ROWCOUNT=1 THEN 1 ELSE 0 END);
 COMMIT;
 SELECT @Acquired Acquired;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.RenewBackgroundLease
 @UserId int,@EnrollmentId uniqueidentifier,@WindowsSid nvarchar(185),
 @SecretHash varbinary(32),@LeaseNonce uniqueidentifier
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC msg.VerifyBackgroundWorker @UserId,@EnrollmentId,@WindowsSid,@SecretHash;
 UPDATE msg.BackgroundWorkerLease WITH(UPDLOCK,HOLDLOCK)
  SET LeaseUntilUtc=DATEADD(second,120,SYSUTCDATETIME()),UpdatedUtc=SYSUTCDATETIME()
 WHERE LeaseId=1 AND EnrollmentId=@EnrollmentId AND LeaseNonce=@LeaseNonce
   AND LeaseUntilUtc>SYSUTCDATETIME();
 IF @@ROWCOUNT<>1 THROW 51263,''This background worker no longer owns the school lease.'',1;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReleaseBackgroundLease
 @UserId int,@EnrollmentId uniqueidentifier,@LeaseNonce uniqueidentifier
AS
BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Send'';
 UPDATE msg.BackgroundWorkerLease SET EnrollmentId=NULL,LeaseNonce=NULL,
  LeaseUntilUtc=CONVERT(datetime2(3),''20000101''),
  UpdatedUtc=SYSUTCDATETIME()
 WHERE LeaseId=1 AND EnrollmentId=@EnrollmentId AND LeaseNonce=@LeaseNonce;
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
 SELECT TOP(1) R.RecipientId,B.BatchId,B.Channel,B.CreatedBy,S.Priority
 FROM msg.BatchSchedule S
 JOIN msg.Batch B ON B.BatchId=S.BatchId
 JOIN msg.ApiBatch A ON A.BatchId=B.BatchId
 JOIN msg.Recipient R ON R.BatchId=B.BatchId
 WHERE S.HoldForReview=0 AND S.DueUtc<=SYSUTCDATETIME()
   AND (S.ExpiresUtc IS NULL OR S.ExpiresUtc>SYSUTCDATETIME())
   AND B.State IN (''Confirmed'',''Sending'') AND R.State=''Pending''
 ORDER BY CASE S.Priority WHEN ''High'' THEN 0 WHEN ''Normal'' THEN 1 ELSE 2 END,
          S.DueUtc,R.RecipientId;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.HoldScheduledBatch
 @UserId int,@BatchId uniqueidentifier
AS
BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 UPDATE msg.BatchSchedule SET HoldForReview=1,
 UpdatedBy=@UserId,UpdatedUtc=SYSUTCDATETIME()
 WHERE BatchId=@BatchId;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReportWorkerStateDivergence
 @UserId int,@EnrollmentId uniqueidentifier,@LeaseNonce uniqueidentifier
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 IF NOT EXISTS(SELECT 1 FROM msg.BackgroundWorkerLease WITH(HOLDLOCK)
 WHERE LeaseId=1 AND EnrollmentId=@EnrollmentId AND LeaseNonce=@LeaseNonce
 AND LeaseUntilUtc>SYSUTCDATETIME())
 THROW 51263,''Only the active worker may report a restore mismatch.'',1;
 UPDATE msg.DispatchControl SET MasterPaused=1,UpdatedUtc=SYSUTCDATETIME(),
 UpdatedBy=@UserId WHERE ControlId=1;
 UPDATE msg.BatchSchedule SET HoldForReview=1,
 UpdatedBy=@UserId,UpdatedUtc=SYSUTCDATETIME()
 WHERE EXISTS(SELECT 1 FROM msg.Recipient R
 WHERE R.BatchId=msg.BatchSchedule.BatchId AND R.State=''Pending'');
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.SetUnattendedSending
 @UserId int,@Enabled bit
AS
BEGIN
 SET NOCOUNT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 IF @Enabled IS NULL THROW 51264,''Choose an unattended sending state.'',1;
 IF @Enabled=1 AND NOT EXISTS(SELECT 1 FROM msg.BackgroundWorkerEnrollment
   WHERE UserId=@UserId AND Enabled=1)
   THROW 51264,''Enroll the school background processor before enabling sending.'',1;
 UPDATE msg.DispatchControl SET UnattendedEnabled=@Enabled,
 UpdatedBy=@UserId,UpdatedUtc=SYSUTCDATETIME() WHERE ControlId=1;
END');

EXEC(N'CREATE OR ALTER PROCEDURE msg.ReleaseScheduledHold
 @UserId int,@BatchId uniqueidentifier
AS
BEGIN
 SET NOCOUNT ON; SET XACT_ABORT ON;
 EXEC dbo.RequirePermission @UserId,N''Communication.Configure'';
 IF NOT EXISTS(SELECT 1 FROM msg.BatchSchedule S JOIN msg.ApiBatch A ON A.BatchId=S.BatchId
 WHERE S.BatchId=@BatchId) THROW 51265,''Only a reviewed API schedule can be released.'',1;
 UPDATE msg.BatchSchedule SET HoldForReview=0,UpdatedBy=@UserId,
 UpdatedUtc=SYSUTCDATETIME() WHERE BatchId=@BatchId;
END');

INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
VALUES(N'24',SYSDATETIME(),SUSER_SNAME(),N'Opt-in authorized communication background worker and SQL lease');
