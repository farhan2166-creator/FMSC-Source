# Audit Issue 05 — Reject inconsistent published BUILD_INFO (2026-11-08)

**Owner request:** Define the next audit finding and implement it immediately, preserving the established acceptance process.

## Confirmed defect

The read-only Windows probe `Tools/VerifyInstalledFmscRelease.ps1` hashes the installed `FMSC_SMS.exe` and optional `BUILD_INFO.txt` against the published 1.0.16 reference. Before this fix it **reported** `DIFFERS FROM PUBLISHED BUILD_INFO` but its final success predicate **ignored** that mismatch. It could therefore return exit 0 even when a present build-provenance file disagreed with the trusted publication, provided executable/hash, SQL version and structural checks otherwise passed.

Example: published EXE is authentic, `FMSC_DB` is schema 10, but `BUILD_INFO.txt` has been replaced or corrupted. The output warned of different BUILD_INFO bytes yet the command still ended successfully.

## Change

- Reject any **present, mismatched** BUILD_INFO with exit code 2 and a clear diagnostic.
- Retain the already documented optional-file behavior: an **absent** BUILD_INFO alone is not a rejection, nor is it positive provenance evidence.
- Keep the independent executable hash, schema version, structural compatibility and process-path checks.
- Add **7 synthetic acceptance-predicate scenarios** to the existing PowerShell installer test suite, covering matching, changed, absent BUILD_INFO; modified EXE; wrong running path; wrong schema; and structural verifier failure.

## Safety and verification limits

No application binary, database, migrations, installer workflow, update feed or production release is modified. The regression checks use only synthetic in-memory values; they do not launch FMSC or connect to SQL. Passing CI at the exact PR SHA is required for Windows PowerShell execution proof. The local installed executable, actual running process, active LocalDB and Communication Center UI still require owner-side checks. The historical published 1.0.16 remains an earlier source SHA and is **not** evidence that later audit source fixes are installed.
