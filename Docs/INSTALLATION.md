# Installation and Upgrade Guide — Windows x64

This describes the **existing FMSC_SMS installer**; it does not replace it. Use a tested Windows machine and approved, verified data. For backup/restore see [BACKUP_RECOVERY.md](BACKUP_RECOVERY.md).

## What is delivered

The [Complete Windows Installer ZIP](../.github/workflows/fmsc-complete-installer.yml) is designed to include:

```text
FMSC_SMS/                 Published self-contained app and resources
  UpdateTransport/         Pinned bundled MinGit (Git, SSH, DLLs, notices)
  UpdateSupport/Tools/     SqlCmd/ and LocalDb/ bundles with integrity manifests
  UpdateSupport/           Shared tool resolver, catalog and recovery support
Database/                 You provide FMSC_DB.mdf and FMSC_DB_log.ldf for a fresh install
Tools/                    Signed Microsoft vc_redist.x64.exe and SqlLocalDB.msi; verified sqlcmd.exe
Migrations/               Empty at first-production schema 1; future numbered upgrades begin at 2.sql
FMSC.Setup.exe             Native Windows GUI entry point for new installs
FMSC.Uninstall.exe         Separate administrator-approved uninstaller
Uninstall-FMSC.ps1         Uninstaller engine (also included in app UpdateSupport)
Install FMSC_SMS.bat        Compatibility shortcut to FMSC.Setup.exe
core.ps1                   Existing protected install/update engine
READ_ME_FIRST.txt         Packaging instructions
COMMUNICATION_CENTER.md  Optional add-on review, consent and deployment instructions
SHA256SUMS.txt            Package content hashes
```

**Update dependencies:** The approved x64 build packages the complete pinned
MinGit runtime (Git plus OpenSSH) with the application, alongside the self-contained
.NET runtime. Windows PATH and a separate Git installation are not required for
private update checks. The complete installer also packages Microsoft Visual C++ v14 x64, SQL LocalDB and sqlcmd in its
top-level Tools folder. Installation retains all three verified tool bundles in
`%ProgramData%\FMSC_Updater\MaintenanceTools`, protected against ordinary-user
modification. The original extracted setup folder can be deleted after successful
installation; update and repair do not look for it.
School PCs retain their own read-only, DPAPI-protected SSH authorization; no shared
GitHub credential is added. An older application that predates this resolver still
needs a one-time upgrade to gain automatic tool recovery. Do not manually copy a
single `git.exe` into the installation.

## Preparing tools for local or workflow publishing

Local publishing defaults to the owner's adjacent `../Tools` folder containing
`MinGit/`, `sqlcmd.exe`, `SqlLocalDB.msi` and `vc_redist.x64.exe`. The publish target verifies and reuses
these files without downloading. On first preparation, existing Git/sqlcmd versions
must match `Installer/ToolCatalog.json`; both Microsoft installer files must have valid Authenticode signatures.
The preparer records complete file hashes and refuses later corruption. Keep the
generated `FMSC.ToolIntegrity.json` files with the cached bundles.

To fill or refresh that cache deliberately, run once from the project folder:

```powershell
.\Tools\PrepareMaintenanceTools.ps1 -DownloadMissing
```

`FmscToolsDirectory`, `BundledGitDirectory`, `BundledSqlCmdPath` and `LocalDbMsiPath`
remain available for custom local paths. Both release workflows use the same
preparer with `-DownloadMissing` in their own workspace, validate approved Git/sqlcmd
archive hashes and the Microsoft MSI signature, and bundle the results automatically.
The download policy, versions and required files are defined in one tool catalog.

**Important:** The GitHub build deliberately excludes school MDF/LDF files. The source repository alone is **not** a ready-to-install fresh database. A complete-installer workflow must first run successfully and upload its artifact; do not assume an installer exists from a passing ordinary CI build.

After fixes are merged, start a **new** Complete Windows Installer ZIP run with **Run workflow → main**. Rerunning an older run rebuilds its original source commit. Check the source commit in the run summary and extracted `READ_ME_FIRST.txt`; do not mix an older `core.ps1` with newer application files. Packaging verifies the copied installer against that checked-out revision and checks the migration folder inventory.

Historical installer guides and dated tests may describe development schemas 19–21 and migrations 20/21. Those are **pre-baseline history**, not install instructions for schema 1. Do not ship or replay those numbered scripts against the current production baseline.

The installer no longer offers the already-applied Communication Center setup. Its runtime definition remains embedded for verification; fresh installs that need the module must use a snapshot containing it.

The installer checks `Migrations` for top-level `*.sql` files. An empty or missing folder skips migration execution. Filenames must be positive integer versions (for example `8.sql`); the installer reads `dbo.DatabaseVersion`, ignores files at or below that version, and applies pending files in numeric order. It creates one checksum-verified recovery backup before the first pending file. Each file must use a balanced transaction, preserve the installer's outer transaction, and record its filename version in `dbo.DatabaseVersion`. A SQL failure or incorrect recorded version stops installation before app activation. Pending versions must match the schema supported by the application release; update the application's and installer's required version together when shipping a future schema change.

## Before a fresh install

1. Choose the intended **Windows operator account** and confirm it can access the database after elevated installation. LocalDB instances are per-Windows-user; installation as a different administrator is a known cross-account risk.
2. Download the database-free installer artifact from the [Actions page](https://github.com/farhan2166-creator/FMSC-Source/actions). Verify the exact source commit, successful job, artifact integrity and contents.
3. Extract the ZIP **completely** into a normal writable folder. Do not run scripts inside the compressed archive.
4. Obtain the owner-approved, compatible **detached** database pair. Both `FMSC_DB.mdf` and `FMSC_DB_log.ldf` must come from the **same** verified snapshot. Never copy an attached/live database or replace a production database with a DACPAC.
5. Put the two files inside the extracted top-level `Database/` folder (beside `core.ps1`); check exact filenames. Neither file belongs in GitHub, email, or CI.
6. Confirm the Windows machine has enough storage, administrator access for the installer, and school-approved antivirus/security policies. Back up any existing installation separately before an upgrade.

## Run the installer

1. Close FMSC_SMS and other sessions connected to its LocalDB instance.
2. Double-click **FMSC.Setup.exe**. Approve the Windows administrator/UAC prompt. The existing **Install FMSC_SMS.bat** is a compatibility shortcut to the same GUI launcher.
3. The taller, OceanBlue-themed FMSC setup window shows all eight installation stages, current verification activity, and completion or failure. Its **Yes, open FMSC** and **No, close** buttons stay disabled until every installation and database check passes; choose Yes to open FMSC after verification or No to exit without launching it. The graphical executable runs the existing PowerShell installation engine with its console hidden. It checks app/process state, LocalDB, database-file presence, staging, version/migration compatibility, and preserves existing school data.
4. **Never ignore a blocked or failed validation.** Record the exact message and installer logs and stop before retrying or adjusting data files.
5. After successful installation, launch the application under the intended operator account; verify connection, login, schema compatibility, a read-only student search, a report preview, and permitted user actions.

The app expects `FMSC_DB` under `C:\ProgramData\FMSC_Database` and uses `(localdb)\MSSQLLocalDB` by default. The **original** approved first-production baseline was application 1.0.0 / database schema 1. An earlier historical snapshot used application **1.0.3 / schema 3**; it is not the current installation target. Read the actual version and schema from [`Installer/core.ps1`](../Installer/core.ps1) at the approved Source commit, and verify the full sequential migration chain. Verify the actual installer version and database schema from the exact new release artifact before a fresh install; never assume the historical baseline is the current required version. See [first-release baseline](FIRST_RELEASE_BASELINE.md).

On every fresh setup and application update, FMSC checks the installed Microsoft Visual C++ v14 x64 runtime. When version 14.30 or later is missing, it runs the included, Microsoft-signed redistributable silently with a diagnostic log, before SQL Server 2022 LocalDB. An already suitable runtime is skipped. A restart-required code stops installation safely; restart Windows and rerun setup. Logs are stored under `C:\ProgramData\FMSC_Database` as `VcRedist-*.log`.

If silent LocalDB installation fails (for example, MSI code 1603), the setup dialog offers a Yes/No choice to open Microsoft's official installer wizard and reports a verbose log under `C:\ProgramData\FMSC_Database` . A failed/cancelled wizard or restart-required result stops installation safely. Code 1603 alone does not identify the underlying MSI problem; retain the verbose log for diagnosis. An immediate `SQL Server LocalDB installation failed. MSI exit code: 1603` message without this fallback belongs to the older installer: verify the artifact's source commit before retrying.

## Updating an existing installation

The in-app updater launches the verified **UpdateSupport/FMSC.Setup.exe** from its prepared release and displays the same OceanBlue-themed eight-stage installation window after it closes FMSC safely. On verified completion, its embedded **Yes, open FMSC** / **No, close** buttons become enabled; No leaves the updated app installed but closed until its normal desktop shortcut is opened. A completed update restarts the application through the desktop shell; a failed update shows an actionable error without hiding its rollback or log information. The GUI executable has separate fresh-install and update entry modes, but both use `core.ps1` for validation, SQL safeguards and recovery. The installer still supports a console-compatible non-GUI mode for unattended, explicitly isolated test execution.


- Take and **verify** a recoverable backup first and note its storage path.
- Do **not** copy a replacement MDF/LDF over an installed school's existing pair.
- Run the same verified installer with the app closed. The original first-production baseline requires **schema 1**. That historical schema-3 description is not a current requirement. The required schema comes from the exact release's [`Installer/core.ps1`](../Installer/core.ps1) and its complete numbered migration chain; a matching approved detached database or its verified migration path is required. Legacy pre-release schemas carrying higher version labels are **not** silently downgraded to 1; use the separately approved fresh-baseline installation procedure in [first-release baseline](FIRST_RELEASE_BASELINE.md).
- Record the automatic pre-migration safety backup, installer status, original/current version, and post-upgrade application compatibility. Recovery requires a tested restore path.
- If anything fails, **do not manually rerun SQL migrations on the live school database**. Follow [recovery](BACKUP_RECOVERY.md), and use a verified disposable clone for diagnosis.

## Uninstalling FMSC while retaining school data

Close all FMSC windows and complete any pending update/recovery. In Windows **Settings → Apps → Installed apps**, select **FMSC School Management → Uninstall**. You can also double-click **FMSC.Uninstall.exe** inside `C:\FMSC School Management` or in the complete installer folder. Approve UAC, review the independent uninstall confirmation window and select **Uninstall**. Cancel makes no changes. After a successful uninstall, acknowledge the completion message; the uninstaller window then closes automatically. If uninstall fails, the window stays open to show the error.

The uninstaller removes the installed application, staged/rollback application versions, updater and maintained tool bundles, FMSC per-Windows-profile local caches/settings, the FMSC desktop shortcut and the Windows uninstall entry. It does not uninstall SQL Server LocalDB or the shared Visual C++ runtime, run SQL migrations, detach the database, or delete files outside its allowlisted FMSC folders. It stops if the application is open or an update/recovery is unfinished.

Existing `.mdf`, `.ldf` and `.bak` files (as well as secondary database/backup formats) under `C:\ProgramData\FMSC_Database` remain in place. Protected files found inside removable FMSC application/updater/cache folders are copied first to `C:\ProgramData\FMSC_Database\UninstallPreserved\<unique-id>`, with SHA-256 verification. **Encrypted cloud queue packages (`.fmscbackup`) are archived along with their per-package DPAPI recovery-key copies, the local recovery-key file, and delivery/verification journals.** These archived metadata and key files are excluded from the subsequent database-folder log cleanup. If copying or verification fails, uninstall refuses to delete those source folders. Database files are **not** detached, modified or repaired by uninstall. Backups placed elsewhere by the school are untouched.

**Caution:** Uninstall deletes application-local preferences, updater authorization, cloud-account OAuth tokens and scheduling settings. Only encrypted cloud backup **recovery** credentials and delivery evidence are retained in the protected archive, not cloud account access tokens. Archived DPAPI keys work only for the originating Windows user/profile and are **not portable** to a replacement computer; keep the school-controlled cloud recovery passphrase independently before uninstall. Archived delivery journals and packages are recovery evidence, **not an automatically resumed upload queue** after reinstall; reconnect the cloud providers and use supported recovery procedures before considering old pending deliveries complete. Future installation must use the supported existing-database detection and migration checks.

The uninstaller must be tested on disposable Windows installations before use on a school computer.

## Disposable Windows installation acceptance in GitHub Actions

Run **FMSC Disposable Windows Installation Acceptance** from **Actions → Run workflow** on the reviewed branch/commit. This manual workflow uses a fresh GitHub-hosted `windows-2022` runner, checks the approved data-free schema fixture, verifies Microsoft's signed LocalDB MSI and pinned sqlcmd, publishes the real full installer, creates a disposable LocalDB schema-only test database containing **no real school records**, detaches it, then runs the actual `Installer/core.ps1` against that synthetic database. The workflow verifies installation completion, on-disk application and database files, and the installed application's `--verify-installation` exit code.

The Release Readiness workflow also calls this test after security, desktop and SQL checks pass on a release branch. It requires a GitHub-hosted Windows runner, refuses existing FMSC assets or catalogs, and removes only its owned synthetic database, installed files and temporary package after success or failure. It uses no repository secrets or write credentials. Its result artifact contains only a sanitized JSON report and Markdown summary, including cleanup status. The workflow does **not** export the synthetic database, the raw installer logs, or secrets. It must fail or report blocked when prerequisites, elevation, validation, or the real installer cannot run. If the GitHub Windows runner already has LocalDB, the report explicitly says the initial clean MSI installation was **not tested**. This is not a guaranteed pristine Windows VM or an interactive MSI wizard/printer/UI test, and it does not replace manual clean-PC acceptance using an owner-approved detached database pair. Self-hosted execution is refused.

## What must be tested before broad deployment

Installation under the intended Windows user; existing-data upgrade and rollback on a restored clone; backup verification; reports and printer; permissions; actual SQL disconnect/reconnect; and representative money transactions. Refer to [RELEASE_CHECKLIST.md](RELEASE_CHECKLIST.md) and [master audit](../Audit/FMSC_MASTER_AUDIT.md).

## Database-free builds

The app-only ZIP is **not** a fresh-install package. The full-installer workflow includes prerequisites but will not contain student data until you supply the detached pair locally. Never claim release-ready without a confirmed successful installer artifact and manual acceptance.

## Fresh-PC dependency boundaries (reviewed 6 November 2026)

- The Windows x64 complete installer includes a self-contained .NET 8 application,
  pinned MinGit and OpenSSH, Microsoft's verified go-sqlcmd, and the Microsoft-signed
  SQL Server 2022 LocalDB MSI. Installation provisions LocalDB system-wide.
- Installation and subsequent updates reuse matching verified machine-wide tools.
  A changed version is retained separately, keeping previous versions available
  for rollback. Every resolution verifies the complete bundle, including Git's DLLs.
- Missing or corrupt tools are restored from a verified application bundle first;
  if unavailable, the shared resolver downloads the approved replacement over
  HTTPS and verifies it before activation. Git can therefore repair itself without
  using Git. Writing this protected cache may require Windows administrator/UAC
  approval. A failed download or verification stops the operation safely.
- The in-app SQL dependency repair uses the retained MSI and verifies its Microsoft
  signature again. LocalDB is installed only when absent, or when repair is explicitly
  requested; ordinary application updates do not reinstall the engine.
- Windows x64, Windows PowerShell 5.1, Windows Installer, administrator/UAC
  approval and a supported OS configuration are still required. These are
  operating-system requirements, not bundled FMSC components. Use the intended
  Windows account because LocalDB instances are per-user.
- The **detached** school MDF/LDF pair is never distributed in public GitHub
  artifacts. Supplying the owner-approved database, confirming version/schema
  compatibility and school login credentials remain required. Each computer also
  needs its read-only public deploy key approved in FMSC-Updates before online
  updates work. Normal bundled installation works offline; internet is required
  for private updates and for replacing tools when no verified local copy survives.
- A successful workflow package is not a substitute for acceptance on a genuinely
  clean Windows PC. If Windows lacks components required by SQL Server 2022,
  its signed LocalDB MSI may refuse installation; inspect the MSI log rather
  than installing unverified extras or touching the school database.

### SQL dependency repair

The connection screen's SQL dependency repair checks the shared VC++ v14 x64 runtime
before repairing LocalDB. It restores verified retained tooling or obtains the approved
replacement when necessary. A damaged registered runtime receives a repair attempt;
a pending Windows restart stops the sequence before LocalDB setup. This operation
never runs FMSC database migrations or replaces the school's MDF/LDF files.

Uninstall removes the exact per-Windows-user FMSC cloud-backup scheduled tasks only
when their action points to the installed FMSC executable and scheduled-backup command.
An unexpected action is retained and stops cleanup for review; unrelated tasks are untouched.
