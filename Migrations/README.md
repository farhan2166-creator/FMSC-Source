# Production database migrations

**Current source target (10 November 2026): FMSC application 1.0.25, database schema 16.** The committed consecutive production migration chain is `2.sql`–`16.sql`. Apply only scripts newer than the actual attached database's schema version, using the checked installer or the standalone migration tool below. This is source-state guidance, **not** proof of an installed/published release or authorization to update live school data.

**Historical starting point:** The first production release used application **1.0.0**, database **1**.
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

Disposable tests publish the complete **record-free** schema-1 baseline, then exercise the committed **2.sql–16.sql** production chain where applicable. Earlier, unrelated **pre-baseline development migrations** remain retired and must not be replayed. The current numbered production migrations are not those retired scripts. See [the historical baseline decision](../Docs/FIRST_RELEASE_BASELINE.md).

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

## Schema 8 — Retired Communication Center (committed destructive migration)

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
**Do not apply this destructive retirement to any live school database without the owner's approved backup and release-specific restored-schema acceptance.** The prior schema-7 testing and the owner's successful local schema-10 upgrade do not authorize arbitrary future live migrations. The backup remains the only retention source for intentionally discarded old communication records.

The new `msg` foundation in migration 9 starts with disabled channels and no
outbound work. Dashboard access to preview, history and permitted settings does
not enable Confirm & Send, the provider, a worker or automatic messaging.

## Schema 9 — New Communication Center foundation (committed migration)

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

## Schema 10 — Provider-neutral groundwork and manual WhatsApp (historical feature milestone)

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
attempt. An actual application publication still requires the normal release gates and exact-commit Sandbox acceptance; no automatic delivery is enabled.

### Free WhatsApp Business manual extension within schema 10

Schema 10 also supplies `msg.ManualBatch` and `msg.ManualHandoff` with a
permission-checked, atomic manual confirmation, per-chat live revalidation,
resume/skip/operator-report semantics, and a database trigger refusing a
provider API attempt from manual profiles. The administrator must explicitly
enable the free WhatsApp handoff; it never sends itself or produces verified
delivery receipts. This extension is in the **committed `10.sql`**; migration 9 is not retroactively edited. An operator report does not prove provider delivery. A database upgrade to 10 alone cannot add the new WinForms UI to an older installed executable; use a validated matching application build.

## Schema 14 — Editable fee-type policy for new-school setup

Migration `14.sql` requires schema 13. It exposes the existing `IsMandatory` and
`IsTransportLinked` flags through `GetFeeTypes` and extends
`SaveFeeTypeWithDefaultFrequency` to persist both in the same transaction as the
fee name, description, and frequency. Existing flags, fee configurations,
student assignments and bills are **not** modified by the migration. Older
procedure callers that omit the two new optional parameters retain their
current policy values. Operators can now open **Fee Rate Setup → Fee Types**,
select Tuition Fee (or another non-transport fee), mark **Mandatory fee**, save,
then re-run **Setup Readiness**. Class fee rates remain separately configured.

The disposable Lifecycle SQL fixture verifies the same save path used by the
editor, including the missing-mandatory readiness warning, its resolution,
transport-linked round-trip and a legacy caller that omits new parameters.


## Schema 15 — First admission on a fresh or restored empty database

Migration `15.sql` requires schema 14 and initializes the missing singleton
`dbo.AdmissionNumberCounter` (`CounterId=1`) without requiring the operator
to run SQL. An empty school starts with `LastNumber=0`, so the normal admission
writer allocates admission number 1. Existing counter values are left untouched.
If the counter has disappeared from a populated database, the migration
conservatively derives the largest numeric prefix from existing official
admission numbers; ambiguous or out-of-range numbers cause the migration to
abort rather than risk duplicate numbering. A verified backup and transactional
rollback are managed by the installer before this migration.

The disposable SQL lifecycle tests now assert the seed exists before any
fixture overrides it, submit real first and second admissions through
`dbo.AdmitStudent`, verify 1/2 and counter progression, then roll back the
test transaction. Only after these checks does the fixture set the historical
test reference counter to 1000. No changes to the Admissions UI are needed.

## Schema 16 — Empty-school prerequisites and optional transport fees

Migration `16.sql` requires schema 15. On an owned genuinely **empty** school
(no users or students), it initializes only the authoritative Administrator and
Accountant reference roles, any missing entries in the reviewed 31-code
permission catalog, and the `BillingAutomationState` singleton.
**It never creates an administrator login or any school profile, student,
fee rate or payment.** Existing user-bearing school databases are not seeded;
a database with students but no users is refused for supervised recovery.

The first administrator must still be created through the normal, secure
`BootstrapFirstAdministrator` process. The SQL lifecycle and disposable
Windows installer checks now assert the prerequisites exist without direct
test-only inserts of the reference roles or permissions.

The default `AdmitStudent` missing-fee check is aligned to Setup Readiness:
a mandatory transport-linked fee rate is required **only when a student uses
transport**. Quick Registration defaults transport-linked choices to opt-out
and submits its explicitly reviewed fee list; ordinary mandatory nontransport
fees remain required by setup validation. The migration validates the
original SQL predicate before altering it and aborts on semantic drift.
Every migration remains subject to the installer's verified backup and
transactional rollback safety.
