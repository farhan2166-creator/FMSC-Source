# Changelog

## Unreleased — One interactive app per computer

- Refuse a second interactive FMSC process across Windows sessions, bring the existing window or its active dialog forward when possible, and release the guard on exit or crash. Scheduled backups and headless installation verification keep their existing behavior.

## 1.0.32 — Communication Centre audit corrections (schema 18)

- Match family fee previews to live scoped messages, recheck school membership before preparing fields, and preserve historical bulk audiences.
- Guard pending reads and confirmations against conflicting navigation, duplicate clicks and late intent changes; show complete per-recipient message previews.
- Add optional Message Student entry points to Admission and Fee Collection through the existing centre, keeping admission/payment drafts intact.
- Separate compose, history and settings access; retain independent student, staff and financial permissions.
- Align mobile validation across Admission and SQL; count final live exclusions honestly without claiming delivery.
- Validate prepared fields with actionable guidance, refresh edited templates, and preserve saved email-template channels.
- Block operator-sent reporting after failed chat launch and bound inactive API response-body reads without retrying uncertain sends.
- Add behavioral Windows and disposable SQL coverage. Automated checks do not replace real-account manual WhatsApp or exact-commit publication acceptance.

## 1.0.31 — Certificate and cloud recovery corrections (schema 17)

- Select the exact saved certificate after backdated or deduplicated creation; refuse to substitute another history row if refresh fails.
- Include teacher/lecturer and bank certificate collection details and totals in Certificate Issue Register.
- Time out stalled cloud download bodies and remove partial packages before SQL work.
- Record pending cloud delivery visibly and return a nonzero scheduled result while uploads remain incomplete.
- Distinguish failed cloud account listings from empty accounts, retaining available recovery points with warnings.

## 1.0.30 — Release review corrections (schema 17)

- Correct 12-hour backup scheduling for morning start times; reconcile Google Drive retries by a durably reserved file ID and verified size/checksum.
- Exclude concurrent handoff windows for the same manual WhatsApp batch on the supported LocalDB host; retain human confirmation and crash-safe resume.
- Remove only verified FMSC cloud-backup scheduled tasks during uninstall; preserve school files and unrelated tasks.
- Preserve leaving-certificate print authority for principal signatures (migration 17); retain separate ordinary-certificate print authorization.
- Report actual Sports/NOC/Provisional collection events. Staff collection reporting was completed in 1.0.31.
- Reject changing recorded Admission consent to “Not recorded”; explicit Yes/No remains authoritative.
- Repair or install the VC++ prerequisite before in-app LocalDB repair; stop for restart or an unverified runtime.


## Update dialog pending approval / layout — 2026-11-11

- Keep a newly generated computer public key visible and copyable until the owner's
  read-only GitHub deploy-key approval is verified; generating a local key no
  longer enables Check for Updates or collapses the enrollment panel.
- Add explicit **Verify access**, recheck saved keys when opening the dialog,
  and guard update checking against unverified authorization even when invoked
  outside normal button interaction.
- Synchronize responsive viewport limits on compact/expanded transitions to
  avoid stale scrollbars and inaccessible buttons. Extend Windows UI smoke checks.

## 1.0.1 — Generic audit retirement

- Apply migration 2 to remove the legacy generic audit tables and routines.
- Fee Change History shows stored fee-rate versions and posted charges. Legacy
  audit events remain available in the pre-migration database backup.

## Baseline 1 consolidation — 2026-11-05

- Retire pre-release reset and historical migration replay tools; retain source Git history.
- Publish the complete data-free test schema directly at version 1, with guarded initialization and SHA-256 provenance. Future migrations start at 2.
- Consolidate the unchanged runtime compatibility requirements and preserve backup, migration and recovery safeguards.
- Rebase current SQL regression runners and retain certificate, report, academic-year and setup-readiness behavior checks.
- Validation pending: 101 billing SQL checks passed; the certificate fixture was corrected after a missing school-profile seed, but has not been rerun. Desktop build was cancelled. Remaining suites deferred at the owner's request.

## Compatible placement choices — 2026-11-05

- Automatically choose the nearest later academic year for Annual Promotion and
  keep its date in that year. Filter source campus/classes/sections by actual
  active enrollments in the selected year, including after a completed batch.
- Scope each row and bulk Assign to compatible campus assignments; retain the
  immediate-next-class rule, configured empty destination sections, same-year
  transfers/corrections and historical repeat/correction options.
- Filter individual transfer/readmission classes by campus assignments; preserve
  same-year higher/lower class transfers and existing save/recovery safeguards.

## Compact setup editors — 2026-11-05

- Fit campus, class and section catalog entry cards to their actual field/button
  heights. Remove unnecessary vertical editor scrollbars and collapse blank campus
  validation rows; display the existing messages when input needs correction.
- Keep genuine overflow scrolling available on smaller windows and preserve the
  existing layout of other master-data pages.

## Main navigation groups — 2026-11-05

- Add Fees and Students below Daily Work. Keep Daily Work to six frequently used
  links; move billing/posting to Fees, fee setup out of School Setup, and placement
  management into Students. Shared shortcuts retain existing pages and permissions.
- Keep page-search destinations unique when a page appears in both menus, including
  after theme/sidebar rebuilds. Add Alt+3 for Fees and Alt+4 for Students.

## Campus setup consolidation — 2026-11-05

- Add Manage Campuses to Campus - Classes / Sections, using the existing campus
  editor in the same stacked form/list layout as the class catalog. Remove the
  separate campus menu entry and route campus readiness issues to the shared page.
- Refresh campus choices without losing the selected campus's assignment draft;
  discard its draft when that campus is removed. Existing contact validation,
  ordering, permissions and deletion safeguards remain in use.

## Excel report export — 2026-11-02

- Add Excel (.xlsx) to the existing report Export dialog, with the selected
  columns, full report result and current ordering. Freeze headings and enable
  spreadsheet filters; preserve numeric amounts, dates and textual references.
- Grouped collection workbooks use data rows without inserted group totals.
  Workbook generation uses ClosedXML and the existing atomic export publisher;
  Microsoft Excel, SQL changes and additional screen controls are not required.

## Monochrome direct PDF reports — 2026-11-02

- Center report titles, filter context, summary figures and table headings.
  Center amounts, counts, dates and references while keeping names/descriptions
  left aligned. Use black text and rules on white paper without colored fills.
- Keep receipt groups, subtotal calculations, column selection and wide-report
  bands intact. This shared report-export styling does not change the finalized
  bill/receipt document templates or stored financial values.

## Action shortcut coverage — 2026-11-02

- Add shortcuts for bill preview, report columns, saved-view renaming, filter
  reset, save-outcome checks, sibling admission and secondary class/section A4
  reports. Preserve distinct keys for adding fees, clearing filters and checking
  admission versus registration saves. Help, tooltips and hints share the bindings.

## Report ordering — 2026-11-02

- Add view-specific Order by choices to Collections & Receipts, Outstanding &
  Aging, Billing & Discounts, Credit Balances, Monthly Fee Reconciliation and
  Certificate Issue Register. Apply with Run Report and retain the order in Saved Views.
- Sort the full result before pagination so print and export agree. Keep receipt
  groups together and totals last; compare amounts numerically, dates chronologically
  and admission/reference numbers naturally. Default preserves the original order.

## Campus details on bills and receipts — 2026-11-02

- Omit repeated student campus lines when the common campus already appears in
  the document heading. Mixed-campus family documents retain each child's campus.
  This is a print-only change; saved bills and receipt snapshots are preserved.

## Outstanding bill ordering — 2026-11-02

- Add Order by choices for admission number, name, class/section and outstanding
  balance in either direction. Default individual ordering follows class/section;
  default family ordering keeps siblings together by their lowest admission number.
  Ordering preserves checkboxes and the preview follows the displayed bill order.

## Bulk outstanding bill preparation — 2026-11-02

- Read selected students and posted charges in two batches for the initial review,
  instead of repeating those reads per student. Carry-forward labels are classified
  together. Each bill still rechecks membership, readiness and dues before issuance;
  numbering, partial-failure recovery and finalized print layouts are unchanged.

## PDF and financial document presentation — 2026-11-02

- Management PDF exports now share Print Preview's school/scope headings,
  authoritative summary cards, monetary alignment, total emphasis and page footer.
- Wide reports retain selected columns in labelled bands; CSV stays data-only.
- Separate receipt discounts, money received and credit used; make settlement
  and family balance labels explicit without changing saved financial values.
- Correct the annual receipt selector to describe its three-column A4 layout.

## Report purpose improvements — 2026-11-02

- Split unpaid balances into overdue, due today and future amounts; family debt
  includes standalone students. Migration 70 changes the report reader only.
- Concession Summary uses an explicit as-of date and the latest effective term
  per enrolled fee instead of summing historical agreements.
- Clarify current-roster attendance printing and run report data/RDLC suites in CI.

## Class and section setup consolidation — 2026-11-02

- Manage Classes and Manage Sections now open from Campus - Classes / Sections;
  the separate Classes & Sections navigation page is retired.
- Full catalog editing, ordering, programs and safe deletion remain available.
  Catalog refresh preserves pending campus ticks by ID without saving them.
- No SQL schema or financial behavior changes.

Notable user-facing, database, installer, and operational changes will be tracked here. This file begins as a **forward-looking** changelog; it is not a fabricated complete history of previous builds.

## [Unreleased]

### Admission workflow

- Separate current-student activation, draft placement completion and historical records in Admission. Group transfers and move Left-to-Hold correction under Advanced; retain the existing return, permission and financial review rules.
- Put the existing placement confirmation inside the draft editor, with loaded-record and unsaved-change guards. No database migration is required.

### Report improvements

- Restore Fee Collection's Print Family Bill as one combined bill for the loaded student and checked siblings. Print Student Bill continues to print only the loaded student; remove the redundant grouping selector from Fee Collection.

- Correct thermal bill preview scaling using the PDF/print paper dimensions, center single bills and distinguish the preview background from the paper. PDF and printer sizing remain unchanged.
- Combine bulk outstanding-bill filters, all actions and the student grid in one compact panel, with narrower filters across two rows on normal windows. Remove individual admission/name search and the redundant Select Shown action; retain bulk placement/balance filters, paper/grouping options and row exclusions.
- Fit each thermal bill to its own rendered text plus a small margin in preview, PDF export and printing. Keep long family totals on the same slip and preserve independent page heights within mixed-length batches.
- Add an inline Bill grouping selector (Individual by default, or By family) to collection, outstanding printing and accounts. Open a single preview directly, with each family or standalone bill on its own page. Keep full-balance scope and readiness warnings, saved bill recovery and child-only copies of saved family receipts. Registration/admission print only the current child. Paper options are unchanged; no database migration is required.
- Extend per-user named report views to Collections, Outstanding, Billing & Discounts, Credit Balances, Monthly Reconciliation, Certificate Issue Register and Class Sheets. Save/apply/rename/delete filters and applicable column choices; add rolling date periods and preserve existing Student Reports views.
- Add Monthly Fee Reconciliation by billing month and school/campus/class/section/fee type, comparing current signed money allocations, credit applied and unpaid fees; include excess settlement and missing-period charge-date fallback.
- Add the consolidated Certificate Issue Register with permitted student/staff sources, date/type/recipient/entry/collection/search filters, recorded issue history and explicitly distinguished saved-only records.
- Add Account Credit filtering and data-only CSV exports for Collections & Receipts; count receipt entries separately from headings/totals and repeat printed group context with numeric subtotals.
- Add overdue/balance/day-range filters and permission-checked Open Statement to Outstanding & Aging; keep filtered output distinct from the full-location A4 report.
- Add a read-only Student Credit Balance Register with student/family views and current/latest placement filters; preserve signed ledger sums and avoid duplicate balances.
- Add a Concession Summary separating assigned discount rates by billing frequency, with no mixed-cycle money grand total.
- Capture finance report generation/aging dates separately from print time. Preserve financial writers, saved documents, database versions and migrations.


- Lock the academic year in Admission's placement/setup actions. New placements use the configured current year; existing placement history retains its recorded year.

- Complete the optional quick counter journey with the existing Fee Collection screen opened over the retained family. Carry only selected children into payment, retain editable suggested allocations, print established receipts and refresh remaining dues without retaining submitted amounts.
- Isolate registration recovery from loaded child editors, reject mismatched recovered identities and identify progress when preparation stops after earlier children have saved. New children remain pending for full admission later.
- Add migration 27 for read-only current/future fully waived bill evidence. Preserve established admission, financial writes, receipt numbering and RDLC templates; no school or financial records are rewritten by the migration.


### Fixed

- Let Delete clear selected Fee Matrix amounts and persist cleared class-rate templates alongside other edits in one atomic save; distinguish blank from zero and reject stale clear requests (migration 42).
- Align certificate inputs directly beneath their labels, let certificate grids size date/dropdown fields consistently, and use compact wide-screen layouts for student certificate details. Match disabled/read-only editors to their surrounding input background.
- Separate student account statements from the family/group statement workflow; expose annual receipts and outstanding bills as related reports, and leave balances unshown until a statement loads.
- Let the school issue student character certificates based on current or earlier Class 10/12 placement, without a passed-exam checkbox or an extra print-wording acknowledgement.
- Fit Record Departure fields and explanations within the dialog, show the waiver month only when selected, and show the school cutoff's actual waiver month in the review.
- Verify a backup's recovery identity and compatibility in a temporary database before replacing live school records; retain backup-file protection and verify identity again after restore or safety recovery.
- Remove main-database version checks from the communication worker and webhook callback; retain communication API, structural, permission and delivery safeguards (migration 41).
- Make the database-file release helper stop a verified lingering LocalDB engine gracefully and require repeated process-exit and exclusive MDF/LDF checks, even when the controller incorrectly reports Stopped.
- Validate each issued bill line against its saved charge balance and student, rejecting offsetting stale amounts even when the grand total matches (migration 40). Existing saved bills remain available for reprint.
- Retain additional-fee posting intent across interrupted saves and reopening; both individual and bulk entry require a read-only outcome check before another posting can proceed.
- Restrict bulk additional-fee posting to eligible As Needed fees, excluding admission, tuition and transport in both the picker and database writer (migration 39).
- Allow the family payment counter to open for students on hold and collect retained posted dues while new fee posting remains paused.
- Return Fee Collection focus to Amount Received after closing its student fee, advance, history, family or print dialogs.
- Allocate received cash after each bill's discount and selected student advance, so mixed advance/cash collections do not assign cash to already covered fees.
- Show staff employment validation messages below their inputs instead of squeezing them beside field captions, and reject a Hire Date later than today.
- Define payment-detail grid columns before binding so Student Fee Corrections can open View Details without accessing missing auto-generated columns.

### Changed

- Show saved pre-payment fee amounts on collection receipts, followed by Total, Received and Balance; retain credit/adjustment details and remove the duplicate settlement total for saved collection receipts.
- Open staff Add Job and Edit Job directly in an editor-only dialog; keep employment history on the staff page.
- Remove the redundant top Employment button from Staff Management; use the existing Edit Job, Add Job and employment-history Edit actions.
- Prefill the bank account opening certificate’s editable Designation from the selected staff member’s employment record; preserve saved certificate wording when editing.
- Remove the trailing “here.” after Subject Taught on lecturer/teacher certificates.
- Prefill new lecturer/teacher certificates with the selected staff member’s employment designation and latest saved certificate subject, keeping both fields editable and existing certificate values intact.
- Move bill/receipt dates below the closing footer in small italic text: bills show the current printed-on date/time; receipts retain their recorded payment date/time.
- Add Whole School, Campus, Class and Section grouping to Collections & Receipts receipt lists, with monetary subtotals and a grand total on screen, print and export; preserve mixed-location family receipts intact.
- Give fee descriptions more width on student/family bills and receipts by narrowing the amount column, preserving paper width and month alignment.
- Clarify Billing Summary headings with the selected charge-date/grouping label, Charge Count, Gross Billed, Discounts and Net Billed.
- Show exact Days Overdue beside the stored due date in Outstanding & Aging student, charge and family views, while retaining aging buckets.
- Center serial numbers, sections and student counts in the Class & Section Summary, including headers and totals; preserve left-aligned names and right-aligned balances.
- Widen Update Student Fee Rates, align its fields in one row when space permits, and fit preview columns to the available width. Smaller windows retain a two-row field layout.

## [1.1.0-rc.2] - 2026-10-18

- Make Billing Center titles and guidance concise and consistent; use posting/reversal labels instead of generic report instructions.
- Show additional-fee periods as month/year in Fee Collection and display charge remarks in their own column.
- Show zero-value numeric inputs as focus-aware placeholders. Use optional Remarks for additional fees, student setup and ordinary fee-configuration notes; retain contextual reasons for corrections and reversals.
- Add Manual / Advance Posting with a reviewed month range and optional student filter for vacation fees; post the range in one transaction using existing monthly eligibility and duplicate checks.
- Show admission number, student, father and recorded class/section in Billing Status with natural admission-number sorting; leave missing placements blank.
- Move Fee Rate Setup to School Setup and student fee corrections, daily collection closing, student promotion and student data review to Daily Work; remove the Fees & Billing navigation group.
- Fill the campus/class/section picker from the highlighted list row during keyboard navigation; Enter accepts the choice and closes the list.
- Let the operator choose concession periods, issued-bill treatment and credit/refund, with a financial preview and save-outcome recovery.
- Offer admission-month or following-month concessions in direct admission and Quick Registration.
- Let leaving retain issued monthly bills, apply the school cutoff or waive from a chosen month; preview full correction chains.
- Upgrade schema 20 to 21 without rewriting school records; remove audit-only preparation from revised procedures.
- Review fee rates and concessions together for monthly, annual and individual purchase bills, including closed placements. Retain credit unless its use is explicitly selected; show credit before/use/remaining in the review.
- Make carrying monthly concessions through promotion an explicit choice; remove the unused amount editor while retaining its SQL compatibility API.
- Document the operator choices and remaining school-PC acceptance in Docs/OPERATOR_CONTROL.md.

## [1.1.0-rc.1] - 2026-10-18

### Changed

- Group direct admission, pending registration and existing-student activation in Student Admissions. Billing Center exceptions now open the selected student's setup or collection account; remove duplicate navigation paths.
- Align application, installer and optional communication runtimes on schema 20. The transactional 19→20 migration preserves school and financial rows and retires four unused read procedures.

### Fixed

- Stop offering credit deposits/applications as ordinary payment corrections, and exclude amounts already released to credit from the adjustable balance.
- Label Quick Registration fees as a plan, separating monthly and yearly/one-time amounts from actual posted dues.
- Support release-candidate installer versions with downgrade protection and validate schema package checksums. Remove the revised callback’s audit insert while preserving inbox/delivery recovery records.
- Restore deleted verification sources/tools, correct outdated test/schema assumptions, repair Help Center source links, and verify release branches through security, Windows, SQL, installation and database-free packaging checks.
- Remove a date-prefixed maintenance patch from release migration discovery; its guard is already in the baseline.

### Added

- Add outstanding student/family bills and a Family / Returns shortcut to Fee Collection. Family cards show placement/year and billing readiness; each return uses the existing guarded confirmation dialog. Quick Registration warns when linked returning siblings still lack new-year charges.
- Level 3 cloud reliability checks for guarded SQL faults, backup/restore, concurrency and measured fictional workloads, plus Windows component/update acceptance and a complete 45-scenario evidence/local-requirement matrix.


### Documentation

- Organize loose workflow, design and developer documents under Docs; group audit reviews and historical records under Audit, with navigation indexes and updated relative links.
- Shorten C# comments and record accepted business policies beside their implementations. Historical audits, executable code, SQL, database artifacts and installer behavior are unchanged.

### Changed

- Keep direct desktop, SQL, Level 2 and disposable installer workflow triggers manual. Release branches run the coordinated security, desktop, SQL, installation and packaging checks; security also runs on main pushes and pull requests.

- Temporarily hide Admission's Save & New button to leave more room for Print. Use Save followed by Clear / New for the next student.
- Distinguish New Record from Clear Search in shared setup editors. Setup Readiness disables unavailable destinations and navigation during checks, with inline permission guidance.
- Clarified payment-adjustment and placement confirmations with aligned details, and shortened Communication Center guidance while preserving warnings and confirmation defaults.

- Replace the XML-heavy `DatabaseSchema/` reference export with real DacFx-generated SQL `CREATE` scripts in `Schema/`. The generated review PR removes the old files only after replacement scripts are produced and reports inline/unsupported model objects in a JSON manifest; no school data or database changes.

- Pause routine Dependabot NuGet and GitHub Actions version-update PRs until the owner chooses to resume. Existing PRs, security-alert settings, security updates and secret scanning remain unchanged.

- [PR #19](https://github.com/farhan2166-creator/FMSC-Database/pull/19) makes Student Account Statement start with one student and full-width account details; group/family selection remains a secondary mode. Annual Receipts opens for the selected student. Family / Siblings opens from Student Profile in an owned window and refreshes the profile on return. Financial calculations, SQL, permissions and installer behavior are unchanged.

- [PR #15](https://github.com/farhan2166-creator/FMSC-Database/pull/15) renames Bulk Promotion to **Student Promotion** and moves Promotion History into a window opened from the promotion-details header. History starts with the selected source filters, remains independently filterable and leaves the promotion draft intact. Existing permissions, promotion processing and SQL contracts are unchanged.

### Added

- Enhance the existing offline Help & FAQ Center: 126 source-linked questions across 15 topics, plain-language rule labels, ranked synonym search, topic/rule filters, related questions and responsive themed reading. Clarify calendar vs enrollment protection, supported role editing, payment recovery and retired communication setup guidance; application business rules remain unchanged.

- Added an opt-in disposable Windows installation acceptance workflow: approved data-free schema fixture, real signed LocalDB prerequisite, validated complete installer, synthetic MDF/LDF, and installed-app verification. It reports pristine-MSI gaps and uploads sanitized evidence only; it does not test real school data or approve production deployment.

- Added a manually dispatchable, DACPAC-triggered, schema-only reference extractor. It stores complete per-object model XML and SQL module bodies, tracks changed definitions and proposes a draft synchronization PR without importing data or approving changes.

- Optional Communication Center with explicit guardian/staff consent, reviewed English/Urdu templates, draft/approval/scheduling controls, a durable SQL queue, safe simulation, Twilio SMS and official Meta template adapters. Live mode defaults disabled; the additive database module requires separate owner review/deployment. No Accountant grants, financial procedures, installer upgrades or approved DACPAC changes are included. See [setup and remaining external acceptance](Docs/COMMUNICATION_CENTER.md).

- [PR #18](https://github.com/farhan2166-creator/FMSC-Database/pull/18) adds conservative DACPAC refresh comparison and trusted default-branch synchronization PR automation. Exact full-model/module/data matching, sanitized refusal reports and synthetic safety regressions preserve existing approval/security/SQL-isolation rules; no automatic merge or material-change override.
- Generated [PR #17](https://github.com/farhan2166-creator/FMSC-Database/pull/17) aligns the verified metadata-only `91ec136` refresh across three approval/provenance records, preserving the refreshed DACPAC and reusing the schema-only fixture.

### Testing

- Added an isolated Level 1 SQL lifecycle harness in [PR #14](https://github.com/farhan2166-creator/FMSC-Database/pull/14), with schema-only approved-model provenance, fictional school fixtures, persisted admission/fees/promotion/recovery assertions, existing-suite integration and sanitized CI evidence. Production application/database/installer behavior is unchanged; real Windows and deployment acceptance remain separate.

### Fixed

- Keep report-preview captions above the print toolbar and preserve the content viewport's docking order. Compact Admission finder/action sizing keeps Print beside the other actions at ordinary widths while narrow windows still wrap whole buttons.

- Bound Help Center splitter transitions during compact layout changes to prevent the reproduced Windows startup/layout exception.

- Refresh Setup Readiness navigation after the current grid cell changes, so permission guidance follows the selected destination.
- Remove obsolete installer setup instructions from the missing Communication Center module warning; direct operators to administrator-reviewed setup and recovery guidance.

- Center message-dialog buttons with a readable gap. Show clear decision headings, prominent consequences, aligned fields and read-only record tables for payment, promotion, backup and duplicate-student confirmations. Preserve confirmation defaults, keyboard choices, exact values and recovery payloads; long details scroll while buttons remain visible.

- Shorten promotion, admission, registration, transfer and collection feedback. Shared status areas stay within two readable lines, with full hover/accessibility text; unknown promotion outcomes remain visible after refresh. Counts, receipt references, recovery guidance and database rules are preserved.

- Communication Center distinguishes inactive-tab keyboard focus from selection and adds per-tab instructions, actionable empty/loading/failure states, bounded paging and explicit save/next-step feedback. Changed filters block stale selections, template paging preserves the chosen template, New Message clears the previous schedule, and dispatcher errors remain visible. SQL, permissions, provider transports and installer behavior are unchanged.

- Offer explicit Communication Center setup for a compatible schema-6 database before application activation. Approval creates a verified backup, checks packaged `7.sql` against the embedded definition, applies it transactionally and verifies base/module contracts before commit. Existing valid modules are preserved; partial or incompatible modules are refused. Normal startup, messaging defaults, base version and reviewed DACPAC remain unchanged.

- Package Communication Center SQL directly as `Migrations/7.sql`, include its deployment guide, and verify that the published SQL and LocalDB recovery helper match the checked-out source. The approved base schema remains version 6; the optional add-on still requires explicit reviewed deployment. Regenerate installer ZIPs after merging fixes; rerunning an older run retains its older source revision.

- Improve LocalDB installation on clean Windows computers: MSI verbose logging, explicit wizard fallback after silent failure, restart-required exit handling, executable verification and PATH discovery. No school data or database schema changes.

- Fixed a fresh-install failure: the installer no longer requires `dbo.ReorderSchoolOfficials`, which is absent from the approved schema. A regression check now keeps the remaining compatibility probes aligned with the reviewed DACPAC; no SQL, migration or school-data changes.

- [PR #16](https://github.com/farhan2166-creator/FMSC-Database/pull/16) restores date-field height after shared width limits are applied, preventing date controls from disappearing while their captions remain visible in admissions and other field layouts. Date values, optional-date handling and validation are unchanged.

- [PR #30](https://github.com/farhan2166-creator/FMSC-Database/pull/30): Find Student clears placement/status filters when an admission number is typed, and clears the admission number when a filter changes. Changed criteria immediately remove old rows and their open/new-admission actions; delayed replies cannot restore stale results. Name searches retain combined filtering.

- Shared DPI and responsive-layout corrections are proposed in [PR #13](https://github.com/farhan2166-creator/FMSC-Database/pull/13): explicit per-monitor awareness, logical client-area density, monitor-relative fonts and grid/tab dimensions, preserved window minima, scrollable small-screen dialogs and bounded fixed-purpose field widths. Appearance and business workflows are preserved. Physical DPI/monitor and ReportViewer acceptance remains manual.

The following end-user-flow corrections are proposed in [PR #8](https://github.com/farhan2166-creator/FMSC-Database/pull/8).

- Dashboard Student Reports and profile shortcuts follow destination permissions. Read-only record review remains available; unavailable photo/family actions are clearly disabled. Administrator and Accountant grants are unchanged (UFA-09).
- Daily closing rejects fractional new actual amounts without rounding; printed summaries match the on-screen precision and preserve historical stored fractions (UFA-08).
- Daily closing summaries print with intentionally blank Refund/Correction actuals, retaining recorded net totals and leaving unchecked methods blank (UFA-07).
- Ctrl+K searches permitted destinations independently of collapsed or compact sidebar visibility and invokes navigation directly (UFA-06).
- Admission and fee collection hold submitted inputs through awaited writes and refreshes, preventing post-submit typing from being lost. Validation remains editable before submission (UFA-05).
- [PR #31](https://github.com/farhan2166-creator/FMSC-Database/pull/31): Admission finalizes a successfully reloaded record's clean baseline after its submitted inputs are enabled again. Mark Left and Correct Placement no longer mistake a successful save for unsaved edits; real edits, failed reloads and pending photos remain protected.
- New sports and provisional certificate content participates in existing leave/discard guards. Date-only untouched dialogs remain freely abandonable (UFA-04).
- Classes & Sections forwards active-save state without adding setup-draft prompts. Bulk fee-rate drafts retain their existing discard protection when embedded, with a clean baseline after a successful apply (UFA-03).
- Admission closure and legacy go-live hold review inputs, reject obsolete previews, and prevent closing during pending operations (UFA-02).
- Certificate creation keeps inputs locked and refuses repeat submission until the pending write finishes (UFA-01).

### Security
- Added weekly Dependabot proposals for application/test NuGet packages and GitHub Actions, with grouped minor/patch updates, bounded open PRs and human review.
- Added read-only Gitleaks CLI and sensitive-file checks on pull requests and pushes to `main`, with checksum-pinned downloads, temporary redacted reports and exact synthetic-fixture exceptions. No paid service or automatic merging is enabled.
- Added disposable detection/redaction regression tests and guidance for security failures and dependency review. Application, SQL, database compatibility and installer behavior are unchanged.

### Documentation
- Added a project overview and operator, installation, recovery, architecture, release-readiness, and development-workflow guides.
- Added a pull-request template prompting contributors and Codex to explain behavior changes, safety implications, tests, and manual acceptance requirements.
- Established a single current [master audit](Audit/FMSC_MASTER_AUDIT.md) with a [historical index](Audit/Archive/FMSC_HISTORICAL_AUDITS.md) for earlier investigations.

### Build and delivery
- Existing Windows desktop CI and manually triggered private application-only and complete-installer artifact workflows are documented. **Workflow existence does not mean a complete installer has passed deployment acceptance.**
- Fresh-install database MDF/LDF files remain owner-supplied after downloading the private full-installer ZIP; school data is not included in source control or Actions artifacts.

## Versioning policy

The application assembly version in `FMSC_SMS.csproj` and the database's required schema version are **different concepts**. The current runtime contract requires **database schema 6**. A version should not be tagged or described as a production-approved school release until the tests and checks in [RELEASE_CHECKLIST.md](Docs/RELEASE_CHECKLIST.md) are recorded.

When a pull request changes behavior, add an Unreleased entry describing the operator-visible result and a PR link. Move entries to a dated version section only after an actual release approval.

For the original fixes, implementation passes, schema verification and historical decisions, see the [historical audit index](Audit/Archive/FMSC_HISTORICAL_AUDITS.md) and commit history.

- Legacy student setup applies the School Profile cutoff to the selected billing start and shows the resulting monthly fee period. Historical class placement stays independent. Migration 29 corrects the earlier placement-based rule in migration 28 without rewriting existing charges.

- Correct the placement cutoff change with migration 29: use the selected billing start for the cutoff, preserve historical class placement independently, and use the billing date for opening one-time fees. Existing student charges are unchanged.

- Align individual/family bill and receipt headers: show the school name followed by the address and campus on one line, widen detail labels slightly, and reduce document-title font to avoid wrapping. Preserve available campus names in grouped documents; mixed-campus families use an explicit summary. Financial amounts and records are unchanged.

- Rename the payment Correction action to Reduce Recorded Payment. Explain that the entered amount is the amount to retain and show the resulting increase in outstanding dues in the confirmation and success message. Preserve transaction codes, calculations and recovery behavior.

- Clarify the leaving-fee review: show each charge's reversal restriction, explain that kept dues remain payable and reversal decisions save with the certificate, name the Fee Collection destination, and count or disable the view-only settled/zero-amount history checkbox. Preserve financial eligibility and invalidate a reviewed decision when its placement changes.

- Simplify financial screen wording and add practical rupee-example tooltips across collection, student advance balances, payment adjustments, fee/discount changes, posting, rate setup, transfers, readmission, leaving and reports. Explain new money versus existing advance, amounts to keep as paid, fee cancellation versus payment reversal, and excess paid money. Preserve financial calculations, permissions and transaction codes.


## 1.0.24 — Initialize first admission counter (schema 15)

- Initialize a missing `AdmissionNumberCounter` singleton when installing or upgrading an empty school, so the first direct admission can use number 1 without SQL tools.
- Preserve all existing counter rows. Only reconstruct a lost counter for populated databases from safely interpretable numeric admission identifiers; refuse ambiguous cases instead of risking duplicates.
- Replace the SQL lifecycle fixture's premature counter insertion with true first- and second-admission regression coverage, including transaction rollback and restored test fixture numbering.
