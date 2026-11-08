# Owner decisions and business invariants

Consolidated in [Business Decisions](../Docs/BUSINESS_DECISIONS.md#owner-decisions-and-invariants).
The complete dated record, decisions and verification limits are preserved there.

## Enforced or recorded application behavior

See the [preserved section](../Docs/BUSINESS_DECISIONS.md#owner-decisions-and-invariants-enforced-or-recorded-application-behavior).

## Approved target policy that is **not yet proven implemented**

See the [preserved section](../Docs/BUSINESS_DECISIONS.md#owner-decisions-and-invariants-approved-target-policy-that-is-not-yet-proven-implemented).

## UI consistency — design constraints, not database rules

See the [preserved section](../Docs/BUSINESS_DECISIONS.md#owner-decisions-and-invariants-ui-consistency--design-constraints-not-database-rules).

## Maintenance rule

See the [preserved section](../Docs/BUSINESS_DECISIONS.md#owner-decisions-and-invariants-maintenance-rule).

- **2026-11-08 — Audit Issue 01 approved:** Require structural compatibility for schema-10 messaging objects, constraints, and records before operating, without altering school data or migrations. [Decision and verification scope](Reviews/FMSC_SCHEMA10_COMPATIBILITY_AUDIT_2026-11-08.md).

- **2026-11-08 — Audit Issue 02 approved:** Compare staged application files to the published source using SHA-256 in addition to tree and file-length checks before activation; reject equal-sized corruption and preserve the active installation. [Implementation and acceptance record](Reviews/FMSC_INSTALLER_SHA256_STAGING_AUDIT_2026-11-08.md).

- **2026-11-08 — Audit Issue 03 approved:** Correct active architecture, migration, release-checklist and Communication Center instructions to reflect source application 1.0.16/schema 10, while retaining historical baseline and design records. No C#, SQL, CI or school-data changes. [Scope and checks](Reviews/FMSC_SCHEMA10_DOCUMENTATION_AUDIT_2026-11-08.md).

- **2026-11-08 — Audit Issue 04 approved:** Verify the published exact-source artifact and private feed separately from newer `main`, and require read-only evidence from the actual Windows executable, local schema, runtime compatibility and manual Communication Center UI before marking the installation accepted. No release publication or school-data mutation authorized by this approval. [Verification record](Reviews/FMSC_RELEASE_INSTALLED_ACCEPTANCE_2026-11-08.md).
