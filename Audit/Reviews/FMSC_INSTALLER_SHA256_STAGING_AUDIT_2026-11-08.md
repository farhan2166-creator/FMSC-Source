# Audit Issue 02 — Installer staged-file SHA-256 integrity (2026-11-08)

**Owner decision: YES.** Verify file contents before enabling a staged application, even when copied files have the same name and byte length as the published files.

## Issue

The installer `Test-StagedApplication` checked the relative directory/file inventory and file lengths. A file modified to different bytes but left at exactly the same length could be accepted and activated.

## Approved change

- Preserve the existing directory/file inventory, count and byte totals.
- For matching **files** (not directory placeholders), compare the SHA-256 hashes of published and staged paths using PowerShell's built-in `Get-FileHash -Algorithm SHA256` with errors treated as failure.
- Report the relative path of a mismatch without exposing file contents or digest values. Reject staging before `Enable-StagedApplication`; do not alter the active application or database.
- Keep the existing post-activation verification path, which calls the same `Test-StagedApplication`, so the strengthened verification also applies there.
- Add no software dependency, app redesign, SQL migration or change to the installer workflow configuration.

## Regression evidence

The existing `Tools/TestInstallerMigrations.ps1` CI suite now exercises the actual two production verifier functions on uniquely owned temporary files. It tests identical copies, same-size executable corruption, same-size nested-asset corruption, size mismatch, missing file, unexpected file and unchanged active-install sentinel. This uses **synthetic bytes only**, with no SQL connection or installer launch.

**Acceptance status:** CI verification and branch merge outcomes must be recorded after workflows finish. An installer SHA-256 source-to-staging comparison is a copy-integrity check and does not itself authenticate where the original published source came from.
