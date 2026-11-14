# Established release packaging decision

The user supplies the latest detached `FMSC_DB.mdf` and `FMSC_DB_log.ldf` in the
`Database` folder adjacent to the project folder (`../Database`) when preparing
a release package. The publish configuration validates and ships that pair.
This is an established, user-owned release preparation step.

Do not treat the absence of those files during development or a readiness review
as a release blocker, an overlooked requirement, or something to repeatedly ask
the user to provide. Check their presence and validity when actually preparing
or validating the release package.

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
fees. Do not reintroduce it without a new explicit owner decision. The owner subsequently approved permanently dropping the cancellation columns
from code and SQL after restoring a clean schema-48 database. Migration 52 removes
them; refuse other snapshots containing cancelled copies rather than reviving them. The normal flow uses Create, Save Changes before collection,
Print, Mark Collected and Issue Again.


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
