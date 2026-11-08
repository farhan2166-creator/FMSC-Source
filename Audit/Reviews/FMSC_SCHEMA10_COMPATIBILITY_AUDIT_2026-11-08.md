# Audit Issue 01 — Schema-10 structural compatibility (2026-11-08)

**Owner approved: YES.** Fail closed when mandatory schema-10 Communication Center structures are missing or incompatible.

## Verified source gap

Source `4abe2d037f9ce51c0922875e7c0241cb112ac88d` requires schema 10, but the startup/restore compatibility manifest covered only the original `dbo` baseline. A database claiming version 10 without `msg.ManualHandoff` could pass the baseline-only structural check and fail at a later operator action.

## Approved scope

Preserve the original baseline manifest, and add `Core/DatabaseCompatibilitySchema10Requirements.json` derived from migrations 9 and 10. Validate the 34 required objects, 89 columns, 74 module parameters, 3 table-type columns, 19 uniqueness safeguards, 5 foreign keys, 28 enabled/trusted checks, and 1 guard trigger using the existing read-only SQL validator. The existing typed compatibility failure stops startup/restore when structures are incompatible. Add a focused portable contract regression test.

**No SQL migration, schema writes, school records, backup files, or historical migration scripts are changed.** The existing schema-10 database should remain unaffected if its structures match. Review/test the runtime gate on a disposable schema-10 clone before production acceptance.
