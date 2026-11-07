# Production database migrations

The first production release uses application **1.0.0**, database **1**.
The schema-1 baseline contains the complete original production schema.
Application **1.0.1** requires schema **2**. Migration `2.sql` removes the
generic audit trail. Fee Change History continues to show stored fee-rate
versions and posted charges. The pre-migration backup retains the legacy audit
entries.

Application **1.0.2** requires schema **3**. Migration `3.sql` reserves
legacy activation for active students and clears the legacy setup flag when
a former student is successfully readmitted.

Migration `4.sql` supports staff-initiated WhatsApp sends. It updates two
procedures in the optional communication module when present and records
schema **4**. It does not install the optional module or modify school records.

Migration `5.sql` adds storage for the optional guardian email and WhatsApp
choice on the admission form. Existing admissions start with no recorded
choice.

Migration `6.sql` strengthens the already-installed optional Communication Center,
when present, by enforcing admission WhatsApp withdrawals and changed numbers
at both preview and provider-claim time for student-linked contacts. If the optional
module is absent, migration 6 records the new base schema version without
installing or enabling it. Existing contacts, messages, approvals, and history
are preserved. The migration requires version 5 and refuses partial module
installations rather than silently skipping an unsafe change.

Migration `7.sql` introduces the new one-choice school communication agreement
on Admissions (SMS, WhatsApp, announcements and financial notices). The existing
WhatsApp-only preference is not silently promoted; its original scope remains
for historical entries until Admission records a new answer. The optional
communication module, when present, gets a permission-checked contact-linking
procedure plus pre-send consent revalidation. No messages send automatically and
the migration does not install the optional module.

Disposable tests publish the complete schema-only baseline directly at version 1. Old development migrations are retired and must never be replayed on baseline 1. See [the baseline decision](../Docs/FIRST_RELEASE_BASELINE.md).

# Apply migrations without the installer

Use **`Apply-Migrations.bat`** (double-click on Windows) or run
**`Apply-Migrations.ps1`** from this folder. This updates an **already attached**
FMSC SQL database; it does **not** install LocalDB, attach an MDF/LDF,
replace database files, install the desktop app, or activate an app release.

1. Run under the **same Windows user account** that owns the intended
   `(localdb)\MSSQLLocalDB` database. Close FMSC and other database users.
   Confirm you have an approved maintenance window and a separate tested backup.
2. Double-click `Apply-Migrations.bat`. The script displays the actual SQL
   instance, database, Windows identity, detected schema, required schema,
   pending versions and backup location. Type **APPLY** to proceed, or close
   the window/cancel without modifying SQL data.
3. Before the first pending script, the applier makes a
   `BACKUP DATABASE ... WITH CHECKSUM`, validates it with
   `RESTORE VERIFYONLY WITH CHECKSUM`, and checks that its `.bak` file exists
   and is non-empty. The default backup folder is
   `C:\ProgramData\FMSC_Database\Backups`. It records the recovery point
   in `LastPreMigrationBackup.txt` and writes `ApplyMigrations.log`.
4. Each pending numbered `*.sql` is executed in ascending order inside the
   same-connection, exclusive-lock, outer-transaction and schema-version
   wrapper used by the installer. Already-applied files are skipped. The
   applier verifies the complete consecutive sequence from the database's
   current version to the required schema **before taking a backup**. If an
   intermediate file is missing, it refuses to run instead of skipping ahead.
   The process stops on any SQL/version/backup error; do not blindly retry.
5. Verify the final version and test the updated FMSC app against the database.
   **Only the full installer performs its staged-application compatibility
   checks and activation**; this standalone tool does not certify app
   compatibility merely by reaching the numbered schema.

For a **read-only migration plan** (database connection and version query, no
backup or SQL changes):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Apply-Migrations.ps1 -PlanOnly
```

For an approved **disposable database clone** with a different name, or when
`sqlcmd.exe` is already installed elsewhere:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Apply-Migrations.ps1 -DatabaseName FMSC_DB_Test -BackupRoot 'C:\FMSC-Clone-Backups'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Apply-Migrations.ps1 -SqlCmdPath 'C:\Tools\sqlcmd.exe' -PlanOnly
```

`-Apply` skips the interactive **APPLY** prompt (use only for intentionally
approved unattended runs). By default the applier reads the required version
from the app/installer source next to `Migrations` and refuses if they
disagree. If only the migration folder is copied, the highest numbered SQL
file becomes the target and the script warns that application compatibility
must be checked separately. If `sqlcmd.exe` is missing, the same pinned and
SHA-256-checked Microsoft go-sqlcmd build as the installer is obtained.

**This is a database mutation tool. Test on a disposable copy first.**
See `Docs/BACKUP_RECOVERY.md` for recovery guidance.

## Schema 8 — Retired Communication Center (staged, not published)

Migration `8.sql` retires the unused optional `comm` module and the separate, unused
`dbo.StudentGuardianCommunication` Admission add-on. The normal student and staff
contact fields remain unchanged. The migration refuses to drop operational records,
unexpected schema objects, external database dependencies or guardian consent rows.
Migration files 2–7 remain immutable; there is no legacy communication-data upgrade.
The installer supplies the outer transaction and verified backup. Do not execute this
migration on a live school database without the standard backup and review process.

The Communication Center dashboard entry, provider services, worker, webhook and
obsolete tests are retired in the same application version. A replacement will use
a separately reviewed schema migration; this removal does not enable messaging.
