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

## Coverage boundary and follow-up

The above issues are independent of prior audit history. A source file inventory, source-code inspection, successful synthetic CI, and static SQL/schema analysis must never be presented as an exhaustive interactive Windows/operator verification. The remaining coverage items are: enumerate every C# file and stored procedure, inspect SQL definitions in the schema-only DACPAC (not only object manifest signatures), reconcile every navigation destination with UI permissions and save/restore path, run clean Windows LocalDB first-install/first-payment and recovery scenarios, verify financial reversals/refunds and cross-campus isolation, and validate real Google/Dropbox behavior with disposable accounts. Mark each executed scenario with actual reproducible logs, timestamps, and reviewed commit SHA.

## Safety and provenance

- **Base source:** [f257a0b2](https://github.com/farhan2166-creator/FMSC-Source/commit/f257a0b2e07a75404b5c03bebae4adb27e2fa53e)
- **Observed Workflow 04 failure:** [run 42809273044](https://github.com/farhan2166-creator/FMSC-Source/actions/runs/42809273044)
- **No live school database modified. No cloud message sent. No release triggered by this audit.**
- **No Windows GUI, LocalDB, printer or cloud-provider reproduction executed in this audit yet.**
