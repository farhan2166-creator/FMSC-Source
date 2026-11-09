-- Schema 12: bind emergency Administrator recovery to the existing local
-- GitHub-authorized Windows identity and retain a durable, credential-free audit.
-- The installer wraps this migration in its verified backup/SQL transaction.
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF ISNULL((SELECT TOP (1) TRY_CONVERT(int,VersionNumber)
           FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0) <> 11
    THROW 51000,'Administrator recovery requires schema 11.',1;
IF OBJECT_ID(N'dbo.AdministratorRecoveryHost',N'U') IS NOT NULL
   OR OBJECT_ID(N'dbo.AdministratorRecoveryEvents',N'U') IS NOT NULL
    THROW 51000,'Administrator recovery tables already exist; inspect before retry.',1;

-- The binding contains only the fingerprint of the EXISTING update deploy
-- key, never a private key, recovery code, GitHub token or password.
-- It is first established after an ordinary, successful FMSC Administrator login
-- on the LocalDB host; no anonymous caller can establish it from the login form.
CREATE TABLE dbo.AdministratorRecoveryHost
(
    HostId tinyint NOT NULL
        CONSTRAINT PK_AdministratorRecoveryHost PRIMARY KEY
        CONSTRAINT CK_AdministratorRecoveryHostSingleton CHECK (HostId = 1),
    DevicePublicKeySha256 binary(32) NOT NULL,
    WindowsSid nvarchar(184) NOT NULL,
    BoundUtc datetime2(3) NOT NULL
        CONSTRAINT DF_AdministratorRecoveryHostBound DEFAULT SYSUTCDATETIME()
);

-- Success-only audit is committed atomically with the credential reset.
CREATE TABLE dbo.AdministratorRecoveryEvents
(
    RecoveryEventId bigint IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_AdministratorRecoveryEvents PRIMARY KEY,
    TargetUserId int NOT NULL
        CONSTRAINT FK_AdministratorRecoveryEventsUser
        REFERENCES dbo.Users(UserId),
    PreviousUsername nvarchar(100) NOT NULL,
    NewUsername nvarchar(100) NOT NULL,
    DevicePublicKeySha256 binary(32) NOT NULL,
    WindowsSid nvarchar(184) NOT NULL,
    WindowsComputer nvarchar(128) NOT NULL,
    RecoveredUtc datetime2(3) NOT NULL
        CONSTRAINT DF_AdministratorRecoveryEventsUtc DEFAULT SYSUTCDATETIME()
);

INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
VALUES(N'12',SYSDATETIME(),SUSER_SNAME(),
       N'Local-host Administrator recovery binding and credential-free audit');
