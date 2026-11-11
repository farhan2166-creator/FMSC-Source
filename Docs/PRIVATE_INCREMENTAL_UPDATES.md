# FMSC private Git updater — manual publishing, no publisher signing keys

**Status:** The updater is integrated. The owner-approved first production baseline is application 1.0.0, database 1; the private distribution was reset before its first production publication. See [the baseline decision](FIRST_RELEASE_BASELINE.md).

## Owner decisions

- `FMSC-Source` contains app source and the existing CI. It is **currently
  public**; the owner plans to make it private later.
- `FMSC-Updates` is a **separate private** GitHub repository used ONLY to
  distribute approved packages and release metadata.
- The owner **manually publishes** approved updates. There is **NO separate
  RSA publisher signature, signing private key, public-key pinning, encrypted
  signing-key backup or GitHub Actions signing secret** requirement.
- No VPS, hosted broker, licensing API, external SQL service or school GitHub
  user accounts.
- Each authorized school Windows user/computer generates a unique SSH key pair.
  Only its PUBLIC deploy key is added to `FMSC-Updates` with **Allow write
  access UNCHECKED**. Its private key is stored under
  `%LOCALAPPDATA%\\FMSC\\Updates` sealed by Windows DPAPI CurrentUser.
  Windows login changes/reinstalls may require re-enrollment.
  A locally generated key is **not** GitHub approval: the computer remains
  pending with its public key visible and copyable until a real read-only Git
  session succeeds. **Verify access** can be retried after the owner adds the
  deploy key; opening the update dialog also retries authorization for a saved
  key, without reading or downloading any update package. **Check for Updates**
  remains disabled and guarded until live authorization has succeeded.
- Account → **Check for Updates** is manual; no automatic program shutdown,
  replacement, or database migrations while the school works.
- Before sign-in, a database/application schema version mismatch in either
  direction opens the existing updater inside the same startup window. The user
  manually checks the private repository; only a saved-key authorization probe
  (not a release check or download) may run on dialog opening, and no automatic
  installation occurs. Closing the updater returns to connection guidance with
  **Open Updates** when the database is older, or **Update Application** when
  the database is newer, replacing the ineffective Reconnect/Restart options.
  Installation still requires explicit **Install Update** confirmation.
  The updater receives the detected database schema and uses the guarded
  installer for backups and migrations if needed. If the database is newer than
  the latest published release supports, no download/install is offered;
  the operator must obtain a newer compatible release, never downgrade SQL.
  A debug executable is not an installed application: installation remains
  restricted to the standard installed FMSC folder; rebuild debug locally.
  Internet failures explain how to retry; no database login is needed.
  A newer application release must be published when necessary, including when
  application files were replaced manually before the database was upgraded.
- Prefer a changed-file incremental download for an EXACT matching installed
  version and file inventory. Otherwise use the full package.
- A database schema mismatch routes to the existing guarded installer/migration
  workflow. App-only staging must never bypass the SQL backup/migration guards.
- Preserve school data/MDF/LDF, installation identity, recovery backups and
  all needed numbered SQL migrations. Application and schema versions are
  **unchanged**; resetting either to 1 is deferred.

## Trust and verification

GitHub private repository write permissions control **who may publish**.
Authorized school devices use **read-only SSH** with GitHub's known SSH host
key pinned. They access only the update distribution repository.

The manifest includes SHA-256 checksums and sizes for archive pieces, full
ZIP/delta ZIP and all installed application files. Clients verify these before
staging. These checks detect corrupted, incomplete or mismatched downloads.

**Limit:** Hashes in a manifest are NOT a digital signature. Someone who gains
write access to `FMSC-Updates` could replace a package AND its hashes.
Protect the owner's GitHub account (2FA) and repository write privileges.
This is an intentionally accepted security trade-off for manual distribution.

No source code, database files, application signing secret or school SSH private
key is published in `FMSC-Updates`.

## Update format and distribution layout

```text
channel/stable.json
versions/<version>/update-manifest.json
versions/<version>/FMSC_SMS-app-full.zip.part0001
versions/<version>/FMSC_SMS-app-full.zip.part0002
versions/<version>/FMSC_SMS-app-incremental.zip.part0001
...
```

`Tools/UpdatePackages` produces a complete ZIP, optional changed-file delta,
a JSON manifest with SHA-256 hashes and deterministic <=90 MiB chunk files.
Its local unchunked ZIP is a build intermediate and must NOT be pushed into Git.

Example local packaging on the approved build output:

```powershell
dotnet run --project Tools/UpdatePackages/UpdatePackages.csproj -c Release -- `
  --current-dir <approved-publish-directory> `
  --output-dir <new-empty-package-directory> `
  --version <approved-application-version> `
  --schema <approved-database-schema>
```

To prepare an incremental package also supply `--previous-dir` and
`--previous-version` from the last approved build. The latest FULL package
must always be available so schools can skip missed versions.

`Tools/UpdateDistribution` creates a checked, independent publishing snapshot:

```powershell
dotnet run --project Tools/UpdateDistribution/UpdateDistribution.csproj -c Release -- `
  --new-package-dir <package-directory> `
  --output-dir <new-snapshot-directory>
```

For a later release, also specify `--previous-snapshot-dir` pointing to
the last published verified snapshot. This tool checks metadata structure,
part sizes, per-part hashes and reconstructed ZIP hashes and retains only
the two latest versions in the NEW snapshot. It never performs a Git push.

The owner must publish a **fully validated snapshot atomically** to
`FMSC-Updates`. Automated Git push/atomic-promotion/history management is
not implemented yet; do NOT copy half a release into the live feed.

## Two-version retention

| New version | Keep in latest tree | Retire from latest tree |
|---|---|---|
| 1.1 | 1.1 | — |
| 1.2 | 1.1, 1.2 | — |
| 1.3 | 1.2, 1.3 | 1.1 |
| 1.4 | 1.3, 1.4 | 1.2 |

Never remove the older version before the newest complete package and manifest
pass every check. A school left on 1.1 when 1.4 is released must still be able
to install the full 1.4 release, with ALL required intervening SQL migrations.

Removing old update files from the current tree **does not reclaim historical
Git blobs**. Workflow 04 now rotates the private `FMSC-Updates/main` branch to a
new parentless commit containing only the checked two-version snapshot, so old
commits no longer remain reachable through main. GitHub may garbage-collect
unreferenced server-side objects later; branch rotation does not promise
immediate remote disk reclamation. Never rewrite `FMSC-Source` history.

## Implemented updater components

- `Core/Updates/UpdateManifest.cs`: validated JSON manifest, path, version,
  inventory and SHA-256 structure, exact-baseline incremental selection,
  full fallback, database-schema installer gate.
- `Core/Updates/UpdateChunks.cs`: deterministic bounded chunking and
  independently checked chunk/whole-archive reconstruction.
- `Core/Updates/UpdateStager.cs`: isolated complete application staging;
  exact file inventory + hashes; never overwrites the live installation.
- `Core/Updates/UpdateDeviceKey.cs`: separate read-only SSH key for an enrolled
  Windows user, with DPAPI-protected private key.
- `Core/Updates/UpdateGitSession.cs`: private read-only Git transport with
  pinned GitHub SSH host key, shallow metadata fetch and bounded chunk streaming.
- `Core/Updates/UpdateGitReader.cs`: manually initiated connection and
  update-version check without a publisher-signature requirement.
- `Core/Updates/UpdateReleasePreparer.cs`: checks repository manifest
  metadata, selects a delta or full update, verifies downloaded chunks,
  reconstructs the ZIP, and prepares the target in an isolated directory.
- `MainDashboardForm.cs` + `UpdateConnectionForm.cs`: Account → Check for
  Updates, generate/copy computer's PUBLIC SSH key, test private Git access.
- `Tools/UpdatePackages`, `Tools/UpdateDistribution` and their regression
  fixtures: package creation and safe two-version snapshot preparation.
- Windows Git enrollment/DPAPI tests and existing desktop CI checks.
- `Tools/PrepareMaintenanceTools.ps1` and `Installer/ToolCatalog.json`: one approved
  catalog for Git/OpenSSH, sqlcmd and the LocalDB MSI. Local publishing verifies
  the owner's cached tools without downloads; release workflows explicitly download
  missing tools and package complete integrity manifests. Git's full runtime and
  notices remain under `UpdateTransport/`; SQL bundles are under
  `UpdateSupport/Tools/SqlCmd/` and `UpdateSupport/Tools/LocalDb/`.
- `Installer/MaintenanceTools.ps1`: installation retains verified versions under
  `%ProgramData%\FMSC_Updater\MaintenanceTools`, outside application activation,
  rollback and transaction cleanup. Update checks use an absolute verified Git/SSH
  pair from this protected cache. Activation/recovery resolves sqlcmd there too.
  Missing/corrupt bundles are restored from the application, then approved HTTPS
  sources if necessary, independently of Git and the original setup folder.
  Machine-cache repair can require UAC; unchanged tools require no rewrite/download.

**Removed:** owner-only `Tools/UpdateSigning`, RSA signature generation,
`update-manifest.sig`, pinned publisher public-key resource, recovery backup
requirements, and all signing-key parameters from packaging and snapshot CLI.
The school's **SSH authorization key is separate** and is still required.

## Remaining production gates

1. Test real private Git downloads, revoked deploy keys, network interruption,
   oversized archives, Windows file-lock behavior and constrained disk space.
2. The Windows x64 release builds now pin and SHA-256 verify Git for Windows
   MinGit 2.56.0(4), and include its entire Git/OpenSSH runtime inside both
   the application-only update and complete installer. Publishing fails when
   required tools are absent. Validate first-install and private SSH transfer
   behavior on a clean Windows school/test PC before production rollout.
3. Complete field acceptance of reconnect/resume under actual network disruption.
   Verified chunk caching, bounded retries, live transfer figures and a determinate
   download bar are implemented and covered by fault-injection tests. Close cancels
   preparation and preserves verified chunks. The single centered Close button
   dismisses this manual check (it does not schedule a notification).
4. Complete disposable Windows/LocalDB acceptance of the connected installer
   handoff, including actual pre-migration backup, migration and rollback recovery.
5. Accept bounded-history publication against the private distribution before
   a live release. The owner approved retaining only two versions without
   accumulated distribution Git ancestry. The exact-head lease guard blocks
   concurrent writers; extra branches or tags block rotation, not disappear.
6. Run release-readiness, Windows UI and full integration tests before asking
   for merge or installation at any school.

No GitHub source visibility change, database migration, update publication,
live installation change or app/schema version reset has been performed.

## Owner publication review and atomic push

### Normal developer path: one manual GitHub workflow

After the code is approved on `FMSC-Source/main`, the owner uses
**Actions → 04 - Build and Publish Private Update → Run workflow**.
The default `publish_update=true` is the **single-click release path**:
version preparation, private Sandbox SQL/installer/chaos acceptance, Windows
build and packaging, and final private-feed publication happen in ONE
serialized workflow run. `publish_update=false` is a safe artifact-only
build of the already accepted main SHA; it never increments a version,
dispatches Sandbox or publishes an update.

The workflow reads `channel/stable.json` from the private feed. If Source
still has the published version, it increments ONLY the patch application
version in `Installer/core.ps1` and fast-forward commits the change to
Source/main. `FMSC_SMS.csproj` already derives assembly version metadata
from that installer assignment. If a previous workflow failed after preparing
a not-yet-published next patch, a rerun reuses its exact version rather than
bumping twice. Unexpected version drift, a moved Source main, missing token,
or schema mismatch is refused; the database schema is never automatically
incremented. For a new database migration, add the next numbered SQL file
and update **only** `$DatabaseVersion` in `Installer/core.ps1`.
The C# application schema is generated automatically during compilation;
`DatabaseHelper.cs` requires no manual edit. Workflow 04 enforces a complete numbered migration
inventory and the private Sandbox automatically upgrades independent copies
of frozen schema-3/7 MDF/LDF fixtures to that declared target, tests both
real installer paths, and fault-injects the newest migration. SQL failure,
missing files, schema-contract failure, or rollback failure blocks
publication. Frozen fixtures never change and no manual Sandbox workflow
edit is needed for a routine schema increment.

**One-time credentials (not workflow inputs):**

- In **FMSC-Source** Actions secrets, retain
  `FMSC_UPDATES_PUBLISH_TOKEN`, a fine-grained token restricted to
  **FMSC-Updates** with **Contents: Read and write**. It reads the stable
  pointer during preflight and authorizes only the final atomic feed push.
- Add `FMSC_SANDBOX_DISPATCH_TOKEN` in **FMSC-Source** Actions secrets,
  restricted to **FMSC-Sandbox** with **Actions: Read and write** and automatic
  Metadata read. It only dispatches the existing trusted
  `private-release-acceptance.yml` workflow; no Sandbox Contents write,
  fixture access or Sandbox read token is needed.
- Keep `FMSC_SOURCE_ATTEST_TOKEN` in **FMSC-Sandbox** (scoped to
  **FMSC-Source Commit statuses: Read and write**). This is separate from
  both Source secrets. The preparatory Source job needs
  `GITHUB_TOKEN` Contents write to fast-forward its one-file version bump;
  branch rules must allow this trusted owner-triggered action. If not, it
  fails closed and the private feed remains unchanged.

The private Sandbox dispatch receives only the **new exact Source commit
SHA**. It tests both historical/current SQL contracts and both real
installer paths, and must also pass historical migration fault injection.
The Sandbox attests that same Source SHA to a public Source commit status.
Workflow 04 requires a **new** successful status from the dispatched run,
checks its trusted creator and run reference, verifies Source main still
points to that SHA, then packages the identical revision. The gate runs
again immediately before the publisher uses its separate credential.

Missing acceptance, failed chaos, stale Source SHA, invalid status,
unavailable dependencies, missing secrets or failed packaging blocks
publication. Git distribution is changed only by the publisher's final
exact-head, lease-guarded replacement of `FMSC-Updates/main` with the verified
parentless release snapshot. **One click is not a distributed
all-or-nothing transaction:** an attempted release may leave its harmless
Source version-bump commit after a failure, but it does not partially change
the private school update feed; rerun after correcting the failure.

Never paste tokens into source, workflow inputs, school enrollment or the app.
School PCs continue using individual read-only SSH deploy keys.

The workflow builds/tests the source, publishes the self-contained application,
fetches the current private snapshot, reconstructs and verifies its full package
as an incremental baseline when schemas match, and uses the existing packaging
and distribution tools. If schemas differ, it prepares a full update. It verifies
every chunk/ZIP and reconstructs the complete target from both new archives before
publication. The log/summary identifies version, schema, sizes, retained/retired
versions and expected Git parent before the final atomic push. Concurrent runs are
serialized; a concurrent external distribution update is rejected without force.
Failures before the push leave the current school feed unchanged. A remote
branch movement or unexpected tag/branch rejects the rotation rather than
silently rewriting a competing publication.
The version-preparation step may commit the next application version to
Source/main; the workflow never changes a school database.

The owner publishing credential is only present in the trusted feed preflight
and final publisher steps, never in the SQL/installer/chaos jobs or any artifact.
It is not saved in a remote URL, application or downloadable artifact. The public source
workflow retains its existing current-application artifact. Private baseline packages,
history and distribution snapshots stay in runner temporary storage and are not
uploaded as public artifacts. GitHub no-reply commit identity avoids email-privacy
push rejection. Buffered HTTPS publication supports the approved large chunks.

On the school side, publication does nothing until someone manually selects
Account → Check for Updates. There is no automatic download or forced installation.

The package publisher is covered by offline trigger refusal, real package
reconstruction and local atomic-publication regressions. Source-to-Sandbox
dispatch/version orchestration must be validated via the first owner-triggered
Workflow 04 run; merging workflow code alone does not publish an update.

### Local CLI alternative

After preparing a verified two-version snapshot with the existing CLI:

```powershell
dotnet run --project Tools/UpdateDistribution -c Release -- --prepare-publication <snapshot-directory> --workspace <new-review-directory>
```

This fetches distribution `main` into a new disposable Git workspace. It
requires the snapshot to advance and retain the remote's current latest
version. Review the printed additions/deletions (including the retired version),
stored-object size and expected parent commit. Nothing is pushed at this step.
The owner uses their own Git write authorization, never a school's deploy key.
Configure the owner's normal Git commit identity before the next command.

Only after approval of that exact snapshot, the owner runs:

```powershell
dotnet run --project Tools/UpdateDistribution -c Release -- --publish-reviewed <review-directory> --expected-parent <printed-commit>
```

The tool revalidates manifests, chunk sizes, chunk hashes, combined archive
hashes and allowed files. It prepares a parentless commit containing only the
latest two releases, then publishes using `--force-with-lease` pinned to the
exact reviewed `main` commit. A competing publication, changed review tree,
extra branch or tag is rejected; no unguarded force push is permitted.
After a rejected push, prepare a fresh workspace against the current remote.
Only the private distribution branch history is rotated; Source and Sandbox
history, installations, databases and backups are not touched.

The **1 GiB publication budget** measures files in the new, verified two-version
snapshot, not historic Git objects imported from the prior distribution.
The new branch has one parentless commit after each publication. GitHub may
retain unreachable old Git objects until its own garbage collection, so this
is a bounded *reachable-history* policy rather than a promise of immediate
GitHub storage reclamation. Existing school clients shallow-clone `main`
and fetch latest full update parts; they need no prior commit ancestry.
An update download already in progress during branch rotation may need to
reconnect and retry after receiving a stale-object error.

## Verification on 4 November 2026

- Release solution build: passed, zero warnings/errors. Standalone tool sources
  are excluded from the desktop project, including stale removed signing-tool output.
- Packaging/preparation regressions: passed (matching delta, older full update,
  corrupted delta full fallback, schema gate, isolated staging).
- Snapshot/publication regressions (4 November baseline): passed against
  disposable local Git remotes, including two-version retention, no push during
  review, competing-publish rejection, corrupt parts and unexpected files.
  The new parentless-history rotation has its own repeated-release, exclusive-ref,
  guarded-push and shallow-client regression checks (run in current CI).
- Windows authorization: 21 checks passed after correcting inherited directory
  permissions. Credential and transient plaintext SSH directories now allow
  only the current Windows user before private key material is written.
- Installer migration and LocalDB recovery contract suites: passed. These are
  offline safeguard checks, not real SQL migration/recovery acceptance.
- Windows post-fix control suite: failed communication tab navigation focus
  assertion in `Program.Communication.cs`. The assertion checks deferred focus
  transfer in `Core/TabStrip.cs`; it remains unresolved. No communication or
  tab navigation code was changed for the updater.
- Python release checks could not run: Python is unavailable on this machine.

## Connected installer handoff (follow-up implementation)

- Published applications now include `UpdateSupport/core.ps1` and all numbered
  migrations. Their bytes are covered by the same full/incremental file manifest.
  The production packaging CLI refuses version/schema values that differ from
  the published application DLL and bundled installer, and refuses missing migrations.
- The existing update window opens without blocking dashboard work. Only one
  window is opened. Schema-changing releases can download a full package without
  running SQL during preparation.
- Install Update is enabled only after handoff validation of the staged inventory,
  installer version and schema. Dashboard unsaved/busy guards and explicit close
  confirmation precede launch; the normal close guard remains active.
- The existing installer runs in an elevated external PowerShell process, waits
  for the original process identity to exit, and refuses another Windows user.
  If closure is cancelled, it times out without replacing files or running SQL.
  Automatic handoff supports the existing standard installation directory only.
  Disposable Level 2 builds cannot initiate activation.
- Update mode requires the existing MDF and LDF. Its template paths are disabled;
  a missing school database fails instead of creating/replacing one. Existing
  database registration/identity checks, verified pre-migration backup, sequential
  numbered migrations, compatibility check and folder activation/rollback are reused.
- Hash/inventory checks run again after process exit and against the installer copy
  before migrations. An exclusive installer mutex prevents concurrent activation.
  Restart remains the existing installer's success-only user choice.
- New offline tests cover process timeout/identity/user refusal, actual process-exit
  waiting, installer version/schema mismatch, changed/unlisted files, path traversal
  and database-payload refusal. They never launch activation or connect to SQL.
- Release build, authorization/handoff fixtures, migration and LocalDB safeguard
  checks passed. The disposable full PortableRelease publish passed, including
  installer and migration content checks. The local post-fix control rerun passed
  all 1,973 checks; the earlier communication-focus failure did not reproduce.
- Desktop CI run `41854489676` for the preceding commit passed every step,
  including Python checks, post-fix controls and admission/finance regressions.

**Not production complete:** The handoff is implemented and offline checks pass;
real activation/migration/recovery acceptance remains unverified. The later
MinGit bundling change still needs a successful release artifact and clean-PC
private SSH test; physical network-disruption and recovery acceptance remain required.
No live installer, database or distribution package was changed in these tests.

## Local private-distribution acceptance and dialog corrections

The owner authorized publication of `1.1.0-rc.2.test.1` and installation on the
owner's test PC using its ProgramData database, with the original detached pair
untouched. Publication commit `2adfe6a47339a673dea3bbd8b2e5d6495488e5f6` in the
private distribution was verified against the approved archive hashes. The real
school transport downloaded the incremental archive, installed all 391 expected
files and preserved 391 verified baseline files for rollback. The owner confirmed
existing records open and the manual checker reports up to date. Schema remained
76: this did not test actual SQL migration, backup restoration or rollback activation.

The owner's test exposed modeless dismissal and owner-close reentrancy defects.
Release `1.1.0-rc.3` fixes Close and Remind Me Later with explicit close handlers,
replaces the update window's FormClosed installation callback with a guarded
request, and launches the installer only after the dashboard accepts closure.
Cancelled confirmation or closure clears the pending update; ordinary later
closure cannot start it. The work guard runs once and duplicate requests are
suppressed. The dialog now separates version/download status, optional computer
access details and a single centered dismissal action. A focused SQL-free WinForms suite reproduces
the owned-window close path and tests exactly one inert installer callback.

The owner has approved merging the source after these corrections. Further package
publication remains owner controlled. Bundled Git/SSH on a clean PC, disrupted-network field acceptance,
full transport acceptance and actual migration/recovery tests remain production
verification limitations; successful incremental testing does not close them.

## Download progress and recovery

The user-visible stages are Downloading → Verifying → Ready to Install in FMSC,
then Installing → Completed in the external installer after FMSC closes.
The percentage explicitly measures **Download**, never installation. A 100%
download continues through archive, staged-file and installer-handoff validation
before Ready to Install. Installation requires the user's separate action and
safe shutdown. Completed is shown only after activated-file and database checks.

Prepared updates launch elevated Windows PowerShell with its console hidden and
show a small Installing progress window instead. Existing installer pauses and
Y/N restart prompts are skipped for this path; ordinary full installation keeps
its interactive behavior. After verified activation, restart is delegated to the
existing Windows desktop shell, then the installer window and process exit.
Windows administrator consent and FMSC's existing safe-close confirmation remain.
Failure shows an attention dialog with the log and recovery paths, without
restarting FMSC. A missing desktop shell after successful installation asks the
user to open the normal shortcut instead of launching an elevated application.

### Interrupted installation recovery

Prepared installation now makes a verified protected snapshot under
`%ProgramData%/FMSC_Updater/transactions/<guid>` and installs a stable desktop
launcher outside the replaceable application directory before migrations run.
The shortcut targets a small GUI-subsystem `FMSC.Launcher.exe`, which starts the
existing PowerShell recovery script with `UseShellExecute=false` and
`CreateNoWindow=true`. Unlike hiding an already created PowerShell console, this
prevents the startup console flash. The bootstrap is compiled during the Windows
build and included in the hashed package; schools need no compiler or extra
runtime beyond the Windows Framework already used by Windows PowerShell.
Only Administrators/SYSTEM may modify recovery storage; users have read/execute
access. Links and weakened ownership/permissions are refused. The owner-published
installer/helper SHA-256 values are recorded locally; no publisher signing key is
introduced. Recovery uses no network or additional database.

The atomically replaced, flushed `recovery.json` records Prepared, Migrating,
Activating, Verified and Completed. Activation intent is committed before either
directory rename. The previous executable's SHA-256 is recorded before migration.
**Prepared, Migrating and Activating are uncommitted**: a failed or interrupted
update restores the previous executable from the protected rollback folder and,
when committed migrations advanced the schema, restores the original verified
database backup. The staged application is discarded; the downloaded package
may remain for later inspection. No migration is wrapped in a new cross-script
SQL transaction. A separate durable verified manifest survives partial cleanup.
**Verified is the commit point**: recovery completes the successfully verified
new application rather than downgrading it. Completed permits normal startup.
The next update removes old completed/orphaned protected snapshots.

The desktop launcher requests elevation to resume an unfinished update. Direct
application startup also checks local pending state before accessing the school
database and redirects to recovery. An active installer blocks concurrent startup;
the original Windows user is required for the same LocalDB identity. Invalid state,
modified recovery scripts, missing payloads or failed compatibility checks stop
with recovery instructions instead of opening an uncertain application.

Each numbered migration remains an independent SQL transaction. The original
pre-migration backup path/hash, database identity, application executable SHA-256
and expected schema are journaled for rollback. Before an automatic SQL restore,
recovery checks the verified backup, matching database identity and file paths,
unverified transaction phase, installed app digest and concurrent SQL sessions.
It also retains an independently verified backup of the partially migrated
database before replacing it. An uncertain baseline, damaged backup, busy SQL
database, inaccessible files or failed restore **blocks startup and requires
supervised recovery** instead of silently discarding potentially newer school
records. Power loss during a SQL restore or disk/controller failure may still
require manual intervention; do not mistake all-or-nothing *intent* for an
unconditional guarantee. Full first-time/manual complete setup is a separate
installation path and is not included in this prepared-update rollback contract.

`TestUpdateRecovery.ps1` actually kills child processes around directory renames
and during cleanup using disposable folders. Restricted local runs substitute a
fixture owner; elevated Windows CI checks production Administrators ownership.
`TestUpdateRecoverySql.ps1` is guarded to approved hosted disposable runners and
tests interrupted committed migrations, transactional failure, forward resume,
backup restoration into a second fixture **and** in-place pre-commit rollback on
an explicitly owned disposable SQL database (never FMSC_DB), with the original
application digest verified. Disk/controller failure and complete-machine-loss
recovery are separate limits; journaling cannot replace external backups.

The complete Windows installation acceptance also invokes
`TestPreparedUpdateRecovery.ps1` against its already owned synthetic FMSC install.
Only the private fixture copy is instrumented: it pauses after the old-directory
rename, is forcibly terminated, and then the real stable launcher elevates the
recovery script. The test verifies the original executable hash, terminal
Completed/Aborted journal, removal of the rollback/staging directories and
retention of unrelated user files. The application is **not restarted after a
failed update**. The activation mutex is released before normal subsequent
startup, so the older application can open only after the baseline is verified.

Workflow 02 defaults to complete acceptance. Its optional `sql` test scope restores
the pinned test tooling and runs the billing and uniquely named disposable
migration/backup fixtures without rebuilding the Windows installer. Recovery tests
include AUTO_CLOSE databases, both PowerShell JSON integer representations and
UTF-8 download paths. None of these test modes publish an update or access school
database files.

The dialog displays download percentage, received/total bytes, remaining bytes,
network speed, elapsed time and estimated time remaining. Git's network pack
progress supplies speed measurements; local `git show` output is never treated
as network throughput. ETA is approximate and disappears while waiting, retrying,
verifying or staging. A half-second heartbeat keeps elapsed time moving during
quiet periods. Complete download remains distinct from validated installer readiness.

Network failures retry the current part with 2/4/8/16-second backoff, stopping
after five attempts. SSH keepalives detect dropped connections. Revoked keys,
repository authorization refusal and host-identity errors stop without retry loops.
Cancellation interrupts both the transfer and backoff; no running application file
or SQL operation is modified by recovery.

SHA-256-verified chunks are atomically retained under the current user's
`Updates/download-cache/<archive-sha256>` directory. A later attempt or reopened
dialog rechecks every cached chunk and fetches only missing/damaged ones. Incomplete
incoming files are discarded. Cache paths cannot overlap the live app or use links.
Git cannot resume a partly received blob at a byte offset: an unfinished chunk
restarts. New packages therefore prefer 16 MiB chunks; the existing 90 MiB maximum
remains accepted for older published packages. No manifest/version reset is needed.

Offline tests cover interrupted transfer, verified resume in a separate attempt,
damaged cache, retry exhaustion, backoff cancellation, revoked authorization,
speed/ETA arithmetic, stalled connections and full-fallback size/method changes.
The explicit `UpdateAuthorizationChecks --live-transfer` probe fetched a small
retained private part (1,777,043 bytes), observed real network progress and verified
its published hash using the existing computer key. The real download coordinator's
second attempt reused that verified cache without issuing another network request.
This probe is opt-in and is
never run by credential-free CI. It does not install or access SQL. Larger sustained
transfers and physical connection-loss acceptance remain separate field tests.

## Successful installation cleanup

After activation, the installer compares the installed application with its source,
checks the prepared manifest again and runs the existing database compatibility
check using the installed executable. Only after these checks succeed does it
remove the fixed `.rollback` application directory. Verification failures retain
the previous application and downloads for recovery. After successful
verification, the installer retains the newest two pre-migration backups in
`C:\ProgramData\FMSC_Database\Backups` and verifies both before deleting
older files with the installer’s pre-migration naming pattern. If verification
fails, all backups are retained. Regular school backups are outside this policy.

After a successful, verified application activation, retain the latest
`prepared-<guid>` package used for that installation. Prune older GUID-named
prepared packages in the current Windows user's `FMSC/Updates` directory and
redundant SHA-256-named chunk-cache directories. Failed/interrupted updates never
trigger this pruning. The computer enrollment key, other unrecognized files,
database files, and database backups are outside its scope. Exact canonical
directory checks and link refusal guard recursive deletion. A locked file
produces a cleanup warning rather than reversing a verified installation;
the next successful update can retry pruning. Disposable-folder checks cover
failed-update retention, the latest package, old packages, chunk-cache cleanup,
locked files, scope refusal, and preservation of enrollment and database backups.
