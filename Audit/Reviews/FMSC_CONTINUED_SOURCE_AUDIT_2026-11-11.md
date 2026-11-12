# Continued source audit — 11 November 2026

Reviewed revision: `eb12f5ceaa99c9c497955b30319f34626bfde123` (merged main).

Scope: a second pass through certificate creation/collection/reporting and cloud backup, scheduling, recovery and SQL prerequisite repair, including the eight fixes just merged. This is a focused continuation, not certification of the entire application. No application, migration or school-data changes were made for this review.

The existing Full Readiness run [#86](https://github.com/farhan2166-creator/FMSC-Source/actions/runs/43061999541) passed security, Windows regressions, SQL lifecycle, disposable installation and packaging for this revision. That evidence does not cover every scenario below. New verification here used source tracing, the root DACPAC definitions, and a small disposable .NET harness; no live cloud accounts, Windows UI or school database were exercised.

## Confirmed findings

### A01 — Creating a backdated certificate can select a different saved certificate (P2)

**Trigger:** a person already has a certificate dated 11 November. Create another certificate dated 1 November. The write succeeds, but the form reloads date-descending history and calls `SelectSavedCertificate(0)`, which selects the first row. It selects the 11 November copy instead of the copy just saved. Printing or Mark Collected now targets the selected earlier record, unless the operator notices and changes selection.

Confirmed create paths include Sports, No Objection, Provisional and Regular Teacher / Lecturer. The teacher path also ignores the ID returned when SQL recognizes an identical earlier certificate and returns it rather than inserting another.

Evidence:

- [CertificateRecordOrder.cs](../../Core/CertificateRecordOrder.cs): `NewestFirst` sorts `CertificateDate DESC, key DESC`; `Select` treats an ID of zero as the first displayed row.
- [SportsCertificatePage.cs](../../Pages/Students/Certificates/SportsCertificatePage.cs), [NoObjectionCertificatePage.cs](../../Pages/Students/Certificates/NoObjectionCertificatePage.cs), [ProvisionalCertificatePage.cs](../../Pages/Students/Certificates/ProvisionalCertificatePage.cs) and [RegularLecturerCertificatePage.cs](../../Pages/Staff/Certificates/RegularLecturerCertificatePage.cs): create handlers discard the saved identity and select zero after reloading.
- Root `fmsc.dacpac`: the corresponding four Create procedures already return the saved certificate ID. The lecturer procedure also returns the existing ID for matching details.

Verification: ran the exact `NewestFirst` method extracted into a disposable .NET harness with existing ID 1 dated 11 November and newly saved ID 2 dated 1 November. The first row was ID 1. The selection fallback is directly established by the production `Select` method; native UI execution was not performed.

Recommended correction: capture the returned identity, reload, and select that exact ID. If refresh cannot find it, retain a confirmed-save warning and require reload rather than selecting another certificate. Preserve the current date-based history order. C# changes should be sufficient; the inspected SQL already returns the identities.

### A02 — Staff certificate collection is still omitted from the issue register (P2)

**Trigger:** mark a teacher/lecturer or bank-account certificate collected, then run Certificate Issue Register. The register reports `Not tracked`, suppresses the collected timestamp/name, and omits these copies from collection filters and awaiting-collection totals.

This corrects an assumption in the preceding fix: staff certificates **do** have a collection workflow. Fixing only Sports/NOC/Provisional was incomplete.

Evidence:

- [CertificateIssueRegisterData.cs](../../Pages/Reports/Certificates/CertificateIssueRegisterData.cs): `rowCopy = !tracked && !staff` excludes both staff sources; collection becomes a literal `Not tracked`, with NULL timestamp and collector.
- [RegularLecturerCertificatePage.cs](../../Pages/Staff/Certificates/RegularLecturerCertificatePage.cs) and [BankAccountOpeningCertificatePage.cs](../../Pages/Staff/Certificates/BankAccountOpeningCertificatePage.cs): both instantiate `CertificateCollectionFlow` and expose Mark Collected / Issue Again.
- Root `fmsc.dacpac`: both tables contain `CollectedAt`, `CollectedBy`, `CollectedByUserId` and `PreviousCertificateId`. Both Get-records procedures return collection fields. `CollectCertificateCopy` handles `Lecturer` and `Bank` explicitly.
- [Program.NewReportSql.cs](../../Tests/ReportReadChecks/Program.NewReportSql.cs): its synthetic staff tables omit those real collection columns, allowing the omission to pass regression tests.

Verification: compiled the actual register query builder in the disposable harness. Both staff UNION branches generated literal `Not tracked` and NULL collector expressions. Cross-checked those branches against DACPAC columns, procedures and form actions.

Recommended correction: include these two row-based copy sources in collection reporting and expand fixtures to represent their actual schema. Existing permissions and collection records should remain intact. No SQL migration is needed for the existing columns.

### A03 — A stalled cloud download has no response-body timeout (P2)

**Trigger:** a cloud service returns successful response headers, then the connection stops delivering the backup body without closing. The restore page can wait indefinitely during download. The application has not started restoring SQL at this point.

Evidence:

- [CloudBackupTransport.cs](../../Services/Backup/CloudBackupTransport.cs): `DownloadAsync` uses `ResponseHeadersRead`, then `CopyToAsync(output, token)`. `HttpClient.Timeout` covers completion of the headers operation, not later streaming of that body. No separate transfer/inactivity deadline is applied.
- [BackupRestorePage.cs](../../Pages/Administration/BackupRestorePage.cs): `RestoreCloudNowAsync` calls `RestoreFromCloudAsync` without a cancellation token; the page stays busy while awaiting it.

Verification: a local disposable TCP server returned HTTP 200 and Content-Length, then withheld all body bytes. With `HttpClient.Timeout` set to 150 ms, the read was still waiting after 700 ms and stopped only because the harness supplied a separate cancellation deadline. This reproduced the .NET transport behavior used here, not an actual provider outage.

Recommended correction: add a bounded inactivity timeout to streaming download and/or operator cancellation during download. Preserve the existing rule that cancellation cannot abandon SQL restore or rollback after SQL work begins. Confirm partial-package cleanup on timeout. No SQL change is needed.

### A04 — Scheduled cloud upload failure is reported as a successful task (P2)

**Trigger:** the local SQL backup and encryption succeed, but every enabled cloud provider rejects the upload, for example because authorization expired or storage is full. `CreateAndUploadAsync` returns a local-success/cloud-pending outcome. `RunScheduledAsync` discards that outcome and returns zero, so Task Scheduler records success despite no new cloud copy.

Fresh provider failures are added only to the returned message in this path; the headless caller neither displays nor logs that message. Later retry failures are logged, but the scheduled result remains zero if the surrounding operation returns normally. The local backup and queued package are preserved: this finding concerns failure visibility, not loss of the queued copy.

Evidence: [CloudBackupCoordinator.cs](../../Services/Backup/CloudBackupCoordinator.cs), provider catches and `BackupOutcome` construction in `CreateAndUploadAsync`, followed by the unconditional zero result in `RunScheduledAsync`.

Verification: traced the normal return path for failed provider uploads and the separate exception-only nonzero path. No provider requests were made.

Recommended correction: expose a sanitized durable pending/error status and a distinct scheduled result for incomplete required uploads. Distinguish this from a healthy no-backup-due run. Keep the successful-local timestamp and retry queue so cloud failures do not repeatedly recreate SQL backups. No SQL change is needed.

### A05 — Failed cloud listings are presented as missing or complete recovery inventories (P2)

**Trigger:** an enabled/connected provider rejects its listing request. `BrowseCloudAsync` logs and suppresses the error. If no provider returns rows, the page says “No cloud backups are available.” If another provider succeeds, the picker silently shows only its recovery points. The operator cannot distinguish an empty account from an unavailable account or see that the inventory may be incomplete.

Evidence:

- [CloudBackupCoordinator.cs](../../Services/Backup/CloudBackupCoordinator.cs): `BrowseCloudAsync` returns only successful provider rows and drops caught provider errors from its result.
- [BackupRestorePage.cs](../../Pages/Administration/BackupRestorePage.cs): `ChooseCloudRestoreAsync` treats zero rows as no available backups; nonempty results carry no partial-list warning.

Verification: source-traced both the all-providers-failed and one-provider-failed branches. No live provider request or native dialog test was performed.

Recommended correction: return provider warnings alongside available recovery points. Allow selecting copies from a working provider, but identify providers that could not be checked. When all listings fail, show a connection/authorization failure rather than an empty-account conclusion. No SQL change is needed.

## Verification boundaries and proposed order

Address A01 and A02 first because they affect the record selected for an action and the accuracy of operational reporting. Then address A03–A05 together as cloud-recovery reliability and status fixes. None requires changing existing financial records or weakening restore identity checks.

Before accepting corrections, use focused Windows tests for exact saved-ID selection after backdated creation and staff collection reporting; fake-provider cases for all/partial cloud failures; and a stalled streaming response with partial-file cleanup. Existing readiness workflows remain useful as the broader regression gate after those targeted cases are covered.

Already reviewed safeguards remain intentional: ordinary restore rejects a foreign school identity, Issue Again preserves collected copies, and creating/posting fees must not silently consume another child's advance. This audit does not recommend relaxing them.

## Corrections implemented — 12 November 2026

A01–A05 are implemented for application 1.0.31 with schema 17 unchanged.
Certificate saves capture the returned identity and refuse another history row
if the saved copy cannot be refreshed. Staff row-copy collection is included in
the register and SQL fixtures. Cloud downloads enforce an inactivity deadline,
preserve explicit cancellation and clean partial files. Scheduled pending
uploads return 4 and retain visible delivery status. Recovery listings carry
provider warnings alongside usable copies.

Focused portable cloud checks and 792 report checks passed locally. Application
and Windows control-test sources compile on Linux with native Windows launcher
compilation omitted only in an external temporary MSBuild file; this is not a
Windows executable acceptance. The existing release-branch Full Readiness
workflow performs the actual Windows controls, SQL report fixtures, disposable
installation and packaging verification for this change. No school data or
live provider account was used during implementation.
