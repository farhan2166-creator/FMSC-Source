# FMSC_SMS — School Management System

Windows school-management application built with C#, .NET 8 Windows Forms and SQL Server Express LocalDB. Includes admissions, placement history, staff records, fees, reporting, certificates, communication and backup/recovery.

## Source and operating guidance

- Use the application's Help & FAQ for operator guidance. Its maintained catalog is in `Pages/Help`.
- [AGENTS.md](AGENTS.md) records owner decisions and development safeguards.
- [SECURITY.md](SECURITY.md) covers sensitive data and vulnerability reporting.
- [CHANGELOG.md](CHANGELOG.md) retains change history; historical entries do not define the current schema or behavior.
- [Migrations/README.md](Migrations/README.md) explains database migration delivery.
- [Fee report file map](Core/Reports/Fees/README.md) identifies the current report implementations.

The application version and required database schema are separate values maintained in `Installer/core.ps1`. Build metadata derives from that source; do not guess them from older notes. Production behavior is defined by current C#, SQL migrations and compatibility contracts.

## Build

On Windows with the .NET 8 SDK:

```powershell
dotnet restore FMSC_SMS.sln
dotnet build FMSC_SMS.sln -c Release --no-restore
dotnet run --project Tests/HelpChecks/HelpChecks.csproj -c Release
dotnet run --project Tests/AuditFixChecks/AuditFixChecks.csproj -c Release
```

Database tests must use their owned disposable fixtures, never a school database.

## Installation and updates

- [Desktop CI](.github/workflows/fmsc-desktop-ci.yml) builds and checks the application.
- [Workflow 04](.github/workflows/fmsc-windows-download.yml) builds the public application candidate and publishes private updates only after owner authorization, exact-commit Sandbox acceptance and artifact verification.
- [Complete installer](.github/workflows/fmsc-complete-installer.yml) packages installer scripts and verified prerequisites. Fresh installations require the owner's matching detached MDF/LDF pair, added privately after downloading the database-free package.

Use the graphical installer/launcher and preserve existing school data. Existing installations upgrade through supported numbered migrations after verified backups; never replace an installed school's database with release-template files. Keep school databases, backups, account tokens and Google Desktop registration out of source and public artifacts. Private Google publishing uses `FMSC_GOOGLE_DESKTOP_JSON`; local private publishing uses ignored `CloudBackupGoogleDesktop.json` beside the project.

Passing CI or merging a PR is not school-deployment acceptance. Verify the intended Windows account, supported database upgrades, recovery, operator workflows and actual printers before release. Preserve permission checks, financial correctness, consent, transaction/recovery semantics and publication gates.
