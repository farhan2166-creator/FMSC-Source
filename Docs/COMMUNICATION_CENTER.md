# Communication Center — manual WhatsApp Business

The original `comm` module and `dbo.StudentGuardianCommunication` are retired by
`Migrations/8.sql`. The owner approved deletion of their **old data even when
nonempty**: contact lists, campaigns, messages, delivery attempts, historical
communication audit rows, settings, and old guardian preferences are **not
archived or migrated**. This is intentionally irreversible except by restoring
the verified pre-migration database backup.

Normal student/admission/staff/financial records are never discarded. An old
communication preference is not treated as consent under the new schema.

`Migrations/9.sql` creates a separate `msg` system with new explicit consent
on the student Admission record. SMS and WhatsApp are initially disabled.
Authorized operators can select recipients, compose and preview, inspect
history and use permitted settings. Schema 10 supports **explicitly enabled free
WhatsApp Business manual handoff** (one operator-opened chat at a time).
Automatic SMS/WhatsApp sending and provider-verified delivery remain unavailable
without separately approved live provider integration. No automatic messaging.

Unknown legacy schema changes, external dependencies or unreviewed objects must
fail closed with rollback rather than delete unrelated data. Historical migrations
2–7 are immutable.

**Release gate:** The relevant schema-8/9/10 source changes are committed;
PR #128 is no longer a pending merge gate. A **new application publication**
still requires its own exact-commit Windows, disposable SQL and private Sandbox
acceptance. An already-applied migration is not proof that the new UI binary
was installed.

## Provider-neutral extension — schema 10 (committed source; publication separately gated)

The existing Twilio adapter is optional. New provider-neutral dispatcher
interfaces select an explicitly enabled SQL provider, while unknown providers
fail before claiming recipients. Meta Cloud API for school-owned WhatsApp and
SMTP for school email are **planned disabled profiles only**, not operational
integrations. An Email row is visible in administrator settings but cannot be
configured through the current Twilio-only dialog or used to send.

Migration 10 adds empty optional `dbo.Students.GuardianEmail` and
`dbo.Staff.CommunicationEmail` and email-only opt-out evidence. Neither field
is collected or used by the UI yet. No recipient or delivery schema is altered
for Email, so the existing phone-only confirmation/claim procedures continue to
reject it. No old guardian email choices are migrated; a future admission UI
must collect email deliberately under the existing consent authority.

Email and Meta Cloud API need separate provider-specific acceptance before **their** delivery can be enabled. They are not prerequisites for the approved, operator-controlled free WhatsApp Business workflow. Manual opening and operator reports must never become provider Delivered status. Publishing any updated executable still requires exact-head Windows/UI checks, disposable SQL and private Sandbox acceptance.

## Stable operator workflow and free/basic integrations

Provider independence is now the product design contract, including manual
WhatsApp Business and email-client handoff paths. **Staging either provider in
schema 10 does not make it send-capable.** Manual handoff has no provider
receipt; it must never be recorded as automatic submission or delivery.

See [the long-term Communication Center product and provider contract](COMMUNICATION_CENTER_TARGET_DESIGN.md)
for free/basic rollout, no-server operation, permissions, recipient eligibility,
status semantics, risk controls, and the no-redesign extension boundary.

## Free WhatsApp Business manual implementation (merged source)

After the administrator explicitly chooses **Use Free WhatsApp** in Settings,
operators holding `Communication.Send` may use the existing school/campus/class/
fee/student/family/staff audience and admission-consent preview to confirm a
**manual WhatsApp handoff**. The app stores a durable batch and one state per
recipient. The native dialog opens one validated `wa.me` chat at a time.
Operators must press Send in their own signed-in WhatsApp Business app/browser,
then explicitly choose **I Sent It** or **Skip**. A closed app can reopen the
batch from History. Opening the link is **not sending**; I Sent It is
**operator-reported**, not provider-verified delivery. No API credentials,
subscription, own server, auto-clicking or unattended bulk sender is required.

The first handoff obtains current validated recipients under a SQL transaction.
Every subsequent chat opening rechecks the student's live Admission Yes,
active/scope membership, current phone and WhatsApp opt-out before exposing
its phone/message. Direct API attempts from manually configured WhatsApp are
blocked both in C# and by a database trigger. All other direct channels remain
disabled pending actual provider acceptance. Migration 10 is **committed in the
source**; publication and local executable installation are separate steps.

## Current UI and release distinction

Migration 10 updates the database but does **not** replace the installed FMSC executable. The manual handoff UI is in the merged source; an installation still running 1.0.15 will not show the new controls until a validated later application build is installed. Do not publish a new update merely because SQL migration 10 succeeded.

In the new WinForms build, open **Communication Center** from the dashboard. Composing requires `Communication.Send` and source-record access; `Communication.View` grants History, and `Communication.Configure` grants Settings. Each can open its permitted view independently. The only operational option in Compose is **WhatsApp Business (manual)**; SMS and Email provider delivery remain unavailable. An administrator with `Communication.Configure` opens **Settings** and chooses **Use Free WhatsApp**; the button then reads **Free WhatsApp Active** rather than offering a second activation. **Pause WhatsApp** disables new chat openings, and the same settings button changes to **Resume Free WhatsApp** while paused. Sending operators additionally need `Communication.Send` plus the corresponding `Students.View` / `Staff.View` and `Fees.View` for fee notices. Use Preview, then Confirm to open one chat at a time; **History > Resume Manual WhatsApp** restores unfinished work. No button sends automatically.

For local acceptance, first confirm which executable Windows is actually running, and use synthetic destinations in a disposable copy of the database. See `COMMUNICATION_MANUAL_WHATSAPP_ACCEPTANCE.md`.

Only one FMSC handoff window may operate a saved manual WhatsApp batch at a time
on the supported per-Windows-user LocalDB installation. Close it before resuming
that batch elsewhere. A process crash releases the exclusive handle; the recorded
recipient states remain available through History. This does not verify WhatsApp delivery.
A saved Admission Yes/No cannot be reset to “Not recorded” by an unrelated details save;
choose an explicit Yes or No when changing a recorded choice.

## Audit corrections and optional record entry — schema 18

Admission offers **Message Student** for a saved record. Fee Collection offers it
under **Student Fees**, targeting only the currently selected main student, not
checked siblings. Both open this same centre in a modal window. They do not save,
reload or reset the parent form, record consent, queue a message, or send anything
on return. Existing Admission Yes remains authoritative; No/unrecorded consent
excludes the destination. No duplicate consent step is introduced.

Campus/class audiences resolve automatically; individual/family selection uses
the established StudentPicker and saved FamilyMembers links. Outstanding-fee
previews and handoffs use current `GetFeeChargeBalance` values, sum only eligible
in-scope children sharing a verified family and destination, and refuse unrelated
shared contacts. A single-student fee notice never expands to siblings. A family
with different admission dates cannot use a single `[AdmissionDate]` value.

Preview shows a scrollable complete message for each selected eligible recipient.
Fields are checked before confirmation and refreshed before handoff. If records
change after preview, the chat contains the newly checked values shown in the
handoff window; it is still the operator's responsibility to review and press
Send. An external launch is not a send. A failed launch cannot enable **I Sent
It** in that window; a saved Opened row remains a prepared/unverified state that
can be reopened. Skipped and live-excluded recipients count in history without
provider delivery evidence. Pending SQL operations block conflicting navigation.

Only `[StudentName]`, `[AdmissionNumber]`, `[AdmissionDate]` and
`[OutstandingAmount]` are supported. Outstanding amount requires the outstanding
fee audience. Unsupported or incomplete fields produce editable guidance.
Template edits refresh the composer; unavailable Email/SMS profiles remain
unavailable and editing never silently changes a saved channel.

Migration 18 adds a `SingleStudentScope` flag: historical bulk fee batches keep
their meaning even if the old UI stored an unused picker ID. Current confirmations
opt into selected-student scope deliberately. It also aligns SQL and Admission
phone validation and corrects manual exclusion completion/counts, preserving
current records. Use the guarded numbered migration chain, never a root DACPAC
publish over school data. See the independent
[audit and validation evidence](../Audit/Reviews/FMSC_COMMUNICATION_CENTER_AUDIT_2026-11-12.md).
