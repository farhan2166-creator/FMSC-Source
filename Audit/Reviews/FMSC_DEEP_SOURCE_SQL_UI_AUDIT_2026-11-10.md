# FMSC — Deep Source, SQL and Windows Workflow Audit (10 November 2026)

> **Status: IN PROGRESS — source-level observations, not production certification.** This report is deliberately pinned to Source `f257a0b2e07a75404b5c03bebae4adb27e2fa53e` (merged #160; application 1.0.24, schema 15). It does not represent live-school SQL execution, physical printer checks, or an exhaustive interactive Windows UI test. The audit branch contains reporting only; no application, database, installer, security or release logic was changed.

## Scope and method

This is a defect-hunting audit separate from historical audits and from claims that CI proves an operator journey. The initial inspection fetched live GitHub source for installed-app entry points, first-run account setup, school profile, fee setup/readiness, direct admission and number handling, SQL fixture and migrations 13–15, backup/restore and cloud transport, updater/installer, exact-SHA release preflight, and relevant CI execution evidence. The repository tree contains **1,210 files, including 751 C# and 45 .sql files**, plus two DACPAC files. An inventory of 751 files is **not** evidence of individually reviewed semantics or completed GUI testing.

Issue classifications:
- **Source-confirmed:** the stated code path and defect are directly inferable without runtime reproduction;
- **Observed CI:** actual Actions logs establish the reported behavior;
- **Coverage gap:** the tests do not exercise the described production scenario, not proof that the scenario necessarily fails;
- **Operational risk:** the code permits a failure scenario, but its frequency is unmeasured;
- **Unverified:** needs a real SQL/Windows/provider reproduction.

## Findings (stable IDs)

### DSA-01 — Cloud backup holds a thread-owned mutex across `await`
**High | Source-confirmed threading defect; occurrence unmeasured.**

`Services/Backup/CloudBackupCoordinator.cs:53–117` uses `Mutex.WaitOne(0)`, performs asynchronous retry, hashing and cloud I/O, and releases the mutex in `finally`. A Windows mutex must be released by its acquiring thread; asynchronous continuations have no thread-affinity guarantee in the scheduled headless entry point (`Program.cs:39–49`). `RetryPendingUploadsAsync` repeats the same pattern (`CloudBackupCoordinator.cs:172–179`). Different-thread release may throw, causing a scheduled task to fail after successful work. Replace the async-held mutex with an async-safe process coordination design; test scheduler and interactive-mode interleavings, crashes and restart.

### DSA-02 — Workflow 04 refuses current unpublished patch sequence
**High release-blocking | Observed CI, not a broken safety invariant.**

Source `Installer/core.ps1:30–31` declares app **1.0.24 / schema 15**, but the stable feed at the failing run was **1.0.21**. `Tools/PrepareWorkflow04Release.ps1:74–104` accepts only the published version or one subsequent patch. Workflow 04 run [42809273044](https://github.com/farhan2166-creator/FMSC-Source/actions/runs/42809273044), attempt 3, failed at preflight with: “Source version 1.0.24 is neither published version 1.0.21 nor next patch.” No Sandbox gate or build was executed in that run. Reconcile publication sequencing without relaxing exact-SHA Sandbox acceptance.

### DSA-03 — Cloud retention recreates a pending upload after deliberate pruning
**Medium | Source-confirmed conditional retry state defect.**

`CloudBackupCoordinator.cs:268–296` clears an old provider remote ID when retention deletes a formerly delivered copy. `RetryPendingUploadsCoreAsync:231–248` treats any empty provider ID with an extant queued package as “not uploaded”. With Google Drive retention=1 and an older queued backup still pending to an unavailable Dropbox provider, a newer Drive backup prunes the older Drive copy, and subsequent retries re-upload that old Drive copy; retention may delete it again. Represent provider delivery/pruned state separately from a never-uploaded state and regression-test multi-provider partial failure.

### DSA-04 — First-run SQL tests still use operator-independent fixture initialization
**High coverage gap | Not a demonstrated new production failure.**

`Tests/LifecycleSqlChecks/Fixture.cs:10–91` creates a synthetic administrator role/permissions by direct SQL, prepares school profile, campus, program, class/section, academic years, fee types and rates before the first-admission regression. `FirstAdmissionCounterChecks.cs` proves two direct admissions on that configured school but not a shipped detached-template → actual first-run WinForms → setup → first collection journey. Add a separate production-template-to-first-payment acceptance path that does not insert hidden prerequisites directly. Preserve existing isolated SQL lifecycle tests.

### DSA-05 — First-admission regression omits the UI's explicit-number parameter
**Medium coverage gap.**

`StudentAdmissionPage.cs:4145–4146,4236–4257` validates and submits explicit `@AdmissionNumber`; the new `FirstAdmissionCounterChecks.cs:64–77` uses `Fixture.AdmissionArgs`, which omits that parameter (`Fixture.cs:117–127`). Test fresh-school admissions using the actual UI's explicit-number argument as well as SQL's automatic-number fallback. Include duplicate, stale, concurrent and failed-save cases.

### DSA-06 — Active technical guides disagree about current version/schema
**Medium documentation defect.**

`Docs/INSTALLATION.md:86–99` says 1.0.3/schema 3; `Docs/ARCHITECTURE.md:23,66,94` says 1.0.16/schema 10; `Docs/DEVELOPMENT_WORKFLOW.md:108` says schema 21 in a “current runtime” statement. `Migrations/README.md:3` correctly identifies 1.0.24/schema 15. Mark all historical information unmistakably as dated, and make current status derived from the installer version authority.

### DSA-07 — Failed cloud restore staging retained on the database volume
**Medium operational risk | Previously observed, rechecked.**

`Services/Backup/CloudRestoreStaging.cs:21–36,63–99` leaves uniquely named downloaded/encrypted and decrypted temporary staging on failure. Preserve recoverability after SQL restore has begun, but clean pre-SQL failures (bad passphrase, corrupt/incomplete download) and cap accumulated staging; record sanitised failure evidence.

### DSA-08 — Cloud upload verification and ambiguous provider acknowledgments
**Medium operational risks | Previously observed, rechecked.**

`CloudBackupTransport.cs:173–185,228–289` verifies upload size but Dropbox content hash is not independently attested; `CloudBackupCoordinator.cs:251–265` compares a transport record largely populated from locally generated SHA. A Dropbox successful upload with a lost response can then fail a same-name retry (add/autorename=false) with no remote reconciliation. Verify provider-specific remote content hashes and reconcile unknown outcomes before resubmission/cleanup.

### DSA-09 — Cloud provider client credentials absent in checked-in config
**Feature-enablement blocker; registration may be intentionally deferred.**

`CloudBackupProviders.json` has empty Google client ID and Dropbox app key. `CloudBackupOAuth.cs:54–59` refuses a connection with empty registration. No credentials should be invented or copied into this audit. Publisher must securely configure provider app registrations.


### DSA-10 — Interactive cloud-backup encryption blocks WinForms synchronization context
**High responsiveness risk | Source-confirmed scheduling path, performance unmeasured.**

`Pages/Administration/BackupRestorePage.cs:424–445` awaits `CloudBackupCoordinator.CreateAndUploadAsync()` from a WinForms action. The coordinator awaits its SQL backup worker without `ConfigureAwait(false)`, resumes on the captured WinForms synchronization context, then invokes the **synchronous, multi-gigabyte** `CloudBackupCrypto.Encrypt` at `CloudBackupCoordinator.cs:78–86`. The progress/status UI and Windows message pump are blocked for the duration of encryption (and synchronous local-backup pruning). Offload CPU/disk-intensive encryption to a worker without losing job state, cancellation/rollback, or the permission boundary. Test with a large synthetic backup and UI responsiveness checks.

### DSA-11 — Compiler detects stack allocations inside every cloud encryption/decryption chunk
**Medium large-backup reliability risk | Compiler warning CA2014, actual stack overflow unproven.**

`Services/Backup/CloudBackupCrypto.cs:53–69,104–120` creates `stackalloc` nonce and authenticated-data buffers on every 1-MiB chunk. Very large files can accumulate stack usage across loop iterations. The Windows audit build emitted four `CA2014` warnings at those locations, plus a separate nullable dereference warning (`CS8602`) in a test file. Move fixed-size spans outside each loop, reset their contents on each chunk, and add multi-gigabyte synthetic roundtrip tests before declaring this resolved.

### DSA-12 — Cloud Settings dialog can close during an asynchronous save/account connection
**Medium UI lifecycle race | Source-level conditional failure, not reproduced.**

`Pages/Administration/CloudBackupSettingsDialog.cs:79–95` leaves Cancel/close available while async Google/Dropbox authentication or `SaveAsync` is pending. `ToggleProviderAsync:98–120` re-enables/relabels controls in its `finally`; `SaveAsync:177–230` saves settings, awaits Windows task registration, then sets `DialogResult`/closes the form. There is no dialog `FormClosing` busy guard in this class or its `ApplicationWindowForm` base. Closing the dialog during these awaits can allow a discarded dialog to finish persisting settings or update disposed controls. Prevent closure while a state-changing operation is active or implement explicit cancellation and lifetime-safe continuation handling.

## Whole-repository automated evidence — first audit CI run

**Review PR:** [#161](https://github.com/farhan2166-creator/FMSC-Source/pull/161) (draft, no app/SQL production changes).
**Actual workflow:** [17 - Deep Source SQL and Windows UI Coverage, run 42816089972](https://github.com/farhan2166-creator/FMSC-Source/actions/runs/42816089972) — first execution **PASS**, including all three jobs.

- Read every checked-out **751 C# source files**, including **487** production-area C# files, ~**100,605 production source lines**, and 254 test files. This is an automated file-level source inventory, not 751 individual expert code reviews.
- Parsed **265** schema-only baseline DACPAC stored-procedure bodies, plus **14** numbered SQL migration files containing **43** procedure create/alter statements. The baseline model is schema 1; current schema 15 combines subsequent migration changes. This is not a fresh `sys.sql_modules` snapshot of an upgraded database.
- The first static pass reported **142 heuristically matched candidates** across C# source and tests. These include expected blocking test helpers, intentionally caught exceptions and false-positive calls, **not 142 bugs**. Four unresolved `Query("print"|"export"|"zoom")` matches were false positives from non-database helpers; the scanner was corrected on the audit branch.
- The first UI-class extractor indexed **111** classes, but omitted `StudentAdmissionPage : ContextPage`. The scanner was corrected to include `ContextPage` and more form entry points; the first number must not be presented as exhaustive UI coverage.
- Existing native Windows UI suite **PASS: 15 test modes** (`RunDurableUiContracts.ps1 -IncludePdfReports`), including admission layouts, collection counter controls, fees/report views, certificate/PDF rendering and print layout. These are **SQL-free control/renderer tests**, not real operator sessions.
- Existing public disposable Windows SQL/installer job **PASS**, with a generated record-free baseline, real installer/LocalDB under the runner, recovery interruptions and cleanup; **100 automatic-billing SQL checks** also passed. The installed LocalDB runtime on the hosted runner was **preexisting**, so **pristine MSI/Visual C++ installation is not demonstrated**.
- Audit run evidence: [Actions job logs](https://github.com/farhan2166-creator/FMSC-Source/actions/runs/42816089972). The Windows disposable test's synthetic checkout/source commit may be the PR-generated merge commit rather than original main; this is not a source publication.
- The currently advertised cloud OAuth client identifiers remain empty, so genuine provider authorization, uploads, ambiguous responses and restore are **not live-tested**.

### Operator workflow verification matrix

| Workflow | Evidence at reviewed SHA | Essential coverage missing |
|---|---|---|
| Pristine Windows install / VC++ / LocalDB | Public disposable installer PASS; synthetic prerequisite checks exist | Machine with *no* LocalDB/VC++, interactive MSI and reboot |
| Initial administrator sign-in | First-run C# and SQL bootstrap inspected | Actual GUI, keyboard/focus, cancellation and second Windows account |
| Setup readiness and school profile | Source and regression fixture inspected | Real untouched template → UI School Profile and fee setup |
| First and second admissions | Real SQL admission procedure regression exists | Full UI-selected explicit numbers on first empty school |
| Tuition/transport billing | 100 disposable real SQL tests PASS | Interactive preview/confirm, print and offline operator paths |
| Payments, credit, refunds, reversals | Real SQL/transaction paths and prior lifecycle tests inspected | All financial UI modal / restart / uncertain-commit sequences |
| Campus/class transfer, leaving, readmission | Source and lifecycle tests inspected | Complete interactive workflow permutations including permission variants |
| Certificates and reports | Native WinForms/RDLC/PDF test modes PASS | Actual printer, both paper feeds, margin/letterhead physical checks |
| Cloud backup & restore | Source reviewed; local crypto corruption tests exist | Provider OAuth IDs, real provider API, fault-injection and large DB |
| Updater/recovery | Actual synthetic installer and interruption recovery PASS | Production old-schema school clone, reboot, network and non-admin account |
| Multi-user/local database | Windows identity/LocalDB dependency inspected | Real multi-PC shared database (separate future design) |

**Interpretation:** Complete **file inventory** and multiple real Windows/SQL test jobs are achieved. Complete **semantic reasoning over every source file / stored procedure** and **end-to-end GUI operator execution** are not achieved by those jobs; these remain explicit verification gates and must not be reported as passes.

## Coverage boundary and follow-up

The above issues are independent of prior audit history. A source file inventory, source-code inspection, successful synthetic CI, and static SQL/schema analysis must never be presented as an exhaustive interactive Windows/operator verification. The remaining coverage items are: enumerate every C# file and stored procedure, inspect SQL definitions in the schema-only DACPAC (not only object manifest signatures), reconcile every navigation destination with UI permissions and save/restore path, run clean Windows LocalDB first-install/first-payment and recovery scenarios, verify financial reversals/refunds and cross-campus isolation, and validate real Google/Dropbox behavior with disposable accounts. Mark each executed scenario with actual reproducible logs, timestamps, and reviewed commit SHA.

## Safety and provenance

- **Base source:** [f257a0b2](https://github.com/farhan2166-creator/FMSC-Source/commit/f257a0b2e07a75404b5c03bebae4adb27e2fa53e)
- **Observed Workflow 04 failure:** [run 42809273044](https://github.com/farhan2166-creator/FMSC-Source/actions/runs/42809273044)
- **No live school database modified. No cloud message sent. No release triggered by this audit.**
- **No Windows GUI, LocalDB, printer or cloud-provider reproduction executed in this audit yet.**
