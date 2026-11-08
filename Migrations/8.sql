-- Schema 8: retire the unused Communication Center and its Admission consent add-on.
-- Do not modify migrations 2-7. The installer owns the enclosing SQL transaction.
-- The retired Communication Center (including old guardian preferences) is intentionally disposable.
-- The verified installer backup and outer transaction protect recovery. Never touch school records.
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF ISNULL((SELECT TOP (1) TRY_CONVERT(int,VersionNumber)
           FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0)<>7
    THROW 51041,'Communication retirement requires schema 7.',1;

-- The former admission add-on is not the new consent authority. Check its
-- expected schema before removing even nonempty rows; never infer new consent.
IF OBJECT_ID(N'dbo.StudentGuardianCommunication',N'U') IS NOT NULL
BEGIN
    IF EXISTS (
        SELECT 1 FROM sys.columns C
        WHERE C.object_id=OBJECT_ID(N'dbo.StudentGuardianCommunication')
          AND C.name NOT IN (N'AdmissionId',N'Email',N'Phone',N'WhatsAppConsent',
                             N'RecordedUtc',N'RecordedBy',N'SchoolCommunicationConsent'))
        THROW 51042,'Unexpected guardian communication columns; review before retirement.',1;
    IF EXISTS (
        SELECT 1 FROM (VALUES (N'AdmissionId'),(N'Email'),(N'Phone'),
                            (N'WhatsAppConsent'),(N'RecordedUtc'),(N'RecordedBy'),
                            (N'SchoolCommunicationConsent')) AS Expected(Name)
        WHERE COL_LENGTH(N'dbo.StudentGuardianCommunication',Expected.Name) IS NULL)
        THROW 51043,'Guardian communication table differs from schema 7; review before retirement.',1;
    IF EXISTS (SELECT 1 FROM sys.triggers
               WHERE parent_id=OBJECT_ID(N'dbo.StudentGuardianCommunication'))
        THROW 51044,'Unknown guardian communication trigger; review before retirement.',1;
    -- Ignore dependencies owned by the retired table itself (such as its
    -- CK_StudentGuardianCommunication_ConsentPhone check constraint).
    -- Independently owned procedures/views still block retirement. Internal
    -- comm functions are already scoped for retirement below.
    IF EXISTS (SELECT 1 FROM sys.sql_expression_dependencies D
               JOIN sys.objects R ON R.object_id=D.referencing_id
               WHERE D.referenced_id=OBJECT_ID(N'dbo.StudentGuardianCommunication')
                 AND R.schema_id<>ISNULL(SCHEMA_ID(N'comm'),-1)
                 AND R.object_id<>OBJECT_ID(N'dbo.StudentGuardianCommunication')
                 AND R.parent_object_id<>OBJECT_ID(N'dbo.StudentGuardianCommunication'))
        THROW 51045,'External SQL code depends on guardian communication; review before retirement.',1;
END;

IF SCHEMA_ID(N'comm') IS NOT NULL
BEGIN
    DECLARE @SchemaId int=SCHEMA_ID(N'comm'), @Sql nvarchar(max)=N'';
    -- Refuse unforeseen schema extensions instead of deleting unrelated data.
    IF EXISTS (SELECT 1 FROM sys.tables WHERE schema_id=@SchemaId AND name NOT IN
       (N'Module',N'Settings',N'Contact',N'Template',N'Campaign',N'Message',
        N'Attempt',N'PendingReceipt',N'WebhookInbox',N'Audit',N'Rule',N'RuleBaseline'))
       OR EXISTS (SELECT 1 FROM sys.objects WHERE schema_id=@SchemaId AND type NOT IN
          (N'U',N'P',N'FN',N'IF',N'TF',N'V',N'TR',N'PK',N'F',N'UQ',N'C',N'D'))
       OR EXISTS (SELECT 1 FROM sys.types WHERE schema_id=@SchemaId AND is_user_defined=1)
        THROW 51046,'Unexpected communication objects; review before retirement.',1;
    IF EXISTS (SELECT 1 FROM sys.foreign_keys F
               JOIN sys.objects R ON R.object_id=F.referenced_object_id
               JOIN sys.objects P ON P.object_id=F.parent_object_id
               WHERE R.schema_id=@SchemaId AND P.schema_id<>@SchemaId)
        THROW 51047,'External table depends on communication; review before retirement.',1;
    IF EXISTS (SELECT 1 FROM sys.sql_expression_dependencies D
               JOIN sys.objects R ON R.object_id=D.referencing_id
               WHERE D.referenced_schema_name=N'comm' AND R.schema_id<>@SchemaId)
        THROW 51048,'External SQL code depends on communication; review before retirement.',1;

    -- All legacy communication records may be discarded by explicit owner decision.
    -- Fail-closed dependency and object-scope checks above must pass first.

    -- Drop all internal foreign keys first; never remove external FKs.
    SET @Sql=N'';
    SELECT @Sql=@Sql+N'ALTER TABLE comm.'+QUOTENAME(T.name)+
        N' DROP CONSTRAINT '+QUOTENAME(F.name)+N';'+CHAR(10)
    FROM sys.foreign_keys F JOIN sys.tables T ON T.object_id=F.parent_object_id
    WHERE T.schema_id=@SchemaId;
    IF LEN(@Sql)>0 EXEC sys.sp_executesql @Sql;

    SET @Sql=N'';
    SELECT @Sql=@Sql+N'DROP TRIGGER comm.'+QUOTENAME(O.name)+N';'+CHAR(10)
    FROM sys.objects O WHERE O.schema_id=@SchemaId AND O.type=N'TR';
    IF LEN(@Sql)>0 EXEC sys.sp_executesql @Sql;

    SET @Sql=N'';
    SELECT @Sql=@Sql+N'DROP PROCEDURE comm.'+QUOTENAME(name)+N';'+CHAR(10)
    FROM sys.procedures WHERE schema_id=@SchemaId;
    IF LEN(@Sql)>0 EXEC sys.sp_executesql @Sql;

    SET @Sql=N'';
    SELECT @Sql=@Sql+N'DROP VIEW comm.'+QUOTENAME(name)+N';'+CHAR(10)
    FROM sys.views WHERE schema_id=@SchemaId;
    IF LEN(@Sql)>0 EXEC sys.sp_executesql @Sql;

    SET @Sql=N'';
    SELECT @Sql=@Sql+N'DROP FUNCTION comm.'+QUOTENAME(name)+N';'+CHAR(10)
    FROM sys.objects WHERE schema_id=@SchemaId AND type IN(N'FN',N'IF',N'TF');
    IF LEN(@Sql)>0 EXEC sys.sp_executesql @Sql;

    SET @Sql=N'';
    SELECT @Sql=@Sql+N'DROP TABLE comm.'+QUOTENAME(name)+N';'+CHAR(10)
    FROM sys.tables WHERE schema_id=@SchemaId;
    IF LEN(@Sql)>0 EXEC sys.sp_executesql @Sql;
    DROP SCHEMA comm;
END;

-- The add-on inserted seven permission codes; do not alter base school permissions.
DELETE RP FROM dbo.RolePermissions RP JOIN dbo.Permissions P
    ON P.PermissionId=RP.PermissionId
WHERE P.PermissionCode IN(N'Communication.View',N'Communication.Compose',N'Communication.Send',
    N'Communication.Bulk',N'Communication.Approve',N'Communication.Configure',N'Communication.History');

DELETE FROM dbo.Permissions WHERE PermissionCode IN
    (N'Communication.View',N'Communication.Compose',N'Communication.Send',
     N'Communication.Bulk',N'Communication.Approve',N'Communication.Configure',N'Communication.History');

IF DATABASE_PRINCIPAL_ID(N'FMSC_CommunicationWebhook') IS NOT NULL
BEGIN
    IF EXISTS(SELECT 1 FROM sys.database_role_members
              WHERE role_principal_id=DATABASE_PRINCIPAL_ID(N'FMSC_CommunicationWebhook')
                AND member_principal_id<>ISNULL(DATABASE_PRINCIPAL_ID(N'FMSCCommunicationIngress'),-1))
        THROW 51049,'Webhook role has unexpected members; review before removal.',1;
    IF EXISTS(SELECT 1 FROM sys.database_role_members
              WHERE role_principal_id=DATABASE_PRINCIPAL_ID(N'FMSC_CommunicationWebhook')
                AND member_principal_id=DATABASE_PRINCIPAL_ID(N'FMSCCommunicationIngress'))
        ALTER ROLE FMSC_CommunicationWebhook DROP MEMBER FMSCCommunicationIngress;
END;
IF DATABASE_PRINCIPAL_ID(N'FMSCCommunicationIngress') IS NOT NULL
    DROP USER FMSCCommunicationIngress;
IF DATABASE_PRINCIPAL_ID(N'FMSC_CommunicationWebhook') IS NOT NULL
    DROP ROLE FMSC_CommunicationWebhook;

IF OBJECT_ID(N'dbo.StudentGuardianCommunication',N'U') IS NOT NULL
BEGIN
    IF EXISTS(SELECT 1 FROM sys.foreign_keys
              WHERE referenced_object_id=OBJECT_ID(N'dbo.StudentGuardianCommunication'))
        THROW 51050,'External foreign key references Admission communication; review before removal.',1;
    DROP TABLE dbo.StudentGuardianCommunication;
END;

INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
VALUES(N'8',SYSDATETIME(),SUSER_SNAME(),
    N'Retire obsolete Communication Center data and legacy guardian preferences');
