-- Synthetic schema-7 legacy messaging rows for a disposable SQL catalog ONLY.
-- This intentionally is not a representation of real school data.
IF (SELECT TOP (1) TRY_CONVERT(int,VersionNumber) FROM dbo.DatabaseVersion ORDER BY VersionId DESC)<>7
    THROW 51000,'Legacy communications fixture requires schema 7.',1;
IF SCHEMA_ID(N'comm') IS NOT NULL
    THROW 51000,'Expected the optional legacy module to be absent before synthetic seeding.',1;
EXEC(N'CREATE SCHEMA comm AUTHORIZATION dbo;');
CREATE TABLE comm.Contact(ContactId uniqueidentifier NOT NULL PRIMARY KEY, Destination varchar(16) NOT NULL);
CREATE TABLE comm.Campaign(CampaignId uniqueidentifier NOT NULL PRIMARY KEY, Subject nvarchar(100) NOT NULL);
CREATE TABLE comm.Message(
    MessageId uniqueidentifier NOT NULL PRIMARY KEY,
    ContactId uniqueidentifier NOT NULL REFERENCES comm.Contact(ContactId),
    CampaignId uniqueidentifier NOT NULL REFERENCES comm.Campaign(CampaignId),
    Body nvarchar(120) NOT NULL);
CREATE TABLE comm.Attempt(
    AttemptId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    MessageId uniqueidentifier NOT NULL REFERENCES comm.Message(MessageId),
    Outcome varchar(20) NOT NULL);
CREATE TABLE comm.Audit(
    AuditId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    MessageId uniqueidentifier NOT NULL REFERENCES comm.Message(MessageId),
    EventName varchar(40) NOT NULL);
DECLARE @Contact uniqueidentifier=NEWID(),@Campaign uniqueidentifier=NEWID(),@Message uniqueidentifier=NEWID();
INSERT comm.Contact(ContactId,Destination) VALUES(@Contact,'+923001234567');
INSERT comm.Campaign(CampaignId,Subject) VALUES(@Campaign,N'Synthetic campaign');
INSERT comm.Message(MessageId,ContactId,CampaignId,Body)
    VALUES(@Message,@Contact,@Campaign,N'Synthetic message, never sent');
INSERT comm.Attempt(MessageId,Outcome) VALUES(@Message,'Failed');
INSERT comm.Audit(MessageId,EventName) VALUES(@Message,'FixtureOnly');
CREATE TABLE dbo.FMSC_Test_Migration8SchoolSentinel(Marker nvarchar(80) NOT NULL PRIMARY KEY);
INSERT dbo.FMSC_Test_Migration8SchoolSentinel(Marker) VALUES(N'Core school records are outside comm');
-- Populate an ordinary role/permission/grant, since the schema-only baseline
-- intentionally has no application users or seeded school permissions.
IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE PermissionCode=N'Students.View')
    INSERT dbo.Permissions(PermissionCode,PermissionName,Description)
    VALUES(N'Students.View',N'View Students',N'Synthetic noncommunication permission');
INSERT dbo.Roles(RoleName,Description)
VALUES(N'FMSC Migration8 Test Role',N'Disposable authorization sentinel');
INSERT dbo.RolePermissions(RoleId,PermissionId)
SELECT R.RoleId,P.PermissionId FROM dbo.Roles R CROSS JOIN dbo.Permissions P
WHERE R.RoleName=N'FMSC Migration8 Test Role' AND P.PermissionCode=N'Students.View';
IF (SELECT COUNT_BIG(*) FROM dbo.RolePermissions RP
    JOIN dbo.Roles R ON R.RoleId=RP.RoleId
    JOIN dbo.Permissions P ON P.PermissionId=RP.PermissionId
    WHERE R.RoleName=N'FMSC Migration8 Test Role' AND P.PermissionCode=N'Students.View')<>1
    THROW 51100,'Ordinary school role grant was not seeded.',1;
IF (SELECT COUNT_BIG(*) FROM comm.Contact)<>1
 OR (SELECT COUNT_BIG(*) FROM comm.Campaign)<>1
 OR (SELECT COUNT_BIG(*) FROM comm.Message)<>1
 OR (SELECT COUNT_BIG(*) FROM comm.Attempt)<>1
 OR (SELECT COUNT_BIG(*) FROM comm.Audit)<>1
    THROW 51000,'Synthetic legacy operational rows were not seeded.',1;
