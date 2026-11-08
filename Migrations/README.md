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

Migration `8.sql` retires the optional `comm` module and
`dbo.StudentGuardianCommunication` (the former Admission communication add-on).
By explicit owner decision, **all legacy communication contacts, campaigns,
messages, attempts, audit evidence, settings and guardian preferences may be
deleted even when nonempty**. Nothing is copied to a legacy archive. Old
WhatsApp or school-message choices are never converted into new consent.

Ordinary school records (student/staff contacts, Admissions, fees, access and
permissions unrelated to the old module) are outside the deletion scope. Unknown
legacy tables, external foreign keys / SQL dependencies, and unexpected guardian
schema extensions are refused before retirement. Migrations 2–7 are unchanged.
The installer provides a verified backup and the enclosing SQL transaction;
if migration fails, recovery restores the previous working application/database.
**Do not use this destructive retirement on a live school database until its
separate restored schema-7 test is accepted.** The backup remains the only
retention source for intentionally discarded old communication records.

The new `msg` foundation in migration 9 starts with disabled channels and no
outbound work. Dashboard access to preview, history and permitted settings does
not enable Confirm & Send, the provider, a worker or automatic messaging.

## Schema 9 — New Communication Center foundation (draft, not published)

Migration `9.sql` requires schema 8 and creates a **new** `msg` schema. It does not
restore `comm`, `dbo.StudentGuardianCommunication`, historical contact lists,
provider credentials or any automatic sender. All SMS and WhatsApp channels
start **disabled**. The existing student admission record holds the one authoritative
nullable school-message Yes/No choice; historical admissions start without consent.

The migration adds durable batches, recipient snapshots, attempts, provider receipt
correlation, channel suppression, templates and audit evidence. All changes are
executed under the installer's existing transaction with pre-migration backup.
The application must not expose sending until SQL writers, authorization, provider
validation, verified callbacks and recovery checks are complete. See
[Communication rebuild contract](../Docs/COMMUNICATION_REBUILD_V2.md).

## Schema 10 — Provider-neutral groundwork (draft, not published)

Migration `10.sql` requires version 9. It adds empty optional guardian/staff email
fields on their existing school records, an opt-out-only email suppression table,
a disabled Email channel, and a provider mapping for direct Meta WhatsApp and
SMTP alongside Twilio. No historical guardian email is imported, and the single
Admission school-message consent remains authoritative. Configuration never
enables a channel.

SMS/WhatsApp transactional confirmation and dispatch remain as in schema 9.
**Email is not yet an eligible send channel**: no email audience resolver,
provider credential vault, email recipient snapshots, email claim, SMTP adapter,
verified email delivery or sending UI exists. Those require a separately reviewed
migration and real acceptance. The manual dispatcher now selects an approved
SQL provider identity and rejects unimplemented adapters before claiming an
attempt. No publication, Workflow 04 bypass or automatic delivery is permitted.

### Free WhatsApp manual extension within draft schema 10

Schema 10 also supplies `msg.ManualBatch` and `msg.ManualHandoff` with a
permission-checked, atomic manual confirmation, per-chat live revalidation,
resume/skip/operator-report semantics, and a database trigger refusing a
provider API attempt from manual profiles. The administrator must explicitly
enable the free WhatsApp handoff; it never sends itself or produces verified
delivery receipts. This is part of the unmerged migration-10 implementation,
not a modification to the already-applied schema-9 migration.
