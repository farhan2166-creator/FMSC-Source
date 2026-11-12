# Local backup, cloud backup and restore audit

Reviewed: 12 November 2026. Repository: `farhan2166-creator/FMSC-Source`.
Workspace updated from `main` to **`d7a0be2ee1003cbc3a05c572cd5fe3b2d5ce56ca`**
(PR #206, following Phase 1 / PR #205). Remote main was checked again before
writing this report and remained at this commit.

The findings below record the independent audit of that baseline. Earlier
runbooks were background, not proof that the implementation works. Remediation
was subsequently authorized; see the correction and validation record below.
This report is not publication or real-account cloud acceptance.

**Result: four high, four medium and one low finding. No demonstrated critical
finding. The new replacement-computer recovery and cloud health reporting need
correction before release approval.**

## Confirmed findings

### BR01 — High: configured Google token requests fail client authentication

Locations: [CloudBackupOAuth.cs](../../Services/Backup/CloudBackupOAuth.cs#L117)
(code exchange), [refresh](../../Services/Backup/CloudBackupOAuth.cs#L134), and
`CloudProviderApplicationIds` / `CloudBackupProviders.json`.

The Google authorization-code exchange sends client ID, authorization code,
PKCE verifier, redirect URI and grant type. Refresh sends client ID, refresh
token and grant type. Neither can provide a Google client-secret field; the
configuration model accepts public identifiers only.

Two requests matching these shapes, using the **configured public client ID**
and deliberately fabricated, non-account authorization/refresh values, were
rejected by Google's real token endpoint with:

```json
{"http":400,"error":"invalid_request","description":"client_secret is missing."}
```

This is a verified integration rejection for the configured request shape,
not a successful real-account enrollment test. It explains a reconnect loop
that changing the network or retrying cannot resolve. Both public identifiers
are configured; this is not merely an empty-config warning.

Correction: reconcile the publisher's Google client registration with the
native installed-app token request contract and test both enrollment and
refresh. Google's native-app documentation describes client-secret requirements
conditionally; do not generalize this result to every Google client type.
**Do not put a confidential web-client secret in the desktop/public source or
remove PKCE, callback-state or enrollment protections.** Review the appropriate
native registration and required client metadata with the publisher.

### BR02 — High: clean first-run installations are rejected as existing schools

Location: [ReplacementSchoolRecovery.RequireAbsentOrVirginSchool](../../Services/Backup/ReplacementSchoolRecovery.cs#L78).

The populated-table test exempts six reference tables by name but omits factory
rows in `dbo.AdmissionNumberCounter` and `msg.ChannelSettings`. A clean supported
database therefore fails the supposedly virgin-template route before the wizard
can restore an existing school.

Verified using the reviewed **data-free schema-1 fixture followed by every
current production migration**, in a new owner-labelled SQL container:

```text
GetFirstRunStatus: IsFirstRun=1, UserCount=0, active campus/year/class/section counts=0
Non-exempt populated tables:
  dbo.AdmissionNumberCounter  1 row
  msg.ChannelSettings        3 rows
```

The exact count predicate in this method is positive, so it throws "School
records or setup data already exist." FirstRunSetupForm invokes it before
opening the wizard; replacement cloud connection and final recovery invoke it
again. The normal fresh installer supplies an attached template, making this
an essential path rather than an optional absent-database case.

Correction: recognize verified factory reference/counter/settings content by
schema and expected values. Preserve refusal for real school data, damaged
accounts, unexpected tables and modified setup. Do not remove the existing-school
gate or blindly ignore all communication tables.

### BR03 — High: replacement activation is not bound to the verified backup bytes

Locations: [replacement preflight](../../Services/Backup/ReplacementSchoolRecovery.cs#L198),
[activation](../../Services/Backup/ReplacementSchoolRecovery.cs#L225),
[ReplacementSqlActivation](../../Services/Backup/ReplacementSqlActivation.cs#L16).

The replacement workflow verifies school scope, compatibility and Administrator
credentials in an isolated restore, closes that operation, then passes the
original pathname to activation. It holds no source-file lease or immutable
private copy across verification and activation. The final school-scope check
occurs **after** writing FMSC_DB.

A real-SQL replay using the actual production identity and activation helpers
verified source A, replaced its pathname with source B, then activated it.
The activated database contained B's synthetic marker and different school
scope. The final RequireSame check refused it only after target restoration.
No existing real school was used or overwritten. The authorization callback
was modelled by successful isolated verification; live publisher approval and
school credentials were not exercised in this probe.

A local backup copied/synchronized/replaced during the asynchronous checks can
thus differ from the one approved. A same-scope revision also invalidates the
assumption that the Administrator credentials and contents checked are those
being activated. Ordinary RestoreBackup already holds a FileShare.Read lease
through its server-side verification and restore; this new path does not.

Correction: protect the exact verified bytes through activation, using a private
staged snapshot and/or the appropriate Windows read-only share lease, and verify
that identity before any target write. Keep absent/virgin, publisher, Windows
administrator, school-Administrator, safety-copy and compatibility checks.

### BR04 — High: lost queued ciphertext can be reported as completed delivery

Locations: [retry enumeration](../../Services/Backup/CloudBackupCoordinator.cs#L423),
[manual waiting count](../../Services/Backup/CloudBackupCoordinator.cs#L306),
[scheduled no-due outcome](../../Services/Backup/CloudBackupCoordinator.cs#L97).

Retry processing and the waiting count filter out journals whose PackageFile
no longer exists, including **unfinished** requested deliveries. Scheduled
processing can then say "Pending uploads are complete" and return success.
This matters most for cloud-only backups: the plaintext is deliberately removed
once ciphertext is durably queued, and a not-yet-due run will not create another
backup merely because this undelivered package disappeared.

Executed the actual private RetryPendingUploadsCoreAsync against an isolated
synthetic journal and fake provider/SQL boundaries that throw if contacted:

```text
valid journals loaded: 1
requested Google delivery: unfinished / unverified
queued ciphertext: missing
retry issues returned: 0
```

Correction: evaluate unfinished journal state before filtering by payload
existence. Missing material must produce an actionable recovery warning and
incomplete outcome; completed/superseded journals are allowed to have no payload.
Do not delete journals, invent delivery or silently reset the protection state.

### BR05 — Medium: successful replacement recovery cancels its own dialog close

Locations: [busy close guard](../../Pages/Security/RestoreExistingSchoolForm.cs#L131),
[successful completion](../../Pages/Security/RestoreExistingSchoolForm.cs#L277).

RecoverAsync sets busy=true, then on success sets DialogResult.OK and calls
Close while busy is still true. The FormClosing handler cancels that close.
Only afterwards does finally clear busy. There is no successful-close exception
or second completion call, so the wizard stays open instead of returning OK to
the connection/first-run caller.

Verified by tracing the actual form and .NET 8 WinForms implementation:
Form.Close synchronously sends WM_CLOSE; modal WmClose calls CheckCloseDialog,
which invokes FormClosing and resets a cancelled result to None. A live Windows
recovery completion was not executed in this audit. The existing cloud settings
dialog already has an explicit closingAfterSave exception for this pattern.

Correction: permit only the completed-success close after backend work finishes.
Retain the busy-close prohibition during authorization, downloads and SQL work.

### BR06 — Medium: replacement source failover stops on provider timeouts

Locations: [replacement download catch](../../Services/Backup/ReplacementSchoolRecovery.cs#L182),
[provider discovery catch](../../Pages/Security/RestoreExistingSchoolForm.cs#L195),
[streaming timeout](../../Services/Backup/CloudBackupTransport.cs#L88).

DownloadToFileAsync explicitly raises TimeoutException on stalled progress.
The replacement copy loop does not catch it, so it aborts instead of trying
another identical Google/Dropbox copy. Discovery similarly omits non-user
OperationCanceledException/TaskCanceledException from HttpClient timeouts and
can stop before checking the other provider.

The transport timeout/cleanup behavior passed existing synthetic transport
checks; the omitted replacement catch filters were independently traced.
No real cloud account outage/failover test was performed.

Correction: handle recoverable source timeouts before SQL. Preserve genuine user
cancellation and authorization failure; never fall back to a different recovery
point or another provider after live SQL restoration starts.

### BR07 — Medium: replacement SQL tests are unbuildable and not wired into CI

Location: [SafetySqlChecks.csproj](../../Tests/SafetySqlChecks/SafetySqlChecks.csproj#L7).

The test project containing the new ReplacementSqlActivation checks references
`../../Apps/Worker/Worker.csproj`, which is absent from the current repository.
A current build failed with the missing-reference warning and CS0234/CS0246
errors for SqlClient types. No tracked workflow/script invokes this project;
the maintained lifecycle runner's project list also excludes it.

CloudBackupCryptoChecks exercises crypto, transport fakes, grouping and pure
queue policy, but does not link the actual coordinator, replacement workflow,
inventory or completion UI. Its passing results do not cover BR02–BR05.
PR #206's desktop/security checks passed; that does not contradict this gap.

Correction: move/wire the tests into the maintained owned SQL fixture or repair
the narrow references and actually invoke them. Do not revive the retired worker
or weaken production/test ownership, backup, rollback or Sandbox acceptance.
Add behavioral coverage for the actual first-run gate and orchestration paths.

### BR08 — Medium: successful supersession leaves a false pending warning

Locations: [captured retry issues](../../Services/Backup/CloudBackupCoordinator.cs#L95),
[copied issues](../../Services/Backup/CloudBackupCoordinator.cs#L195),
[supersession](../../Services/Backup/CloudBackupCoordinator.cs#L538),
[final outcome](../../Services/Backup/CloudBackupCoordinator.cs#L220).

An older exhausted/backoff delivery contributes a retry issue before a new
backup is made. The newer recovery-tested upload can subsequently supersede
and release that older package. The final summary still uses the earlier
issue list, reports pending deliveries and can return scheduled exit code 4
even when no unfinished delivery remains.

This is a source-traced state-transition defect; full coordinator execution
against live accounts was not performed. Pure supersession policy tests pass
but do not test the final orchestration outcome.

Correction: reconcile final incomplete/error state after supersession and
retention. Preserve real unresolved failures and retry budgets; do not clear
warnings merely to turn the task result green.

### BR09 — Low: zero-byte cloud placeholders appear as available recovery points

Location: [UnifiedRecoveryCatalog.Group](../../Services/Backup/UnifiedRecoveryCatalog.cs#L52),
with [Drive listing](../../Services/Backup/CloudBackupTransport.cs#L275).

Cloud eligibility checks the date/hash but not positive payload size. Google's
resumable upload code explicitly recognizes incomplete zero-byte placeholders;
listing can expose them under valid FMSC names. A direct call of the actual
catalogue accepted a zero-byte CloudRemoteBackup as one available recovery point.
Restoration rejects its empty download later, but the list offers a known
non-backup as an available choice.

Correction: exclude empty/incomplete physical objects or show an explicit failed
upload diagnostic separately. Keep real unverified nonempty backups available
for validation; never delete unknown remote objects as a cleanup shortcut.

## Implementation traced and protections retained

- BackupRestorePage: unified destinations, local folder, manual backup, retry,
  available recovery points, local/cloud selection/fallback, confirmation,
  progress/activity, authenticated restore and application restart.
- CloudBackupSettingsDialog, BackupSettings, CloudBackupSettings,
  CloudBackupTaskSchedule/SchedulePolicy: per-Windows-user settings, DPAPI key,
  logged-on-user Task Scheduler execution and existing recovery permissions.
- CloudBackupCoordinator, QueuePolicy, JobLock, Operations, DeliveryPolicy,
  ActivityJournal: one verified SQL backup, encrypted fan-out, independent
  provider receipts, persisted attempts, backoff, supersession and retention.
- CloudBackupOAuth/CloudOAuthLoopback: PKCE, loopback-only callback, state
  verification, bounded callback reads and protected refresh-token persistence.
  Public client IDs are configured; no token or real passphrase was read.
- CloudBackupTransport/DropboxIntegrity: reserved Google IDs, lost-receipt
  reconciliation, upload/download integrity, pagination, bounded streaming
  inactivity and same-point fallback. Provider-specific logic stays outside SQL.
- CloudBackupCrypto: chunked AES-GCM, authenticated metadata, PBKDF2, random
  salt/nonces, atomic publication and failed-output cleanup.
- DatabaseBackupManager and BackupRestoreIdentity: header/integrity checks,
  same-school disposable restore/compatibility checks, source pinning, mandatory
  pre-restore safety copy, live restore and attempted safety rollback. Uncertain
  failures preserve evidence and refresh connection validation.
- UnifiedRecoveryInventory/Catalog: local header enumeration, journal digest
  linkage and grouping; names/times alone do not prove local/cloud equality.
- FirstRunSetupForm, DatabaseConnectionForm, RestoreExistingSchoolForm,
  ReplacementSchoolRecovery/SqlActivation: absent/virgin entry, explicit online
  approved device key, Windows administrator, original school Administrator,
  isolated verification, protected safety material and activation.

No automatic dropping/recreating a real school's database or bypassing its
identity/permissions was needed for this audit. Installer/updater recovery,
numbered migrations, financial procedures and exact-commit Sandbox gates were
not changed.

## Execution evidence and limits

1. **Source compilation passed:** application + ThemeSmokeChecks Release,
   Windows targeting on Linux, using temporary local targets that omit Windows
   native launcher execution only. Existing BillingGrid CS8602 warning remains.
   This is not Windows UI execution or publication acceptance.
2. **Existing focused backup tests passed:** `CloudBackupCryptoChecks --release-review`
   runs scheduling, transport failure/cancellation/stream cleanup, lost-receipt,
   digest grouping and pure queue policy checks.
3. **Full portable backup run was not a pass:** after passing its preliminary
   groups and settings checks, it hit the Windows-specific sharing-error
   assumption in CloudBackupJobLock on Linux. This is a platform verification
   limit, not evidence of a Windows product failure. An initial no-restore build
   also needed current NuGet assets restored; that was environmental, not a
   product finding.
4. **Independent current-source crypto controls passed:** multi-chunk random-data
   roundtrip; wrong passphrase, corrupt ciphertext and truncation rejected with
   no published plaintext or leftover partial output. No real SQL content was
   used in these encryption controls.
5. **Real SQL controls passed:** 18 identity/disposable-restore/replacement-
   activation assertions, plus the fresh first-run predicate and source-swap
   reproductions. SQL Server 2022 used the same pinned image as the repository
   harness. The baseline checksum/provenance was checked; current numbered
   migrations were applied to an owned data-free catalog. Target/owner checks
   restricted destructive commands to a new, loopback-only labelled container.
   Container cleanup was verified after both the initial platform-limited
   attempt and completed run.
6. The temporary Linux SQL harness linked the exact production identity/
   activation helpers and copied the repository RestoreChecks source. SQL file
   permissions were adapted for host/container UIDs. Its Windows file-share
   assertion was explicitly not executed; the repository test is unchanged.
   The swap replay modelled successful authorization; live device approval,
   original school credentials and a full Windows replacement recovery were
   not performed.
7. **Queue finding reproduced** by executing the actual private coordinator
   retry method against temporary synthetic metadata with fake boundaries
   that reject any real SQL/provider/settings access.
8. **Provider validation:** two Google HTTPS token requests used the configured
   public ID and fabricated authorization/refresh values. Both returned the
   same missing-client-secret rejection. No account consent, tokens, real cloud
   backup, live upload/delete or school data access occurred. See Google's
   [native-app token contract](https://developers.google.com/identity/protocols/oauth2/native-app).
9. **UI finding:** traced against the application and actual .NET 8 WinForms
   [Close/WmClose/CheckCloseDialog implementation](https://github.com/dotnet/winforms/blob/v8.0.0/src/System.Windows.Forms/src/System/Windows/Forms/Form.cs).
   It was not executed as a real Windows recovery-completion test here.
10. **Test project build failed**, as detailed in BR07. Latest PR #206 existing
    [desktop workflow](https://github.com/farhan2166-creator/FMSC-Source/actions/runs/43255755712)
    and security checks passed on that PR head; no claim is made that those
    executed the absent replacement workflow tests or private installed-school
    acceptance.

Temporary reproducer source/logs are retained in this workspace under
`/tmp/fmsc-backup-audit-probes` and `/tmp/fmsc-backup-audit-*.log` for remediation.
No secret values, connection strings, backup binaries or school records are
included in this report.

## Investigated items not promoted to release defects

- Replacement of a synthetic virgin target returned **MULTI_USER** after SQL
  recovery. The suspicion that successful activation inherently leaves it in
  SINGLE_USER was disproved on the tested engine.
- Administrator usernames are deliberately case-sensitive in normal sign-in
  too. Replacement's exact comparison is consistent with that protection.
- A synthetic 400-ms timestamp proved that exact catalogue equality would
  separate a matching local/cloud digest after filename precision loss. The
  actual SQL backup headers observed here had **zero fractional milliseconds**.
  This remains a precision-hardening edge case, not a demonstrated current
  SQL-generated backup defect.
- Queue capacity currently pauses the complete new backup operation even when
  permanent local storage is enabled. Legacy journals with unknown original
  destinations conservatively retain ciphertext; rotating the passphrase also
  leaves old packages dependent on the old key. These are operational policies
  to confirm and document, not grounds to discard encrypted recovery material
  or weaken retention in this audit.

## Recommended remediation order

Correct BR02/BR03/BR04 and repair/wire the recovery tests first. Resolve BR01's
Google registration/request contract without adding a confidential server
credential to the desktop source. Then correct completion/failover/status and
inventory behavior. Retest full Windows fresh-install and absent-database
recovery, existing-school restore with safety rollback, interrupted/lost queued
payload, and real authorized Google/Dropbox enrollment/upload/download recovery.

The existing same-school restore controls and encryption have useful verified
protections. They do not establish that the new end-to-end recovery path is
release-ready while these high findings remain.

## Remediation — 12 November 2026

All nine confirmed findings have code corrections on `fix/backup-recovery-audit`.
No SQL migration, release-version change, installer redesign or school-data
mutation is needed for these corrections.

| Finding | Correction and regression evidence |
| --- | --- |
| BR01 High | Google Desktop registration parser accepts only Google's `installed` JSON, checks the registered client ID, rejects web credentials and supplies native client metadata to code exchange and refresh. PKCE remains required; user cancellation propagates. Fictional registration/field tests pass. Publisher must supply ignored `CloudBackupGoogleDesktop.json` at build/install time. No real account enrollment, refresh or provider storage acceptance is claimed. |
| BR02 High | `ReplacementTemplateGuard` checks schema-qualified objects and permits only the factory admission counter and disabled, unconfigured channel defaults. A real schema-only baseline plus all migrations passes; modified counters, configured/enabled channels, unknown records and lookalike table names are refused. The authoritative first-run procedure and device/admin gates remain in place. |
| BR03 High | Replacement recovery holds a read-only, `FileShare.Read` lease from before isolated validation through live activation. Native Windows checks exercise refusal to overwrite or delete leased bytes. SQL identity/refusal checks still exercise actual backup/restore commands. |
| BR04 High | Health derives from unfinished journal obligations, even when ciphertext is missing. Missing unfinished files and unreadable/invalid journals produce incomplete status. Verified or safely superseded records may legitimately lack a local payload. Actual coordinator tests use a compile-time-isolated temporary root, never the school's profile. |
| BR05 Medium | Successful recovery clears busy state after backend completion before returning modal OK. Native modal tests cover cancellation before backend invocation, refusing close while busy, successful OK and recoverable errors. Internal test delegates replace only backend/user-dialog boundaries. |
| BR06 Medium | Replacement discovery uses the existing shared inventory operation; timeout and non-user cancellation permit another provider/copy before SQL. Operator cancellation stops immediately. No fallback was added after SQL activation. Timeout/isolation/cancellation tests pass. |
| BR07 Medium | SafetySqlChecks references the actual SqlClient package and production restore helpers instead of retired Worker. A guarded runner provisions an independently owned, pinned, loopback-only SQL container, applies the verified data-free baseline and migrations, runs fresh-template and real recovery checks, and verifies cleanup. Existing SQL CI invokes it. Original fee-matrix checks remain available through the suite's default mode; `--backup-recovery` selects this separately provisioned fixture. |
| BR08 Medium | Final completion status re-reads durable journal state after new uploads and supersession. Retry iteration refreshes entries to preserve newly persisted flags. Native coordinator coverage creates, encrypts, uploads through fake external boundaries, downloads/authenticates and supersedes an older exhausted delivery in one real orchestration run. Unfinished destinations remain independent. |
| BR09 Low | Both provider listings, shared discovery and unified catalogue exclude zero-byte upload placeholders. They are not deleted remotely. Valid nonempty unverified recovery points remain visible. |

### Validation executed locally

- Windows-targeted application and ThemeSmokeChecks source compilation: passed
  on Linux using a temporary compile-only targets file; native launcher execution
  is omitted, with no fake binary or repository build-safety relaxation.
  Existing BillingGrid nullable warning remains.
- CloudBackupCryptoChecks `--release-review`: passed, including new Desktop
  registration, empty-object, timeout, provider isolation and cancellation cases.
- Actual coordinator portable fixture: six checks passed. Windows ACL/job-lease,
  full orchestration and modal checks require native Windows CI and are not
  counted as local passes.
- Owned SQL suite: six migrated-template checks and 18 actual SQL identity and
  replacement-activation checks passed. Windows file-sharing enforcement is
  explicitly skipped on Linux and separately checked by native UI regressions.
- Disposable SQL container cleanup: owner-checked and confirmed.

Native GitHub Windows and SQL workflow results will be recorded with the PR.
Real enrolled-PC recovery, real Google/Dropbox sign-in and a real provider
upload/download/SQL recovery still require authorized acceptance. In particular,
Google cannot be called release-ready until the publisher's matching Desktop JSON
is provided and sign-in plus refresh are demonstrated. No credential or student
data was committed or uploaded for these tests.
