# Communication Centre — final upgrade contract (decisions 1–64)

> Status: implementation contract, **not** proof that a feature has shipped. Do not
> mark this document complete until the relevant executable tests and Sandbox gate pass.

## Product boundary

Enhance the existing WinForms / SQL Server communication subsystem, rather than
introducing parallel messaging, template, recipient-resolution, or history services.
The UI must remain simple: action-focused Home; one recipient selector; clean composer
with live preview; Queue; History; searchable categorized Templates; Settings with
Sending, Queue & Schedule, and Automation tabs. New Message defaults to the school's
preferred compatible method, and paid channels require informed confirmation.

The existing installation has never been used for messaging: historical communication
batches and history do not need migration. This **does not** authorize deleting or
modifying school, fee, admission, or backup data. Avoid silently resending legacy work.
Provide backward-compatible migrations and strict rollback/compatibility checks.

## Mandatory operating rules

- Manual WhatsApp Business: prepare, open each chat, operator sends, operator reports
  outcome; resume later. Never describe chat opening or self-report as verified delivery.
- SMS and Meta WhatsApp API: send only after authorization and confirmation. Provider
  acceptance is not delivery; collect delivery receipts where supported. No incoming
  API reply inbox and no manual WhatsApp reply integration.
- Automation modes by event: Off, Prepare for Review, Send Automatically; **all Off
  initially**. Events: admission, payment, outstanding fee, approaching due date,
  overdue follow-up, announcement, examination/result, birthday/greeting. Attendance
  automation is excluded.
- Smart recipient resolution: school / campus / classes / student / family; family
  consent is authoritative, use current approved family-contact selection, deduplicate
  appropriately. Revalidate at dispatch. An automatic event resolves current contacts,
  while a reviewed manual audience never silently expands. A changed manual destination
  requires re-review.
- **Withdrawn / school-left students receive no automatic messages**, including debt
  reminders. Other active siblings remain eligible for relevant communications.
- Consolidate related family financial reminders; preserve unrelated content and
  individual transaction receipts. Use current balances for automated bills and
  fee statements; preserve immutable transaction receipt evidence.
- Review-mode batches support grouped preview, exclusions, and explicit approval.
  Editing content or recipients triggers re-review. Schedule and priority edits can
  skip full re-review if no material sending change occurs.
- Language: administrator-approved English and Urdu template versions, school default
  and manual selection. No automatic translation. Respect API provider template
  approval and Unicode SMS cost/segment behavior.
- Form integrations: retain Admission / Fee Collection; add other eligible forms and
  reports through compact context-aware dialogues, reusing one engine.

## Queue, scheduling, and recovery

- SQL-enforced single active worker per school; safe leases and atomic claims across
  computers, never merely a process mutex. Runs at authorized Windows sign-in without
  opening the FMSC UI; uses individually authorized access to encrypted school secrets.
  No plaintext secrets, unauthorized machine access, or staff password storage.
- Schedule now / later / optional recurring (daily, weekly, monthly, custom).
  Administrator-configured hours (initial default 08:00–18:00), school working days
  and holidays; explicit urgent exceptions cannot bypass consent or master pause.
- Smart speed with administrator-defined maximum (illustrative 60/min), plus
  administrator-configurable daily provider limits, reserved capacity for important
  messages, and **no automatic over-limit charges**.
- Priorities and per-family daily routine limits (initial 3/day) with carefully
  specified important-transaction exceptions. Default expiry depends on message type;
  scheduled announcements initially expire after 24 hours where applicable.
- Queue views: Needs Attention / Upcoming / All Active; completed entries in History.
  Detailed per-batch recipients and outcomes behind expandable details.
- Administrator-only persistent Pause All. A pause does not retract an already
  submitted or in-flight message. No auto retry of unknown-outcome attempts. Safe
  retry only of provably unsent / provider-confirmed retry-safe attempts.
- Detect outages and catch up: confirmations only for valid events missed since
  activation; recalculate current unpaid balances; do not flood overdue reminders;
  skip expired notices and past birthdays. On first enable, baseline historical
  transaction events rather than replaying past admissions and payments.
- When changing or disabling a rule, pause affected automatic work pending review.
  At academic-session transition, pause class/session-specific rules that require
  review; unchanged school-wide rules can continue.
- Changing provider identity, credentials, compatibility, or authorization pauses
  affected dispatch; independently healthy channels keep working. Optional fallback
  per rule, only when original submission is proven unsent and charge/channel
  authorization permits.
- During application installation/update: stop claiming new work, journal in-flight
  attempts, safely pause, update/migrate/verify, resume when safe. Failed migrations
  preserve prior application/database compatibility.
- During **database restore**, assume the restored database may predate successful
  sends. Reconcile with *independently retained* evidence and provider identifiers.
  Uncertain attempts must not be released for duplicate sending. An admin may review
  unresolved work; no automatic 'assume unsent' rule.
- If the original sender loses authorization, hold their unsent approved work for
  explicit administrator review and takeover. Automated system authorization and
  staff ownership must be distinguishable.

## User-facing details

- Homepage: New Message prominent, compact queue attention summary. No technical
  fields on the everyday composer; scheduling/queue under advanced actions.
- Templates: searchable categories (Fees & Payments, Admissions & Students,
  Examinations & Results, Announcements & Greetings), contextual suggestions.
- Review: personalized per-recipient previews, current recipients and exclusions,
  optional send-to-authorized-test-number, explicit real-batch confirmation.
- History: compact rows with expandable recipient-level evidence, who prepared,
  approved and dispatched; distinguish accepted, confirmed delivered, failed,
  unknown, and manually self-reported sent. Search school record/admission number,
  guardian, student, channel, dates/status; permission-controlled CSV/PDF exports.
- Notifications: quiet routine operation, grouped actionable Windows and in-app
  alerts only; do not reveal financial/guardian content on lock-screen.
- Family-level temporary exceptions for selected automatic event types, optional
  expiration and audited reason. No mutation to consent or financial balances.
- Corrections to previously sent financial or school-record content prepare a
  separate follow-up for approval, preserving the original audit event.
- Temporary files bounded and automatically cleaned without deleting unresolved
  provider attempts or required financial evidence. Default detailed history
  retention 12 months, subject to statutory/audit protection.

## Single release acceptance criteria

1. One supported SQL migration path from the current schema, with installer,
   complete installer, updater, compatibility manifests and uninstaller aligned.
2. SQL concurrency tests: exactly one claim, idempotent confirmation, no duplicate
   after worker crash, restart, connectivity loss, session change, provider change,
   machine takeover, rollback, or database restore.
3. Authorization tests: staff scope, admin takeover, guardian consent, opt-outs,
   withdrawn student, family sibling routing, changed mobile, unauthorized machine,
   and credential isolation.
4. Financial tests: receipts tied to immutable transactions; current outstanding
   balances; family consolidation; corrections; document preservation.
5. Provider tests: accepted vs delivered vs unknown; provider throttling and
   allowances; uncertain attempt never auto-retried; optional fallback does not
   bypass approved cost or channel.
6. WinForms behavioral tests for composer, Queue, templates and contexts; don't
   bind release to exact pixels, obsolete button captions, or frozen test fixtures.
7. Full Sandbox acceptance for the **exact Source commit**, safe updater recovery,
   and successful Workflow 04 release gate. Publish only after all checks pass.

Implementation may use internal commits within one feature branch, but should
ship as **one complete, verified upgrade**, not misleading partial publication.
