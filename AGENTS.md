# Established release packaging decision

For an actual fresh-school installation, the owner supplies an approved,
verified **detached** `FMSC_DB.mdf` and `FMSC_DB_log.ldf` pair. A local complete
release may take these from the sibling `../Database` folder and validate/copy
both when supplied. The GitHub Actions complete-installer artifact is deliberately
**database-free**; the owner adds the pair privately to its `Database` folder
after download. Application-only updates do not replace the school's database.

Do not treat a missing MDF/LDF pair during development, CI packaging, or a
readiness review as a release blocker. Verify both files and their compatibility
when the fresh-installation folder is actually assembled for deployment. Do not
publish school database files or backups in a GitHub Actions artifact.

Assess database migration requirements against the intended deployment scenario
and the supplied snapshot. An empty migration folder alone does not establish
that a release is blocked. Installation, existing-database upgrades, and recovery
testing are separate verification questions; report concrete findings about them.

# Database change delivery

Put all required database schema or stored-procedure changes in versioned SQL
scripts in `Migrations` so the user can reapply them after restoring the original
database. Do not rely on changes made only to the currently attached database.

# Gradual audit retirement

When revising a stored procedure, remove its writes to the retiring audit system
and audit-only before/after JSON preparation. Do not make a separate sweep through
unrelated procedures. Preserve operational student/lifecycle history, bills,
payments, allocations, credit movements, migration versions, and request/recovery
markers: these remain necessary business and recovery records.

# Certificate cancellation decision — 25 October 2026

The owner removed certificate cancel/void from the application because it adds
unnecessary complexity and can imply deleting a record or undoing leaving and
fees. Do not reintroduce it without a new explicit owner decision. The owner
subsequently approved dropping cancellation columns after review of a clean
historical schema-48 snapshot; the earlier implementation record called this
migration 52. **Those are pre-consolidation history labels, not the current
production schema or migration number.** Use the numbered scripts in `Migrations`
and `Installer/core.ps1` for current deployment requirements. Preserve the
existing refusal to revive cancelled copies. The normal certificate flow uses
Create, Save Changes before collection, Print, Mark Collected and Issue Again.


# Regression tests must validate behavior, not incidental UI wording

In active WinForms smoke/acceptance tests, assert the operation and its observable
result (correct selected record, enabled/disabled permissions, saved state,
readiness, counts, cancellation, navigation, no duplicate writes, and layout
visibility). Do **not** gate an otherwise working release on an exact button
caption, label phrase, status sentence, order of cosmetic cards, or historical
screen wording. Use stable control identities, dialog accept/cancel roles,
domain values and business state instead of selecting controls by display text
when possible.

When product copy changes, preserve the behavior test and update or remove only
obsolete presentation-coupled checks. If a caption is part of an actual
contract (e.g. mandatory legal/consent notice, safety warning, accessibility
requirement, or an explicit owner-approved wording requirement), test that
contract deliberately and document why it requires exact text. Do not silently
relax those cases.

Continue to fail releases for real defects in SQL migrations and compatibility,
permission enforcement, finances, data integrity, admissions, cloud-backup
recoverability, installer/updater recovery, publication exact-commit gates,
consent, idempotency, and message delivery status. The audit script for legacy
copy-coupled tests is advisory and must never substitute for these validations.
