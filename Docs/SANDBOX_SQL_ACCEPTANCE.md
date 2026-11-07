# Private MDF/LDF release acceptance (Sandbox-owned)

## Immutable fixture baselines

Private `farhan2166-creator/FMSC-Sandbox` retains exact synthetic-only SQL
LocalDB pairs extracted once, pinned at commit
`5a8c5130c9d7b76552bb73a753cd44c4d192d2d0`:

| Baseline | Permanent files | Acceptance |
| --- | --- | --- |
| schema 3 | `Database/v3/FMSC_DB.mdf`, `Database/v3/FMSC_DB_log.ldf` | Apply migrations 4–7, verify full contract, then test actual installer |
| schema 7 | `Database/v7/FMSC_DB.mdf`, `Database/v7/FMSC_DB_log.ldf` | Verify full contract, then test actual installer |

Check the pinned checkout's original SHA-256/size values against
`Tests/manifest.json`. Never attach or change the checked-in MDF/LDF:
**all writes, migrations, extended-property adjustments, and installer
operations must run against independent disposable copies**. Verify empty
student/user tables and a dedicated synthetic ownership marker; perform
`DBCC CHECKDB`.

These baselines are intentionally frozen as the application advances. New
SQL revisions require numbered `Migrations/N.sql` updates and a renewed
two-baseline migration/installer test. Never silently replace the fixtures.

The root `FMSC-Source/fmsc.dacpac` is for manual inspection only. Its
presence, freshness, content, checksum, and modification time are **not**
workflow prerequisites. The old, independently committed
`Tests/LifecycleSqlChecks/Baseline/schema.dacpac` is a static public smoke
fixture; it is **not** trusted release acceptance and is not refreshed by
the root DACPAC.

`FMSC-Sandbox/Latest.dacpac` and its instructions are retired and must
not be recreated as workflow dependencies.

## Trusted execution and publication

1. The owner starts **Source Workflow 04** from `FMSC-Source/main`.
   Its preflight verifies separate credentials, reads the current private
   update-feed version, and safely commits the next patch application
   version to Source/main (or reuses an already prepared unpublished
   next patch after failure). The database schema is not auto-bumped.
2. Source Workflow 04 sends **only the resulting exact Source SHA** to
   Sandbox workflow `private-release-acceptance.yml` by
   `workflow_dispatch`. A Source-side token restricted to **FMSC-Sandbox
   Actions: Read and write** can trigger this workflow but has no
   Sandbox Contents write or access to immutable fixture data.
3. On **Sandbox/main**, the trusted workflow checks out that exact
   public Source commit and the frozen private fixture commit with
   `persist-credentials: false`. The SQL jobs validate schema 3
   (migrations 4–7) and schema 7 against the full production requirements.
   Both real installer jobs must pass on independent, disposable fixture
   copies. Additionally, the mandatory `migration-chaos` job injects
   failures in versions 4 and 5 on a separate historical schema-3 copy:
   it proves transaction rollback, schema marker preservation,
   verified backup restore, and successful official migration resumption.
   Permanent MDF/LDF, backups, and school data are never published.
4. The distinct trusted **attestation** job requires successful
   schema-3/7 SQL, real-installer, and chaos jobs. It verifies that
   Source/main still points to the tested SHA, then writes
   `fmsc/sandbox-mdf-ldf/v1` success to that exact Source commit.
   On a failed or incomplete job, no success is granted.
5. **Workflow 04** waits for a **NEW** attestation posted after its
   dispatch (not an old success for a previous attempt). It verifies
   exact Source main, latest status, expected attestor creator,
   description and Sandbox run URL. Only then does it package the
   **same checked-out Source SHA**, run its build/regressions, and
   recheck the trusted status immediately before the atomic fast-forward
   publication to private `FMSC-Updates`. Missing/failing/stale
   approval blocks the final push.

The Source-to-Sandbox direction is **workflow dispatch only**;
Sandbox-to-Source direction is an exact-commit **status attestation**.
Source does not read private Sandbox fixtures, credentials or run logs;
Sandbox scripts never receive any Source publication token.
Public Source PR smoke tests are not release authorization.

## One-time least-privilege credentials

- In **FMSC-Sandbox Actions secrets**, retain
  `FMSC_SOURCE_ATTEST_TOKEN`, scoped to
  **only FMSC-Source → Commit statuses: Read and write**.
  It must remain exclusive to the final trusted Sandbox attestation job.
- In **FMSC-Source Actions secrets**, retain
  `FMSC_UPDATES_PUBLISH_TOKEN`, scoped to
  **only FMSC-Updates → Contents: Read and write**.
  It reads the feed during trusted preflight and authorizes the final
  feed publication only, never the Sandbox tests.
- Also in **FMSC-Source Actions secrets**, add
  `FMSC_SANDBOX_DISPATCH_TOKEN`, scoped to
  **only FMSC-Sandbox → Actions: Read and write** with automatic
  Metadata read. It is used only for preflight and to dispatch the
  approved workflow by name; do not grant private contents write.
- Source's GitHub Actions `GITHUB_TOKEN` must permit Contents write
  **only for the version-preparation job**. No Source-side
  `FMSC_SANDBOX_READ_TOKEN` is required.

A missing, expired or unauthorized token refuses the run. Published
feed changes are a single final fast-forward; a failed run may leave a
nonpublished Source version-bump commit, safely reused by the next run.

## Testing, limitations and release discipline

- A synthetic fixture may require resetting filesystem read-only
  flags/ACLs and converting `FMSC_QuickRegistrationScope` from a
  DacFx-persisted SQL `varchar` to a native `uniqueidentifier`.
  Apply these repairs **only to disposable copies**, never the master
  fixture. A valid GUID string alone is not sufficient.
- Installer success and matching `dbo.DatabaseVersion` are not enough:
  enforce the full production structural manifest after migrations and
  real installer execution.
- Test reports are sanitized; do not upload MDF, LDF, NDF, BAK,
  exported schools, credentials or source checkout archives.
- Baselines mostly lack user/student records. Migration *data-preservation*
  coverage beyond the deterministic synthetic records remains limited.
  Do not claim live-school upgrade, interactive GUI, physical-printer,
  reboot or clean-MSI coverage without independent evidence.
- Do not bump an application/database version or publish a release just
  to repair this gate. Retire temporary one-off diagnostic workflows
  after acceptance succeeds and commit their removal separately.
