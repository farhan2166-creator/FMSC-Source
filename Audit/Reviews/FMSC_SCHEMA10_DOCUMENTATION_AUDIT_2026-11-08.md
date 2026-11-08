# Audit Issue 03 — Current technical documentation and source baseline

**Owner decision: APPROVED — 2026-11-08.**

## Problem

Operative technical guides still claimed the current FMSC release was application 1.0.0/schema 1 or 1.0.3/schema 3, while `Installer/core.ps1` specifies application **1.0.16 / database schema 10** at baseline commit `79d25aa1cac83cf933b2c3d00e5ccce8511e4ae6`. The installed messaging schemas `9.sql` and `10.sql` were described as unpublished drafts, even though both are committed to the current migration chain. The architecture guide also named a nonexistent `Core/DatabaseMigrationRequirements.json` and described the former `comm` module as if its old records should be preserved.

## Approved documentation-only correction

- `Docs/ARCHITECTURE.md`: current source version/schema, two actual embedded compatibility manifests, migration chain, historical schema-1 reset, retired communication data and free WhatsApp manual handoff.
- `Migrations/README.md`: distinguish historical 1.0.0/schema-1 origin, current 2–10 production chain, committed schema-8/9/10 scripts, owner-approved destructive retirement, release gating and operator-reported handoff status.
- `Docs/RELEASE_CHECKLIST.md`: remove obsolete 1.0.3/schema-3 source claim, fix Communication Center acceptance and mention SHA-256 staged-file verification.
- `Docs/COMMUNICATION_CENTER.md`: reconcile old pending-PR/draft/disabled-Confirm statements with the implemented manual WhatsApp pathway; keep automatic provider delivery explicitly unavailable.
- `Docs/FIRST_RELEASE_BASELINE.md` and `Docs/COMMUNICATION_REBUILD_V2.md`: retain the historical content while prominently labeling the dated baseline/design as historical, not current release certification.
- `Audit/OWNER_DECISIONS_AND_INVARIANTS.md`: preserve the explicit approval.

## Verification and limits

The target application/schema is verified against `Installer/core.ps1`; migrations `2.sql` through `10.sql` and both manifest files are present in source. This issue changes **documentation only**. No Windows build, migration, SQL runtime or live school database changes are required. Source-state descriptions are **not a claim that release workflow 04 completed or that the local PC runs the latest executable**.
