/*
BUSINESS DECISIONS — source-resident policy record, consolidated 2 November 2026.

Keep this record with the C# source even if documentation is lost. BD identifiers
link concise comments at implementation sites to the rationale here. These are
maintenance constraints, not additional runtime validation. C# and SQL jointly
enforce behavior; a UI check never replaces a writer's permissions/transaction.
Later dated decisions supersede earlier rollout descriptions, not saved history.

ADMISSION AND PLACEMENT

BD-01 Registration (31 Oct; sibling clarification 2 Nov): Admission owns pending
registration. Pending Save confirms only after required-detail and official-number
review; Save Registration Draft remains non-confirming. The sibling panel excludes
the loaded child. That child uses its main Confirm & Save and registration actions;
siblings retain row actions. Do not restore the retired Family Admission route.

BD-02 Reopening (migration 64): only a cancelled pending identity without an
official number can reopen. Reuse its identity/reference with a new pending
placement and a reviewed reason/date, not before cancellation. Preserve cancelled
episodes, family links and finances; do not revive cancelled bills/agreements.
Recheck the financial/placement snapshot at commit. Identical request replay is
safe; changed replay is refused. Admitted former students use readmission instead.

BD-03 Activation: placement start records class history; billing commencement is
an independent reviewed date. Apply the school cutoff to billing commencement,
not enrollment start. Admission/selected one-time charges use the chosen billing
date. Pending registration is not admission or permission to post ordinary fees.

BD-04 Historical placement (migrations 57/61): confirmed past-year admission can
remain Active with BillingReady=0; it is not awaiting return. Do not enable old
billing to repair that classification. Confirm Return requires a confirmed current
year placement awaiting billing, excluding expired/future years and legacy drafts.
Historical dues remain collectible; factual placement history stays editable.

BD-05 Hold/resume (21 Oct; migration 30): hold blocks automatic and ordinary manual
posting, not collection of existing dues. Preserve identity, placement start,
annual fees, receipts and earlier-placement balances. Resume explicitly reviews
return date, billing month, restored waived fees, missing monthly fees, optional
unbilled annual fees and available credit. Never duplicate or silently recreate
reversed fees/payments. Prepare a later-year placement before resuming there.
Certificates remain recorded and do not alone prohibit resume. Existing Left
records require individual reviewed adoption as a hold; never convert them all.

BD-06 Departure: record a date and reason separately from the fee decision: use
cutoff, keep issued monthly fees, or waive from a chosen month. Reducing paid fees
requires an explicit advance/refund choice and Fees.Adjust. Keep annual/one-time
fees and older-placement dues. Departure prevents future posting even when issued
bills are retained. Historical departure/transfer dates must fit their recorded
placement/year; never silently choose today/year-end. Hold/resume requires a
placement covering today, including for backdated requests (migration 61).

BD-07 Readmission (1 Nov; migration 68): an official former student in Left,
Inactive or legacy Withdrawn may re-admit after date, placement and fee review.
Active/Pending students, active enrollment or unresolved hold are ineligible.
Final class does not automatically mark Left or require an artificial next class.
Record departure/completion first. Keep departure, certificate and number history;
cancelled pending registrations use Reopen, held students use Resume.

BD-08 Transfer (1 Nov; migration 69): route by campus, then class, then section.
Same campus/class with only a section change is Transfer Section, with no class
fee synchronization. Class transfer rejects the current class but may move to a
higher or lower class in the same year; next-class rules belong to promotion.
Historical/nonbilling transfers preserve fee plans and BillingReady=0. Do not
relabel past TRANSFER_CLASS events automatically. New-placement active choices
are a C# policy; later deactivation must not invalidate historical placements.

BD-09 Annual promotion: select the immediate next class in the same program using
the full catalog's order and ID tie-breaker; do not skip an unconfigured class.
Repeat Class retains class/section in the next year; HOLD remains its stored
decision and history displays Repeated Class. New placements await Confirm Return
before billing. Carrying concessions is explicit, requires Fees.Adjust, excludes
expired/yearly/purchase concessions, caps fixed amounts at the new rate and lets
a full concession cover that rate. Keep promotion requests and undo history.

FEES, COLLECTION AND DOCUMENTS

BD-10 Fee terms: class/future-plan changes must not silently rewrite posted bills.
Review agreement changes, issued-bill corrections or both, preserving terms outside
the chosen period and restoring the prior agreement after a temporary concession.
No bill is manufactured just to represent a concession. Correcting a closed
placement's bill never reopens it or rewrites its historical agreement.

BD-11 Shared fee editor (31 Oct): Admissions and Collection use the same rules,
options, factory, permissions, snapshots and save recovery. Admissions has action
tabs; Collection preselects its fixed action. Collection deliberately defaults
both From/Through to the clicked/start month; Admissions keeps its normal range.
Do not make their defaults identical as a cosmetic consistency change.

BD-12 Concessions and money: discount reduces the full fee; correcting a recorded
payment is distinct from returning cash. Reductions consume unpaid dues first;
paid differences become the explicitly selected advance or refund. Optional credit
use follows the reviewed manual collection order: in family collection, every
selected sibling's own advance applies only to their own dues; unselected siblings
are excluded, and unused advance stays with its owner unless transferred explicitly.
Future posted fees may be prepaid deliberately only in collection; posting and
automatic billing do not consume advance. Credit use is
settlement, never new cash, and confirmed collection saves a receipt snapshot.
Advance is allowed only when the collection date is today; backdated
collections may use cash, not advance, subject to existing enrollment-date
validation. The 4 Nov owner decision supersedes earlier automatic credit use.
No silent money rounding.

BD-13 Collection: posting creates dues; collection settles them. Partial and family
collection keep each child's allocations, concessions and debt attached to that
child even on one family receipt. Do not replay a committed collection because
printing failed. Payable rows may offer fixed bill discount actions; newly waived,
not-yet-documented rows are read-only, unallocatable and offer details only.
An issued document consumes that waiver baseline; historical settlements do not
reappear. Finish/clear a payment draft before submitting a fee change or its note.

BD-14 Charge cancellation (migration 60): an unpaid cancellation requires both
Fees.Collect and Fees.Adjust through every route and at SQL commit. Paid/partial
charges cannot take that shortcut. Keep the complete reason, actor and timestamp
separate from original notes; cancellation retains the charge/history. This is
bill cancellation, not the removed certificate cancel/void feature.

BD-15 Transport (27 Oct; migration 58): its cutoff is independent of admission's
fee cutoff. Days 1–28 or blank are supported; before cutoff permits this month,
on/after cutoff starts next month, blank permits this month throughout. Paid or
part-paid months stay charged. Stop cancels wholly unpaid permitted future
transport charges and ends the plan, without refund/credit or erased allocations.
Start posts an eligible current month once; future starts await billing. Older
cancelled copies remain history. Require an Active placement, recheck cutoff,
payments and snapshot under the billing lock, and retain durable request recovery.

BD-16 Monthly reversal: only the latest eligible posting for the selected year
can reverse, whole-school and all-or-nothing. Later posting, payment history or
ineligible placement may block the whole operation. Recheck the reviewed snapshot
and reason at SQL write; never erase paid transaction history or enable exclusions.

BD-17 Daily closing: adjustment rows intentionally have no entered Actual Amount;
blank is not zero. Ordinary methods require whole rupees, rejecting fractions
without rounding. Preserve historical precision in display/print. Closing is a
revisable reconciliation snapshot, not a lock on backdated transactions, and does
not edit student payments. Recheck/report any changed closing position explicitly.

BD-18 Receipt paper trail (migrations 55/56): visit-based version 3 supersedes the
earlier cumulative layout. Begin with the previous relevant receipt's closing
dues, adding later fees/adjustments/concessions only; settled fees stay in history.
Use saved monotonic visit/student-order metadata for backdated or same-second
events. Bills use that position without consuming the receipt baseline.
Show full fee amounts and plain fee names; deductions, payment and balance must
reconcile. Issued/annual reprints use saved participants, fees, plans and balances,
not today's family or terms. Disclose incomplete legacy evidence; do not invent it.
Retain original snapshots, payment corrections and later-change evidence.

BD-19 Fee-change notes: use FeeChanges.rdlc and the existing numbered, zero-money
acknowledgement writer. Show saved before/change/after dues, signed changes and
reasons; unchanged unpaid fees reconcile separately. Saved plans are a separate
non-billed section. Omit unchanged plans; without prior evidence say Saved Fee
Plan, never invent old terms. Acknowledgements receive no cash or applied credit.
Ordinary outstanding bills still require positive dues.

BD-20 Student Fee Account: investigation reuses existing readers, receipts,
correction form and recovery writers. Statement access needs Reports.View;
collection users with Fees.Collect may use Receipts without gaining statement
SQL/export access. Payment/credit readers also need Fees.View; corrections need
Fees.Adjust; annual receipts retain Fees.Statement.View. Include Siblings remains;
arbitrary group statements and duplicate standalone correction routes are retired.
Keep original family receipt totals distinct from per-child shares and later
adjustments. Label current all-date dues/advance separately from filtered totals.
The statement is not an immutable running ledger; do not fabricate a balance or
net outstanding against credit. Late or prior-user reads cannot enable actions.

CERTIFICATES, SETUP AND REPORTS

BD-21 Certificates (25 Oct; migrations 51/52): no cancel/void or replacement-from-
cancelled-copy flow without a new owner decision. Create, Save while uncollected,
Print, Mark Collected then lock; Issue Again creates a separate copy and reprint
uses the selected copy. Never undo departure/fees or silently mark collected.
Migration 52 removed cancellation columns only on the approved clean baseline;
refuse snapshots with cancelled copies rather than revive them. Preserve records,
issue/collection evidence and request markers. Leaving clearance is optional.
Leaving-copy duplicate checks are scoped to the recorded leaving episode, not
all past certificates; preserve earlier copies when a readmitted student leaves.

BD-22 Certificate fields: do not reintroduce the removed Passed Exam gate.
Use saved eligible placement/year evidence and the writer's checks. Staff
designation and subjects may prefill from the selected staff record but remain
editable. Keep the school's finalized certificate wording and separate print
permission checks; creating/viewing a record alone does not authorize printing.

BD-23 Academic years (31 Oct; migration 63): any enrollment, including historical
or left, and either side of promotion history locks Start/End dates. Name,
Remarks and Current remain editable; age alone does not lock an unused year.
Names use consecutive four-digit years. SQL must reject stale date changes too.
Never repair a historical year by renaming dates or deleting its usage.

BD-24 Setup catalogs (2 Nov): Campus - Classes / Sections is the setup entry point;
dialogs retain the complete global class/section editors. Rename is school-wide;
creating a catalog row does not assign it to a campus. Apply Changes alone saves
assignments. Restore unsaved matrix differences by IDs after catalog refresh;
renames/reorder keep ticks, deleted keys disappear, new keys use saved baseline.
Campus switching and small setup drafts retain the approved prompt-free discard;
block closing during submitted writes. Bulk fee drafts keep their own guards.

BD-25 Drafts/UI: explicit New Record in MasterPage deliberately discards its
draft without another confirmation; switching/leaving retain their own guards.
An untouched date default alone is not a substantive certificate draft. Preserve
the approved Collection/Admission layouts, warnings and critical financial
consequences while trimming routine guidance. Do not treat optional subtitles or
embedded headers as a reason to change workflows or introduce blanket prompts.

BD-26 Reports (2 Nov): default order follows each report; optional sorting precedes
pagination and reaches print/export. Preserve groups/totals and typed amounts,
dates and textual references. Grid page preferences override shared alignment;
names/prose stay left, compact categories default centered. Direct PDFs use black
text/rules with centered titles, filters, headings and applicable data. Do not
change bill/receipt fonts or wording as a side effect of management-report styling.
Outstanding aging days cannot be negative; stored charge due dates are valid.

BD-27 Report periods (2 Nov): custom dates or 1/3/6/12-month academic-year blocks
choose the same report date parameters, not two simultaneous filters. Clamp blocks
to saved year dates, preserve saved-view year/block identity, and return to custom
mode after manual date edits. Existing grouping and each report's date meaning
remain unchanged. Current-balance reports do not become period activity reports.

BD-28 Receipt wording (2 Nov): print Cash + Credit and Date. Deductions name only
applicable Discount, Waived and Credit; hide the row when none apply. Free is the
stored sibling-concession kind and displays as Discount, not Waived. Unknown old
types use Discount; changed amounts cannot reuse stale waiver classification.
This changes captions only, never settlement arithmetic or stored money.

DATABASE, ACCESS AND RECOVERY

BD-29 Release data: the owner supplies the matching detached MDF/LDF pair beside
the project for packaging; missing development snapshots are not release blockers.
Never overwrite an attached school's data with a DACPAC/template. Validate supplied
release assets, compatibility and recoverability for the actual install scenario.
Keep all required SQL changes in numbered migrations; do not apply unrecorded fixes.

BD-30 Backup/restore: reject unknown/foreign identities and incompatible structure;
warn about loss of newer records when restoring an older verified backup. Keep
safety copies, rollback and failed-restore recovery. A dated test pass is not
current installer/operator/physical-printer acceptance. A historical approved target
was to refuse version mismatches without upgrade; it is not proof that the current
installer has retired migrations. Do not remove migration/compatibility safeguards
until that separate transition is actually implemented and verified.

BD-31 Audit retirement: migration 59 was the explicitly approved batch exception
for 19 generic audit writers. Preserve historical audit data/readers (fee history
still uses them), operational histories, bills, allocations, credit, certificate
issues and recovery markers. Keep financial review hashes/JSON; only audit-only
snapshots were retired. Future procedure changes retire their own generic audit
writes, not unrelated procedures. Missing old evidence stays disclosed.

BD-32 Access/actor: preserve Administrator's full authority and the approved
Accountant/grant model. Screens, shortcuts and direct handlers use destination
permissions, not sidebar visibility. Recheck actor/permission on write and after
async reads. Attribute opening charges/credit application to the authenticated
actor, never a previous operator. Do not rewrite historical attribution.

BD-33 Unknown outcomes (migration 60): flush database/operator/request-bound
recovery intent before financial/identity writes. Only the bounded, verified
rollback signal (SQL state 60) is a correctable refusal; an enclosing transaction
retains rollback ownership. Timeouts, lost replies and failed reconciliation reads
stay uncertain: check saved outcome before resubmitting. Preserve entered details
and photos; committed identity/number/receipt outcomes are never replayed on print
failure. Preview is read-only; stale reviewed snapshots must fail at commit.

BD-34 Accepted audit limits: the local SQL trust boundary, decoded-photo RAM
measurement, SQL photo-size policy and live-module semantic verification have
separate accepted/deferred scopes (BL-03, FR-07/08, F-07). Do not silently redesign
authentication, impose a new photo-size limit or claim those gates passed while
tidying comments. Preserve each dated finding's evidence and supersession status.

BD-35 Communication deployment: the optional module has its own structural/module
contracts and version. Worker/webhook validation is independent of the main
migration version. Opening the page or rerunning installation does not install
the module, start dispatch or enable delivery. Preserve incompatible-module and
permission failures, exact-definition verification and existing message history;
never approve a changed module merely by synchronizing metadata hashes.

BD-36 Communication delivery: saved school telephone numbers are candidates, not
verified ownership/consent. Review contacts, purpose/channel consent and templates.
Disabled/master-pause is the default. Simulation makes no external request and
must not become live when settings change; require a new live campaign. Explicit
submission, approval/limits, provider credentials and dispatcher start remain
separate. Queued/accepted is not delivered. Keep authenticated webhook evidence,
idempotency, delivery history and role checks; never add Accountant grants merely
to expose the page. Missing provider infrastructure is not proof of delivery.

BD-37 Staff: hire dates cannot be future; an entered leaving date cannot precede
hire. Add Job and Edit Job use their own dialogs from the staff employment panels,
without restoring the redundant header Employment button. Saved designation and
subjects prefill certificates but remain operator-editable.

BD-38 Global record search: the dashboard text box keeps feature/page navigation;
Ctrl+K separately searches students, saved bills/receipts and payment references.
Default to All; an optional permitted record type filters SQL before limiting results.
Name/admission lookups include saved document participants, never today's family;
open the matched child's account while keeping the original document unchanged.
Do not duplicate a saved receipt's linked payment aliases for a person lookup;
keep those aliases searchable by number and keep standalone legacy entries.
Limit results and cancel stale reads; recheck permissions, actor and database
endpoint before displaying/opening. Explicit selected-record IDs override saved
workspace selections and keep normal busy/unsaved-work guards. Open saved
snapshots/readers only; lookup must not issue a bill, allocate a number or record
money. Keep legacy payment entries distinct when no original receipt is available.
*/
