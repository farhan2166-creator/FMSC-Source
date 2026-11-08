-- Schema 8: retire the unused Communication Center and its Admission consent add-on.
-- Do not modify migrations 2-7. The installer owns the enclosing SQL transaction.
-- This migration intentionally refuses deletion when communication records exist.
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF ISNULL((SELECT TOP (1) TRY_CONVERT(int,VersionNumber)
           FROM dbo.DatabaseVersion ORDER BY VersionId DESC),0)<>7
    THROW 51000,'Communication retirement requires schema 7.',1;

IF OBJECT_ID(N'dbo.StudentGuardianCommunication',N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM dbo.StudentGuardianCommunication)
    THROW 51000,'Guardian communication records exist; review before retirement.',1;

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
        THROW 51000,'Unexpected communication objects; review before retirement.',1;
    IF EXISTS (SELECT 1 FROM sys.foreign_keys F
               JOIN sys.objects R ON R.object_id=F.referenced_object_id
               JOIN sys.objects P ON P.object_id=F.parent_object_id
               WHERE R.schema_id=@SchemaId AND P.schema_id<>@SchemaId)
        THROW 51000,'External table depends on communication; review before retirement.',1;
    IF EXISTS (SELECT 1 FROM sys.sql_expression_dependencies D
               JOIN sys.objects R ON R.object_id=D.referencing_id
               WHERE D.referenced_schema_name=N'comm' AND R.schema_id<>@SchemaId)
        THROW 51000,'External SQL code depends on communication; review before retirement.',1;

    -- Discard only stock templates/settings. Do not drop contacts or operational evidence.
    SELECT @Sql=@Sql+N'IF EXISTS (SELECT 1 FROM comm.'+QUOTENAME(T.name)+N')
        THROW 51000,''Communication operational records exist; review before retirement.'',1;'+CHAR(10)
    FROM sys.tables T WHERE T.schema_id=@SchemaId AND T.name IN
       (N'Contact',N'Campaign',N'Message',N'Attempt',N'PendingReceipt',
        N'WebhookInbox',N'Audit',N'Rule',N'RuleBaseline');
    IF LEN(@Sql)>0 EXEC sys.sp_executesql @Sql;

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
        THROW 51000,'Webhook role has unexpected members; review before removal.',1;
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
        THROW 51000,'External foreign key references Admission communication; review before removal.',1;
    DROP TABLE dbo.StudentGuardianCommunication;
END;

INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
VALUES(N'8',SYSDATETIME(),SUSER_SNAME(),
    N'Retire unused Communication Center and Admission communication add-on');
