-- All claims are against a disposable data-free SQL fixture, never school data.
IF (SELECT TOP (1) TRY_CONVERT(int,VersionNumber) FROM dbo.DatabaseVersion ORDER BY VersionId DESC)<>9
    THROW 51000,'Legacy upgrade did not reach schema 9.',1;
IF SCHEMA_ID(N'comm') IS NOT NULL
 OR OBJECT_ID(N'dbo.StudentGuardianCommunication',N'U') IS NOT NULL
 OR SCHEMA_ID(N'legacy_comm_v7') IS NOT NULL
    THROW 51000,'Obsolete communication objects or an unwanted archive survived.',1;
IF OBJECT_ID(N'msg.Batch',N'U') IS NULL
 OR (SELECT COUNT_BIG(*) FROM msg.Batch)<>0
 OR (SELECT COUNT_BIG(*) FROM msg.Attempt)<>0
 OR (SELECT COUNT_BIG(*) FROM msg.Receipt)<>0
    THROW 51000,'The replacement schema has unexpected messages or is incomplete.',1;
IF (SELECT COUNT_BIG(*) FROM msg.ChannelSettings WHERE Channel IN('Sms','WhatsApp') AND Enabled=0)<>2
    THROW 51000,'The fresh communication providers must stay disabled.',1;
IF (SELECT COUNT_BIG(*) FROM dbo.FMSC_Test_Migration8SchoolSentinel
    WHERE Marker=N'Core school records are outside comm')<>1
    THROW 51000,'A normal dbo table was modified by communication retirement.',1;
IF NOT EXISTS(SELECT 1 FROM dbo.Permissions WHERE PermissionCode=N'Students.View')
    THROW 51000,'Normal school permissions were lost.',1;
