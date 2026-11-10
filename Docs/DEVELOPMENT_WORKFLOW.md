# Development, Code Review, and Documentation Workflow

These instructions are for maintainers and Codex working on FMSC_SMS.

## Owner-selected verification scope

Desktop, SQL lifecycle, Level 2 and disposable installation workflows remain manually
runnable. Security checks also run on main pushes and pull requests. Publishing a
`release/*` branch explicitly starts the Release Readiness workflow, composing
security, Windows component tests, real SQL scenarios and disposable installation.
Ordinary feature branches do not start the full release matrix. No workflow uses
production school data or deploys to a school PC.

For small UI-only changes, inspect the affected code and use only a relevant focused check when needed. Do not repeat full regression, SQL, performance, installer or interactive desktop suites for a button visibility or spacing change. Choose broader checks for the affected behavior when a change touches data, payments, security, recovery or deployment. Record what actually ran; manual-only workflows do not imply passing verification. This owner-selected scope takes precedence over the generic check lists below.

## One change at a time

1. Begin at the latest `main`, check that the workspace is clean, and read [AGENTS.md](../AGENTS.md), [architecture](ARCHITECTURE.md), and [master audit](../Audit/FMSC_MASTER_AUDIT.md).
2. Create a descriptive branch for the task (example: `fix/fee-save-recovery`, `docs/operator-guide`). Do not combine unrelated refactors with a behavior fix.
3. Reproduce the reported scenario and locate existing validation, busy/dirty, transaction or recovery helpers before adding new code.
4. Implement the **smallest compatible change**. Preserve student data, finance logic, SQL security, and UI navigation/discard semantics; explain any migration explicitly.
5. Add focused regression tests that reproduce the original problem; do not disable failures or claim tests were executed when they were merely compiled.
6. Update the [user guide](USER_GUIDE.md), [installation](INSTALLATION.md), [recovery](BACKUP_RECOVERY.md), and [architecture](ARCHITECTURE.md) **only where the behavior actually changed**. Add a concise entry to [CHANGELOG.md](../CHANGELOG.md) under Unreleased.
7. Open a pull request using [.github/pull_request_template.md](../.github/pull_request_template.md) and record actual command output, CI links and remaining manual checks.
8. Review the diff and Windows CI status before merging. Branch-protection/ruleset enforcement, if desired, must be configured separately in GitHub Settings; a template alone does not enforce checks.
9. After merging, re-run the complete installer workflow when the release candidate is frozen. Only a verified artifact and test-install outcome can advance release readiness.

## Pull-request description expectations

Every PR should answer: **Why is the change needed? What behavior changed? Which screens/database operations are affected? Why will existing workflows/data remain safe? What tests passed (exact commands/results)? What remains untested?**

For SQL changes, include schema version, affected procedures/tables/migrations, transaction boundaries, idempotency and rollback implications; attach evidence from a disposable database only.

For UI changes, include the before/after operator sequence, dirty-state/busy-state handling, permissions and relevant Windows screen or DPI acceptance.

For installer changes, distinguish a passing publish regression from a successful private installer ZIP, and a successful ZIP from real-school installation approval.

## Source comments

Keep comments brief and explain intent, safety constraints or business policy. Record accepted owner decisions beside the relevant C# implementation, with the audit ID where useful; preserve the historical audit evidence. Distinguish accepted policy from proposals and unverified findings. Avoid obsolete fix narratives or comments that repeat the code.

## Changelog conventions

Use headings `Added`, `Changed`, `Fixed`, `Security`, and `Documentation` as appropriate under `## [Unreleased]`. Link changes to PRs or commits. When tagging a version, move release entries into `## [x.y.z] - YYYY-MM-DD` only after release acceptance; avoid claiming historical release dates or production readiness from CI alone.

## Data boundaries

Never commit or upload real `.mdf`, `.ldf`, `.bak`, screenshots of student records, staff passwords, live connection secrets, or sensitive logs. Use synthetic/disposable SQL fixtures. Never replace an installed school database with a development DACPAC.

## Lightweight local checks (Windows)

```powershell
dotnet restore FMSC_SMS.sln
dotnet build FMSC_SMS.sln -c Release --no-restore
dotnet build Tests/ThemeSmokeChecks/ThemeSmokeChecks.csproj -c Release
dotnet run --project Tests/ThemeSmokeChecks/ThemeSmokeChecks.csproj -c Release --no-build -- --post-fix
dotnet run --project Tests/AuditFixChecks/AuditFixChecks.csproj -c Release
dotnet run --project Tests/PerformanceChecks/PerformanceChecks.csproj -c Release
```

For Windows PowerShell and disposable publish tests, see [CI workflow](../.github/workflows/fmsc-desktop-ci.yml). SQL integration must target disposable clones, not the production database.

Avoid direct merges that bypass review for sensitive changes. A Codex PR is a proposal until reviewed and merged; neither tests nor documentation automatically imply production sign-off.

## Level 1 SQL integration

The separate [Level 1 lifecycle workflow](../.github/workflows/fmsc-lifecycle-sql-ci.yml) provisions an owned disposable SQL Server container and fictional school. See [LEVEL1_TESTING.md](LEVEL1_TESTING.md) for real admission-to-promotion SQL assertions, data-free SQL fixture validation, cleanup, evidence and local execution. Its passing result complements Windows CI; it does not approve interactive UI or production deployment.

## Security checks

The SQL workflow publishes the current schema-only DACPAC into a separate owned catalog for school scenarios, and tests upgrades using the pinned record-free fixture. It does not refresh exports or certify the owner's MDF/LDF snapshot. Review schema changes independently and keep the pinned fixture under separate review when its tested baseline changes.

[FMSC Security Checks](../.github/workflows/fmsc-security.yml) is read-only and runs on main pushes, pull requests, manual dispatch and release verification. It downloads **Gitleaks CLI 8.30.1** from its official release and verifies a pinned SHA-256 before running it. The CLI is MIT-licensed and works with private repositories without a Gitleaks action license, PAT or GitHub Advanced Security subscription. Normal Actions minutes/storage limits still apply.

`Tools/check_security.py` checks fetched Git history, working source files and extracted historical DACPAC XML, including secrets or forbidden files added and subsequently deleted. It blocks `.mdf`, `.ldf`, `.ndf`, `.bak`, private key containers, `.env` files, `secrets.json`, `credentials.json`, production/local appsettings and private publish settings. Safe `.env.example`, `.env.sample` and `.env.template` names are allowed, but their contents are still scanned. `.gitignore` helps prevent accidental additions; it cannot protect files already tracked or force-added.

The scanner's default token/private-key rules remain enabled. Additional rules catch literal passwords and SQL connection passwords. The only project fixture exceptions are the exact existing dummy values in the specific test files listed in [.gitleaks.toml](../.gitleaks.toml). Other contents of those files remain scanned. Inline allow comments and `.gitleaksignore` files cannot silence this workflow; any rule/exclusion change needs review.

To check locally, install the official Gitleaks 8.30.1 CLI for your operating system and use Python 3.11 or newer (CI uses 3.12):

```powershell
python Tools/test_security.py --gitleaks C:\Tools\gitleaks.exe
python Tools/check_security.py --gitleaks C:\Tools\gitleaks.exe
```

The tests create harmless synthetic fixtures in temporary repositories and delete them afterwards. Do not commit a real secret to test detection. The local check scans history and tracked/unignored source files, including unstaged edits. Ignored local configuration and generated output are outside the working-source snapshot; any tracked ignored files remain scanned.

**When a security check fails:**

1. Look at the reported file, line and rule, or forbidden file name. Credential values, source excerpts and raw scanner output are withheld; reports are temporary and are never uploaded.
2. If a real credential was exposed, revoke/rotate it immediately. Remove the source value and use local private configuration or environment variables. Do not paste it into a PR, issue, screenshot or log.
3. If it is in Git history, ask the maintainer to plan cleanup. Removing the latest line alone does not clear a full-history finding. Do not force-push shared history or add broad exceptions to make CI green.
4. For a genuine synthetic false positive, propose a narrow, documented value-and-path exception and retain a test proving real secrets in the same file are still caught.
5. A scanner/configuration/download failure also fails the check. It is not a clean scan; fix the tool setup and rerun.

These are heuristic text-secret/file-name checks, not a guarantee of finding every secret or personal school record. Do not commit data-bearing DACPAC exports; the record-free test schema is kept separately for isolated SQL tests. Review the pinned Gitleaks version/hash periodically; Dependabot does not update the downloaded CLI. No repository visibility, paid service or branch-protection setting is changed. GitHub Actions must be allowed; maintainers may separately require the **Secrets and sensitive files** check in branch rules.

## Reviewing Dependabot updates

[.github/dependabot.yml](../.github/dependabot.yml) currently pauses routine NuGet and GitHub Actions version-update PRs with `open-pull-requests-limit: 0` in both ecosystem entries. This leaves existing dependency PRs open and does not change GitHub security-alert settings, separate security-update PRs (if enabled) or secret scanning. No dependency versions or target frameworks are changed.

To resume, restore both limits to `3` and merge that configuration change into `main`. The retained schedule checks NuGet at the project root, `Tests/*`, `Apps/Host` and `Apps/Worker`, and GitHub Actions weekly on Monday, grouping minor/patch updates. Major updates remain separate proposals. The pause has no automatic expiry, and there is no auto-merge configuration.

For each update PR, read release/breaking-change notes, ensure the package still supports **.NET 8 Windows Forms**, and inspect every manifest change in a grouped update. Require the existing Windows CI and security checks to pass. For reporting/SQL-client dependencies, also perform the relevant Windows/report/disposable-database acceptance before a release. Merge manually after review or defer/close the proposal; a minor/patch label alone does not prove compatibility.

## Communication module development

See [COMMUNICATION_CENTER.md](COMMUNICATION_CENTER.md) before changing the optional SQL add-on, provider integration or queue. CI uses fake HTTP transports and a separately owned disposable SQL database; no live messaging or school data is permitted. Run `Tests/CommunicationChecks` and the Level 1 harness as well as Desktop CI and security checks. The applied communication setup is retired from deployment. Its embedded reference definition lives at `Services/Communication/Schema.sql`; only guarded disposable tests install it. The installer discovers numbered SQL files in `Migrations`, skips an empty folder or already-applied versions, backs up before pending migrations, and verifies the full database contract before activation. **Historical, pre-production communication branch only:** schema 21 and migrations 20/21 belonged to an earlier development sequence, not the active runtime target. Read the current required schema from `Installer/core.ps1` and the numbered production migrations from `Migrations/README.md`. The runtime compatibility contract remains enforced. Provider registration, hosting and production schema deployment require separate owner acceptance.
