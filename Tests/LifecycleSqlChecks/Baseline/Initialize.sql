-- Initialize a NEW, owned, schema-only test fixture. Never upgrades a school database.
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF NOT EXISTS(SELECT 1 FROM sys.extended_properties WHERE class=0 AND name IN
 (N'FMSC_Level1Owner',N'FMSC_Level2Owner',N'FMSC_BillingChecksOwner',N'FMSC_CertificateChecksOwner',N'FMSC_DisposableFixture'))
 THROW 51000,'Baseline initialization requires an explicitly owned disposable fixture.',1;
IF EXISTS(SELECT 1 FROM dbo.Students) OR EXISTS(SELECT 1 FROM dbo.Users)
 OR EXISTS(SELECT 1 FROM dbo.DatabaseVersion)
 THROW 51000,'Baseline initialization requires an empty, freshly published test database.',1;
INSERT dbo.DatabaseVersion(VersionNumber,AppliedOn,AppliedBy,Notes)
VALUES(N'1',SYSDATETIME(),N'Disposable fixture',N'Complete first-production schema baseline');
DECLARE @Scope uniqueidentifier=NEWID();
EXEC sys.sp_updateextendedproperty @name=N'FMSC_QuickRegistrationScope',@value=@Scope;
-- Do not seed roles, permissions or BillingAutomationState in the
-- disposable fixture. Production migration 16 must prove that it initializes
-- these first-run prerequisites from genuinely schema-only state.
