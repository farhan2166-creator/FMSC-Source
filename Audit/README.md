# Audit — active navigation and dated evidence

The **latest approved business policy** is maintained in [Business Decisions](../Docs/BUSINESS_DECISIONS.md), not in dated audit reports. Historical audit reports record findings at their original source revisions; they do not override the current policy.

## Current deep audit (10 November 2026)

- [Deep source, SQL and Windows UI audit](Reviews/FMSC_DEEP_SOURCE_SQL_UI_AUDIT_2026-11-10.md) — pinned source findings, severity and reproduction boundaries.
- [Read-only inventory scanner](Tools/deep_source_scan.py) — enumerates every checked-out C# file, schema-only test DACPAC procedure, migrations, and Windows UI class candidates. Its output is **not** a live desktop acceptance certificate.

## Numbered audit guides

- [Status And Scope](01_STATUS_AND_SCOPE.md)
- [Finance And Credit](02_FINANCE_AND_CREDIT.md)
- [Ui And Workflow](03_UI_AND_WORKFLOW.md)
- [Release And Verification](04_RELEASE_AND_VERIFICATION.md)
- [Archive And Retention](05_ARCHIVE_AND_RETENTION.md)

## Important evidence

- [Master audit](FMSC_MASTER_AUDIT.md) — dated source/release finding summaries, not current release certification.
- [Credit lifecycle audit, 4 November](FMSC_CREDIT_LIFECYCLE_AUDIT_2026-11-04.md) — findings from the reviewed source. Compare the former automatic/backdated credit suggestions to [BD-12](../Docs/BUSINESS_DECISIONS.md#bd-12).
- [Consolidated audit reviews](REVIEW_HISTORY.md) — technical history and test limitations, retained to make prior findings traceable.
- [Archived audit sources](Archive/) and [review references](Reviews/) — source-revision evidence only.

For current application guidance use [Docs](../Docs/README.md). Documentation cleanup does not rerun verification or change real business records.
