# Release Checklist — Windows x64 Installer

**Release candidate commit:** `________________`  
**Application version:** `________________`  
**Required DB schema version:** `________________`. Verify the application-required version and `Installer/core.ps1` match at the candidate commit. As of 8 November 2026 the current source/installer specifies **application 1.0.16 / schema 10**; schema 1 is the historical baseline, not the active target. See [production migrations](../Migrations/README.md).
**Reviewer and test date:** `________________`  
**Windows account / OS / target hardware:** `________________`

**Rule:** An automated green check is not permission to install on a live school database. Mark a stage done only with a matching artifact/run URL, output and recorded result. For full risk context see [master audit](../Audit/FMSC_MASTER_AUDIT.md).

## 1. Source and changes

- [ ] Freeze the exact Git commit and review its changes and any outstanding security/deployment findings.
- [ ] Explain user-visible and business-rule changes in the pull request; update [CHANGELOG.md](../CHANGELOG.md) and guides.
- [ ] Confirm no school MDF/LDF, .bak files, credentials, student exports or sensitive logs were committed.
- [ ] Review any permission, admission, fee, recovery or schema change with the relevant operator.

## 2. Automated validation

- [ ] [Desktop CI](../.github/workflows/fmsc-desktop-ci.yml) passed **at the release candidate SHA**.
- [ ] C# Release compilation and focused WinForms contract fixtures passed.
- [ ] Portable regression and synthetic performance checks passed.
- [ ] Disposable SQL fixture safety, real SQL integration and installer acceptance passed (as applicable).
- [ ] If DB code changed, the SQL integration tests passed **against a disposable copy**; record connection, fixtures and schema.
- [ ] Confirm known runner warnings are understood; no test was disabled to make an artifact appear.

## 3. Database-free complete installer artifact

- [ ] [Complete installer workflow](../.github/workflows/fmsc-complete-installer.yml) finished **successfully** at the same SHA.
- [ ] Downloaded database-free Actions artifact and recorded URL, run ID and checksum.
- [ ] Package contains `FMSC_SMS/`, `Install FMSC_SMS.bat`, `core.ps1`, `Tools/SqlLocalDB.msi`, `Tools/sqlcmd.exe`, the applicable versioned `Migrations/` folder, required app resources, readme and communication deployment guide. For the current source **1.0.16/schema 10**, the committed production chain contains **2.sql through 10.sql**. The installer applies only pending numbers for the attached database. Confirm every required intermediate migration is present and verify the supported source/target versions; **never replay retired pre-baseline development migrations**.
- [ ] Copied installer and numbered SQL files match the source commit; copied-helper migration discovery/version tests passed. The staged application's files also pass source-to-staging SHA-256 checks before activation (Audit 02). Start a new workflow run for new source rather than rerunning an older run.
- [ ] Microsoft LocalDB MSI signature and pinned sqlcmd digest were verified by the workflow; double-check source of prerequisites if changed.
- [ ] DB snapshot is **not** included in Actions. The owner locally supplies matching **detached** `FMSC_DB.mdf` and `FMSC_DB_log.ldf` only for fresh-install validation.
- [ ] Confirm expected `FMSC_DB` schema and database version against supplied snapshot; **do not replace existing school data or publish DACPAC over MDF/LDF**.

### Exact-source release and installed-binary provenance (Audit Issue 04)

- [ ] Record the **exact Source commit used by Workflow 04**, its successful gated run, and the matching private Updates manifest; do **not** substitute the current `main` SHA if it has advanced.
- [ ] Compare the installed `FMSC_SMS.exe` SHA-256 to the independently verified published manifest (not just `1.0.16` ProductVersion); use [the read-only local probe](../Tools/VerifyInstalledFmscRelease.ps1) and retain its safe version/hash/status lines privately.
- [ ] Confirm the running Windows process points to that same installed executable and the active LocalDB schema equals the release requirement. Run the application's read-only installation compatibility check only on a trusted matching EXE.
- [ ] Verify Communication Center visible controls and permissions in the **actual installed UI**; exercise chat handoff only on a disposable synthetic database with test destinations. Opening a chat or reporting a send is not provider delivery.
- [ ] Record the local operator's acceptance; until then the installed-device result is **PENDING**, even if CI and private publication succeeded. A published source SHA that predates later fixes does **not** prove those fixes are installed.

## 4. Isolated installation and recovery acceptance

- [ ] Fresh install on a disposable Windows test PC with a verified matching database pair.
- [ ] Elevated install and operation under the **actual Windows user**; file ACL/LocalDB instance/attach validated.
- [ ] Existing schema version upgraded with correct migration order using a restored **disposable clone**; original data and recovery scope preserved.
- [ ] Verified pre-migration backup and rollback/recovery scenario tested.
- [ ] App startup and full schema compatibility check pass after install/upgrade.
- [ ] Communication Center: confirm that `8.sql` deliberately retires `comm` and old `dbo.StudentGuardianCommunication` data **without archiving/importing it** (approved owner decision); preserve unrelated student/staff/finance records. Confirm `9.sql` creates disabled `msg` messaging and explicit Admission consent, `10.sql` adds the provider-neutral/manual WhatsApp handoff, and the schema-10 runtime structure check passes. Test manual one-chat-at-a-time open, pause, opt-out, consent and operator-report behavior on a disposable clone. Never interpret a chat link or operator report as provider-verified delivery; SMTP, Meta Cloud API and automatic sends remain disabled.
- [ ] Deliberately invalid, newer, missing and mismatched database/prerequisite scenarios refuse unsafe installation.

## 5. School workflow acceptance

- [ ] Test login, role permissions, user/role maintenance and school setup.
- [ ] Test direct admissions, Quick Registration confirm/cancel, student movement and unsaved-edit protections.
- [ ] Test whole-rupee fees, postings, collections, adjustments/refunds, and uncertain SQL response recovery without duplicate financial writes.
- [ ] Test backup, `RESTORE VERIFYONLY`, safety backup, actual restore and application reconnection on a test database.
- [ ] Test reports, PDFs/CSVs, certificates, previews, real printer and expected DPI/window sizes.
- [ ] Confirm no known blocker or undocumented manual SQL workaround remains.

## 6. Final release decision

- [ ] **GO** — All applicable gates passed with recorded evidence; authorized by release owner.
- [ ] **NO-GO** — Any missing evidence or unsafe behavior is documented in [FMSC_MASTER_AUDIT.md](../Audit/FMSC_MASTER_AUDIT.md).

**Outcome / blockers:** `________________`  
**CI run:** `________________`  
**Installer run and artifact:** `________________`  
**Backup / rollback location:** `________________`  
**Approver:** `________________`

Artifact visibility follows this public repository. Include no school database, backup, credentials or private logs. Broad school deployment requires the owner’s recorded acceptance above.

### Offline dependency verification for current releases

- [ ] Verify complete `FMSC_SMS/UpdateTransport/`,
  `FMSC_SMS/UpdateSupport/Tools/SqlCmd/` and
  `FMSC_SMS/UpdateSupport/Tools/LocalDb/` bundles, each with
  `FMSC.ToolIntegrity.json`, plus `MaintenanceTools.ps1` and `ToolCatalog.json`
  in application support and full-installer support.
- [ ] An application-only private update carries both SQL tools, so future
  update installation and recovery do not depend on tools on Windows PATH.
- [ ] Confirm the Microsoft signature for LocalDB MSI and pinned go-sqlcmd
  archive digest during packaging.
- [ ] Exercise bundled SQL dependency repair on a disposable Windows machine,
  never a live school database.
- [ ] Confirm first installation populates protected
  `%ProgramData%\FMSC_Updater\MaintenanceTools`; delete the extracted setup folder
  and verify update checks and SQL repair still work.
- [ ] Confirm local publishing reuses `../Tools` offline. Release workflows download
  approved tools themselves and verify the actual published bundle manifests.
- [ ] Test a corrupt/missing Git bundle, its HTTPS recovery without Git, offline
  refusal when no valid copy survives, UAC cancellation and unchanged-version reuse.
- [ ] Confirm a deliberately changed tool version is retained separately and the
  previous application's tool version remains usable after rollback.
- [ ] Check actual app/schema versions and migration inventory against the
  approved commit; historical 1.0.0/schema 1 is not the current release version.
