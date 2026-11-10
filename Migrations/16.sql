-- Schema 16: bootstrap only immutable application reference data for a
-- genuinely empty school. Do not initialize school identity, fees, students,
-- users, or other operator-owned data.
-- Executed only inside the installer's verified-backup and SQL transaction.
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF ISNULL((SELECT TOP (1) TRY_CONVERT(int,VersionNumber)
    FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0) <> 15
    THROW 51001,'Clean-school reference initialization requires schema 15.',1;
IF OBJECT_ID(N'dbo.Users',N'U') IS NULL
 OR OBJECT_ID(N'dbo.Students',N'U') IS NULL
 OR OBJECT_ID(N'dbo.Roles',N'U') IS NULL
 OR OBJECT_ID(N'dbo.Permissions',N'U') IS NULL
 OR OBJECT_ID(N'dbo.BillingAutomationState',N'U') IS NULL
 OR OBJECT_ID(N'dbo.BootstrapFirstAdministrator',N'P') IS NULL
 OR OBJECT_ID(N'dbo.GetFirstRunStatus',N'P') IS NULL
    THROW 51002,'Required bootstrap reference schema is missing; inspect before upgrading.',1;

-- Existing schools are unchanged. The first-admin wizard still owns user
-- creation; this migration creates no credentials, accounts, role membership
-- or school operational records.
IF NOT EXISTS (SELECT 1 FROM dbo.Users)
BEGIN
    -- No users alongside populated school records indicates damaged access
    -- state, not an authorized new school; do not silently reopen bootstrap.
    IF EXISTS (SELECT 1 FROM dbo.Students)
        THROW 51003,'Students exist but no users do. Recovery requires review; no new administrator was created.',1;
    IF EXISTS (SELECT PermissionCode FROM dbo.Permissions GROUP BY PermissionCode HAVING COUNT(*) > 1)
        THROW 51004,'Duplicate permission codes prevent safe bootstrap.',1;

    IF NOT EXISTS(SELECT 1 FROM dbo.Roles WITH(UPDLOCK,HOLDLOCK)
                  WHERE RoleName=N'Administrator')
        INSERT dbo.Roles(RoleName,Description)
        VALUES(N'Administrator',N'Full application authority');
    IF NOT EXISTS(SELECT 1 FROM dbo.Roles WITH(UPDLOCK,HOLDLOCK)
                  WHERE RoleName=N'Accountant')
        INSERT dbo.Roles(RoleName,Description)
        VALUES(N'Accountant',N'Configurable finance and reporting authority');

    -- Checked-in source of truth: Core/PermissionCatalog.json (31 codes).
    -- Preserve previously seeded names/descriptions; fill missing codes only.
    DECLARE @Catalog TABLE(
        Code nvarchar(100) NOT NULL PRIMARY KEY,
        Name nvarchar(100) NOT NULL,
        Description nvarchar(500) NOT NULL);
    INSERT @Catalog(Code,Name,Description) VALUES
    (N'Backup.Create', N'Create Backup', N'Create a database backup.'),
    (N'Backup.Restore', N'Restore Backup', N'Restore the database from a backup.'),
    (N'Backup.View', N'View Backups', N'View database backup information.'),
    (N'Calendar.Manage', N'Manage Calendar', N'Manage academic terms/calendar'),
    (N'Certificates.Create', N'Create Certificates', N'Create certificates.'),
    (N'Certificates.Print', N'Print Certificates', N'Print certificates.'),
    (N'Communication.Configure', N'Configure Communication', N'Manage school messaging channels and templates; no automatic provider activation.'),
    (N'Communication.Send', N'Confirm School Messages', N'Confirm explicit, previewed school communication batches. Requires source record access.'),
    (N'Communication.View', N'View Communication History', N'Read message history and delivery states. No contact export.'),
    (N'Enrollment.Manage', N'Manage Enrollment', N'Manage enrollment records.'),
    (N'Fees.Adjust', N'Adjust Fees', N'Request/review/complete fee adjustments'),
    (N'Fees.Collect', N'Collect Fee', N'Record a student fee payment.'),
    (N'Fees.Configuration.Manage', N'Manage Fee Configuration', N'Create and modify class fee configuration.'),
    (N'Fees.Reports.View', N'View billing status checks', N'Check unposted or missing charges and billing cycle status. General report categories use Reports.View.'),
    (N'Fees.Statement.View', N'View annual fee receipts', N'Open and print annual student fee receipts; also needs report and fee viewing access.'),
    (N'Fees.View', N'View Fees', N'View student fee information.'),
    (N'Reports.View', N'View student and finance reports', N'Open student and finance report categories and their exports. Fees.Reports.View is a separate billing-status check.'),
    (N'Staff.Manage', N'Manage Staff', N'Create and update staff records.'),
    (N'Staff.View', N'View Staff', N'View staff records.'),
    (N'Students.Administer', N'Maintain student records and lifecycle', N'Edit saved student details and photos, manage family membership, re-admission, placement/status and legacy/lifecycle maintenance.'),
    (N'Students.Admit', N'Admit Student', N'Create a new student admission.'),
    (N'Students.Family', N'Open family workspace', N'View student families; changing members also requires Students.Administer.'),
    (N'Students.Leave', N'Leave Student', N'Mark a student as leaving the school.'),
    (N'Students.View', N'View Students', N'View student records and searches.'),
    (N'System.Administration', N'Manage system administration', N'Manage users, roles, permissions and audit access. The Administrator role separately retains full application authority.'),
    (N'System.Audit', N'View Audit Logs', N'View administrative audit history.'),
    (N'System.Roles', N'View roles and permissions', N'Read role definitions and the permission catalog. Editing also requires System.Roles.Manage.'),
    (N'System.Roles.Manage', N'Edit Accountant permissions', N'Save the Accountant description and permissions; requires role viewing access. Administrator remains protected with full authority.'),
    (N'System.Setup', N'Manage School Setup', N'Manage school and system configuration.'),
    (N'System.Users', N'View users', N'Read user accounts. Editing also requires System.Users.Manage and role viewing access.'),
    (N'System.Users.Manage', N'Create and edit users', N'Save normal user accounts and reset their passwords; requires user and role viewing access.');
    INSERT dbo.Permissions(PermissionCode,PermissionName,Description)
    SELECT C.Code,C.Name,C.Description FROM @Catalog C
    WHERE NOT EXISTS(
        SELECT 1 FROM dbo.Permissions P WITH(UPDLOCK,HOLDLOCK)
        WHERE P.PermissionCode=C.Code);

    IF EXISTS(SELECT 1 FROM @Catalog C
              WHERE NOT EXISTS(SELECT 1 FROM dbo.Permissions P
                               WHERE P.PermissionCode=C.Code))
       OR NOT EXISTS(SELECT 1 FROM dbo.Roles WHERE RoleName=N'Administrator')
       OR NOT EXISTS(SELECT 1 FROM dbo.Roles WHERE RoleName=N'Accountant')
        THROW 51005,'Required reference catalog bootstrap is incomplete.',1;

    IF NOT EXISTS(SELECT 1 FROM dbo.BillingAutomationState WITH(UPDLOCK,HOLDLOCK)
                  WHERE StateId=1)
    BEGIN
        IF EXISTS(SELECT 1 FROM dbo.BillingAutomationState)
            THROW 51006,'Unexpected billing state prevents safe singleton initialization.',1;
        INSERT dbo.BillingAutomationState(StateId,StartMonth)
        VALUES(1,DATEFROMPARTS(YEAR(GETDATE()),MONTH(GETDATE()),1));
    END;
END;

-- DSA-14: the ordinary/default admission path must not demand a
-- transport-only rate from a child who does not use transport. The real
-- Quick Registration caller also sends its explicit selected-fee list.
-- Preserve the full reviewed admission writer: change only the one guard
-- predicate, and refuse drift rather than guessing at SQL dependencies.
DECLARE @AdmitDefinition nvarchar(max)=OBJECT_DEFINITION(OBJECT_ID(N'dbo.AdmitStudent',N'P'));
IF @AdmitDefinition IS NULL
    THROW 51007,'Admission procedure definition unavailable for reviewed transport guard.',1;
SET @AdmitDefinition=REPLACE(@AdmitDefinition,NCHAR(13),N'');
DECLARE @OldTransportGuard nvarchar(200)=N'WHERE  FT.IsMandatory = 1
           AND NOT EXISTS';
DECLARE @NewTransportGuard nvarchar(280)=N'WHERE  FT.IsMandatory = 1
           AND (@UsesTransport=1 OR FT.IsTransportLinked=0)
           AND NOT EXISTS';
IF CHARINDEX(@OldTransportGuard,@AdmitDefinition)=0
 OR CHARINDEX(@OldTransportGuard,@AdmitDefinition,
              CHARINDEX(@OldTransportGuard,@AdmitDefinition)+LEN(@OldTransportGuard))>0
 OR CHARINDEX(N'@UsesTransport',@AdmitDefinition)=0
 OR CHARINDEX(N'@UseSelectedFeeTypes',@AdmitDefinition)=0
    THROW 51008,'The reviewed admission transport predicate has changed; inspect before migrating.',1;
SET @AdmitDefinition=REPLACE(@AdmitDefinition,@OldTransportGuard,@NewTransportGuard);
-- Some originally published procedures retain leading comments/newlines.
-- Do not guess the starting byte; locate the header keyword and verify the
-- actual procedure identity before constructing an ALTER-only batch.
DECLARE @ProcedureKeywordPos int=CHARINDEX(N'PROCEDURE',UPPER(@AdmitDefinition));
DECLARE @Header nvarchar(256)=UPPER(LEFT(@AdmitDefinition,
    CASE WHEN @ProcedureKeywordPos BETWEEN 1 AND 256
         THEN @ProcedureKeywordPos ELSE 256 END));
IF @ProcedureKeywordPos<1 OR @ProcedureKeywordPos>256
 OR (CHARINDEX(N'CREATE',@Header)=0 AND CHARINDEX(N'ALTER',@Header)=0)
 OR CHARINDEX(N'ADMITSTUDENT',UPPER(SUBSTRING(
       @AdmitDefinition,@ProcedureKeywordPos+LEN(N'PROCEDURE'),100)))=0
    THROW 51009,'Unexpected admission procedure header; refuse unreviewed alteration.',1;
-- ALTER PROCEDURE must be the first statement of its dynamic batch.
SET @AdmitDefinition=N'ALTER PROCEDURE'+SUBSTRING(
    @AdmitDefinition,@ProcedureKeywordPos+LEN(N'PROCEDURE'),LEN(@AdmitDefinition));
EXEC sys.sp_executesql @AdmitDefinition;

INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
VALUES(N'16',SYSDATETIME(),SUSER_SNAME(),
       N'Initialize empty-school reference prerequisites and align optional transport admission policy');
