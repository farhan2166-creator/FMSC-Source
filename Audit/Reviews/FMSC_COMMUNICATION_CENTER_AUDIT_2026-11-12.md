# Communication Centre independent audit and remediation

Date: 12 November 2026. Repository: `farhan2166-creator/FMSC-Source`.
Reviewed main baseline: `5a16dcf5967af4a277189bc866dfba193049c983`.
Scope: school-record recipient discovery through explicit manual handoff, SQL
storage/migrations, permissions, templates, optional record integration, and the
existing provider extension. Findings below come from source tracing and tests,
not from treating previous audit conclusions as acceptance evidence.

## Implementation and execution paths

- `MainDashboardForm.cs` routes to `Pages/Communication/CommunicationCenterPage.cs`.
  The page builds its controls programmatically (no separate designer), binds
  audience changes, uses the existing `StudentPicker`, `StaffPicker` and lookup
  controls, and implements compose → preview → confirmation → manual handoff.
  History and Settings use their own separately checked permissions.
- `Core/CommunicationRecipientResolver.cs` queries Students, preferred active
  Enrollments, CampusClassSections and FamilyMembers. It canonicalizes Pakistan
  mobile destinations, reads channel suppressions, applies Admission consent,
  deduplicates destinations and exposes masked previews. Campus/class selection
  discovers a complete audience automatically. No individual selection loop is
  required. School/individual/family selection follows active-student rules;
  campus/class/outstanding audiences additionally require an active placement.
- `Core/AdmissionConsentStore.cs`, Admission save/load and
  `msg.RecordAdmissionConsent` use `Students.SchoolMessageConsent` plus its
  recorded metadata/`msg.ConsentAudit`. Yes is authoritative; No and null are
  excluded. There is no new guardian consent, verification or address-book flow.
  Staff recipients use active staff records and independent Staff.View access;
  they do not inherit a student's guardian consent.
- `CommunicationPreparedMessages` validates/substitutes supported fields and
  obtains current source values. Fee values come from noncancelled BillingCharges
  using `dbo.GetFeeChargeBalance`, not gross bill amounts. Shared fee destinations
  require real FamilyMembers links; only consented in-scope children contribute.
- `CommunicationBatchStore.ConfirmPreviewAsync` passes exactly the reviewed
  eligible destinations in `msg.RecipientSelection`. SQL recomputes the complete
  eligible set, compares it to that TVP and serializes a stable request GUID.
  Confirmation records intent; it does not send. Cancelling confirmation makes
  no batch and launches no external application.
- `CommunicationManualHandoffStore` calls `msg.OpenManualHandoff` to recheck live
  scope, consent, phone, opt-out, permissions and channel state, then refreshes
  prepared fields. `CommunicationManualHandoffDialog` displays the resulting
  message, opens an encoded official `wa.me` URL, and separately asks for an
  operator report. `CommunicationHandoffLease` excludes concurrent windows for
  one batch on the supported per-user LocalDB host. Interrupted work resumes
  from History; opening, reporting and provider delivery remain different facts.
- `CommunicationManagementStore`, `CommunicationTemplateDialog` and the Settings
  card read/edit templates and enable/pause manual WhatsApp. The existing
  `CommunicationProviderDialog` is not offered as a live delivery option.
  `CommunicationProviderCredentialStore` uses Windows DPAPI. The transport
  registry, `CommunicationDispatchStore` and `CommunicationTwilioTransport`
  retain claim/result/reconciliation/callback/retry boundaries. Unknown results
  cannot be blindly resent. Meta/SMTP profiles are disabled placeholders;
  SMS/email sending is not represented as operational by this UI.
- Admission's optional Message Student action and Fee Collection's Student Fees
  menu now open the same centre through `CommunicationRecordEntry`. They target
  the saved main student, leave underlying drafts/transactions intact and never
  save consent or send automatically. Collection sibling-payment ticks do not
  expand the selected-student communication scope. The centre's Family audience
  deliberately uses the same saved sibling/family associations as school records.

### SQL inventory and historical paths

Actual current communication objects are installed by the numbered migrations,
not by the root `fmsc.dacpac`, which predates the current `msg` system/consent
fields. The DACPAC was useful only for base table contracts. Migrations 5–7
contain earlier admission/consent changes; migration 8 retires the old `comm`
module under its existing explicit historical decision. This change does not
expand that deletion. Migration 9 creates the current system; 10 adds manual
handoff and disabled provider extensions; 11–17 preserve it; 18 corrects the
verified issues below. Fresh disposable baselines and nonempty legacy upgrade,
refusal and rollback paths are exercised by the existing lifecycle suite.

Current objects examined:

- Tables: `msg.ConsentAudit`, `ChannelSettings`, `Suppression`, `EmailSuppression`,
  `Template`, `Batch`, `Recipient`, `Attempt`, `Receipt`, `ManualBatch`, `ManualHandoff`.
- Type/function: `msg.RecipientSelection`, `msg.NormalizeMobile`.
- Consent/confirmation: `RecordAdmissionConsent`, `ConfirmBatch`, `ConfirmManualBatch`.
- Delivery boundary: `ClaimRecipient`, `RecordProviderResult`, `ApplyVerifiedReceipt`,
  `ApproveDefinitiveRetry`, `ReadAttemptForReconciliation`, `ReadDispatchProvider`.
- Manual path: `EnableManualWhatsApp`, `ReadManualWhatsAppMode`, `ReadManualHandoff`,
  `OpenManualHandoff`, `MarkManualHandoff`, `TR_Attempt_RejectManualProvider`.
- Management: `ReadHistory`, `ReadChannelSettings`, `ConfigureChannel`, `PauseChannel`,
  `ReadComposeTemplates`, `ReadTemplates`, `SaveTemplate`.

There is no current communication view or independent background worker to
activate. School views/tables and the existing fee-balance function are source
records, not a second messaging directory. Parameterized C# calls and SQL TVPs
are used; no user-controlled SQL concatenation was found on these paths.
FamilyMembers has a unique AdmissionId constraint, preventing duplicate join rows.

## Confirmed defects and corrections

No critical defect was demonstrated. High findings are release blockers until
corrected and verified. All findings below have implementation fixes in this PR.

| ID / severity | Verified behavior and root cause | Correction / principal files | Behavioral evidence |
|---|---|---|---|
| CC01 High | Fee preview described the first child while final handoff combined eligible siblings; a family's names could be paired with only one child's admission date. Preview and live preparation used different values. | `CommunicationRecipientResolver`, `CommunicationPreparedMessages`: share scoped family aggregation and display-name fallback; refuse a single admission date when children differ. Unrelated shared contacts fail safely. | Real SQL reader tests compare family/selected-child balances and identities, decline a sibling, and refuse mixed dates/unrelated shared numbers. |
| CC02 High | The async prepared-field read following SQL Open rechecked consent/phone but omitted confirmed campus/class/family scope. A record moved between those calls could still be rendered. | `CommunicationPreparedMessages.ReadLiveAsync`: recheck complete confirmed audience immediately before materializing current fields. | Change campus after Open; actual production reader refuses. SQL also refuses a forged cross-campus recipient TVP. |
| CC03 High | Action BusyScope did not set the page's BusyOperationRunning flag; navigation/intent could change during SQL work, then a late confirmation could launch the old batch. | `CommunicationCenterPage`: wrap actions in existing RunBusyAsync, guard Back/New and capture/check request GUID, target, account and body before handoff. | Delayed WinForms operations check navigation/close blocking, duplicate refusal, stale read disposal and no late launch. SQL concurrent request tests preserve one batch. |
| CC04 High | No optional Admission or Collection communication entry existed, leaving the required saved-record workflow unintegrated. | `CommunicationRecordEntry`, Admission and FeeCollection pages: reuse existing centre with saved-student scope. SQL migration 18 and matching C# readers support explicit single-student fee scope. | UI callbacks simulate cancellation without changing drafts; real SQL proves the selected child does not expand to siblings. Admission operations create no batch. |
| CC05 Medium | Dashboard required history permission to reach messaging; legitimate send-only/configuration-only roles could not use their granted function. Page initialization also read student lookups unnecessarily for unrelated roles. | `CommunicationAccess`, `MainDashboardForm`, centre: separate compose/history/settings entry, default staff-only sender appropriately, keep independent source/finance permission checks. | Role-specific UI checks; real SQL refuses send without source access and source access without send. |
| CC06 High | SQL normalized malformed/unprefixed imports differently from Admission/C#. The independently resolved recipient set could disagree and block an otherwise valid bulk confirmation. | Migration 18 `msg.NormalizeMobile`: match supported prefixes, balanced parentheses, plus placement and Unicode whitespace; no stored phone rewrite. | C#/SQL format boundary comparisons including null/invalid/Unicode cases and automatic campus/class discovery. |
| CC07 Medium | Last-recipient live exclusion left a batch Confirmed; history did not count Excluded as skipped, producing inconsistent remaining work. | Migration 18 `OpenManualHandoff`/`ReadHistory`: finish exhausted manual batches and count skipped/excluded without inventing sent/delivered events. | Withdraw consent after preparation; reopen is denied and history is complete with zero reported/delivered. |
| CC08 Medium | Unsupported/incomplete prepared fields could be saved; later system exception types obscured actionable guidance. Missing audience selections also appeared as generic failures. | `CommunicationValidationException`, prepared messages, template dialog/store, centre and resolver: validate save/preview/confirm/open with operator-facing guidance. | Unsupported fields/brackets and incomplete targets refused; Unicode/quotes/newlines round-trip through official handoff URL. |
| CC09 Medium | Template editor omitted Email, converting an existing Email template to SMS on save; edits did not refresh compose choices. | `CommunicationTemplateDialog`, centre: preserve stored channel codes, label unavailable delivery, reread compose templates after editing. | Editor test preserves Email; SQL edit/deactivation test verifies persistence and sender-list exclusion. |
| CC10 Medium | SQL persists Opened before shell launch for crash safety. A failed render/browser launch followed by refresh enabled I Sent It even though no chat opened in that attempt. | `CommunicationManualHandoffDialog`: track failed openings in the active window, block button and handler reporting until a successful reopen, show exact prepared body. Preserve crash-safe resume semantics. | Fake shell failure, direct handler refusal, successful reopen, canceled report and explicit unverified report tests. |
| CC11 Medium | Existing API adapter bounded requests only until response headers. A streaming body could stall send/reconciliation indefinitely. | `CommunicationTwilioTransport`: apply the bounded cancellation deadline through body reads; uncertain send remains Unknown with no retry. Channels remain disabled. | Synthetic stalled HTTP bodies validate deadline, Unknown result and one submission. No live provider requests. |

### Compatibility and data safety

Migration 18 adds `msg.Batch.SingleStudentScope` default false, rather than
reinterpreting historical `StudentId` values that the old UI retained but SQL
ignored for bulk fee audiences. Only new explicit single-student confirmations
set it true. Confirmation, claim, handoff and C# preparation all use that meaning.
Existing batches, operational/consent history and financial rows are retained.
The migration requires its predecessor and is version-idempotent; existing
backup/lock/outer-transaction/version and structural compatibility gates remain.
`DatabaseCompatibilitySchema18Requirements.json` asserts the new column contract.
The only installer edits are application/schema version values required for this
migration; installer, updater, CI and exact-commit Sandbox architecture are unchanged.

## Validation record and limitations

Executed locally against the working tree before commit:

- Guarded Linux SQL suite, SQL Server 2022 (16.0.4295.3), schema 18:
  **64 scenarios passed, 0 failed, 0 blocked; 76 expected refusals verified**.
  Audit stage: **42 assertions**; existing communication stage: **81 assertions**;
  nonempty legacy migration/refusal/rollback: **16 assertions**; financial report
  checks: **851 assertions**; portable regressions: **969 assertions**.
  Run ID: `fcf254d9-6301-4614-a1e5-cc6745379187`.
  This is precommit working-tree evidence (runner reported baseline HEAD), not
  exact-commit publication acceptance. Owned SQL container cleanup passed.
- LifecycleSqlChecks Release build: passed, zero warnings/errors.
- Application and ThemeSmokeChecks Release source compilation on Linux with
  Windows targeting: passed. Temporary local build targets omit native Windows
  launcher execution only; the repository and CI targets are unmodified. This
  does not count as executing the Windows UI tests or native publish.
- `git diff --check`: passed.

The first local attempts were blocked by sandbox IPC/mutex restrictions; one
synthetic fixture also used an invalid status literal and was corrected to the
real Left status. The final run used the unchanged guarded harness with IPC
access and passed. No protection was disabled to obtain these results.

Exact-commit GitHub validation and remaining acceptance are tracked on the PR.

Test sources:

- `Tests/LifecycleSqlChecks/CommunicationAuditSqlChecks.cs` executes actual C#
  recipient/current-field readers against real SQL; `CommunicationQueryContext`
  delegates only to the runner's existing guarded disposable target.
- Existing `CommunicationV2SqlChecks` and lifecycle/migration suites remain
  enabled, including permission, consent, opt-out, idempotency, disabled-default,
  uncertain-delivery, manual/API exclusion and legacy rollback protections.
- `Tests/ThemeSmokeChecks/Program.CommunicationAudit.cs` drives real WinForms
  event handlers with synthetic asynchronous storage/shell delegates. It is
  called by the existing mandatory `--communication-workflow` desktop suite.
  Existing safety tests were not replaced with wording/version assertions.

Linux source compilation is not native Windows execution. Automated synthetic
shell tests do not prove a real WhatsApp Business account, browser association,
operator review or actual external delivery. No production database or real
recipient was used. No provider credentials were printed or committed.

Release assessment: do not treat this audit or migration success as publication
approval. Require the exact PR commit's existing Windows, SQL, security and
Sandbox gates and real-account operator acceptance for manual WhatsApp. SMS,
Email, Meta and SMTP delivery remain deliberately unavailable, not newly promised
features. No speculative redesign or new external service was introduced.
