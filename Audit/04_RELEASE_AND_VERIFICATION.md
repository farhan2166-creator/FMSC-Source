# 04 — Release and SQL verification

The original first-production baseline was application 1.0.0/schema 1. As of the **8 November 2026** audit, current source is **application 1.0.16/schema 10**, but the private published **1.0.16** artifact was built from an **earlier exact source commit**. Do not infer deployed code from a version string. Separate source inspection, trusted private release provenance, installed executable bytes, live database schema, native UI and owner acceptance. See [Audit Issue 04 evidence](Reviews/FMSC_RELEASE_INSTALLED_ACCEPTANCE_2026-11-08.md). Older schema-20/21/69/75 records remain historical.

## Full records and their purposes

1. [FIRST RELEASE BASELINE](../Docs/FIRST_RELEASE_BASELINE.md)
2. [RELEASE CHECKLIST](../Docs/RELEASE_CHECKLIST.md)
3. [INSTALLATION](../Docs/INSTALLATION.md)
4. [BACKUP RECOVERY](../Docs/BACKUP_RECOVERY.md)
5. [FMSC MASTER AUDIT](FMSC_MASTER_AUDIT.md)
6. [RELEASE VERIFICATION 2026 11 01](../Docs/RELEASE_CHECKLIST.md)

## Retention and use

This is a numbered, consolidated subject document, not a copy of the source reports. Read the linked records for details and original evidence. Existing filenames, decisions and audits remain unchanged; this indexing work does not change source code, SQL or test status.

## Read-only owner-side check

Run `Tools/VerifyInstalledFmscRelease.ps1` from the current source checkout on the **same Windows account** that runs FMSC. It compares the actual on-disk executable to the published 1.0.16 SHA-256, queries only `dbo.DatabaseVersion`, and—only if the executable bytes match—runs the existing read-only `--verify-installation` compatibility entry point. It neither installs an update nor inspects student/guardian data. See the [evidence guide](Reviews/FMSC_RELEASE_INSTALLED_ACCEPTANCE_2026-11-08.md) for commands and manual UI checks. A passing script proves the **older 1.0.16 published binary**, not the three newer audit fixes; those require a later exact-head publication.
