# FMSC — Windows acceptance for audit items 7.2, 7.4 and 7.5

Status: **CI regression gates added; manual school-PC acceptance not yet performed.**
This checklist does not authorize modifications to any real school database.

## CI checks (exact pull-request revision)

- Linux SQL lifecycle Level 3: validate new schema-11 signature and schema-12 Administrator recovery structural manifest; temporarily hide each object/field on a disposable catalog and verify fail-closed startup compatibility.
- Windows Workflow 09 / Workflow 04: run the prepared-message and manual WhatsApp workflow fixture, all report caption modes, and certificate PDF rendering, in addition to the existing update-dialog, recovery and installer tests.
- Private Sandbox still owns frozen schema-3/schema-7 real-installer/migration-chaos acceptance **only after** merge and owner-triggered Workflow 04. PR CI cannot authorize publication or school deployment.

## Actual Windows test-PC checklist (disposable school data only)

1. Record the exact executable path, application build SHA, installed SQL schema and database identity before beginning. Test the **installed binary** rather than a Debug build.
2. On a disposable copy of the historical database, test clean upgrade and an injected failed second migration. Compare original executable SHA and database identity/schema after rollback. Check the staged folder, retained SQL safety backup and recovery journal. Never inject faults into school data.
3. Register an approved test-host device, sign in successfully as the sole Administrator, verify the recovery-host binding, then test recovery with the same elevated Windows/LocalDB owner and online, authorized private Updates SSH key. Confirm the original user ID/roles remain and the old login stops working. Negative tests: remote client, wrong SID/key, offline/revoked key, second Administrator.
4. Render SLC and CLC on 100% and 150% Windows scaling with each paper mode. **Preprinted letterhead** must retain the original report and 2-inch / 2.5-inch margins; **plain paper** must show header and footer and remain on one page. Test optional principal signature both on and off in PDF and print preview.
5. With fictional fee records, confirm two linked, consenting siblings sharing a verified WhatsApp number and having positive in-scope balances produce a single operator-controlled chat with both identifiers and the correct sum. A reused unrelated contact, declined consent, missing fees, out-of-scope campus and withdrawn consent must never expose another student's information or launch an automatic send. The operator must press Send in WhatsApp.
6. Record screenshots of each printer preview, recovered app and database versions, CI run URLs and the observed test outcome. Do not upload real student names, phone numbers, SQL backups, SSH credentials or private school files to public Actions.

**Acceptance boundary:** Green GitHub CI proves the automated fixtures, not a real Windows printer, live GitHub key revocation or school installation. Keep these three field checks explicitly unverified until they are run on a safe test PC.
