# Communication Center — schema 10 and manual WhatsApp Business

The original `comm` module and `dbo.StudentGuardianCommunication` are retired by
`Migrations/8.sql`. The owner approved deletion of their **old data even when
nonempty**: contact lists, campaigns, messages, delivery attempts, historical
communication audit rows, settings, and old guardian preferences are **not
archived or migrated**. This is intentionally irreversible except by restoring
the verified pre-migration database backup.

Normal student/admission/staff/financial records are never discarded. An old
communication preference is not treated as consent under the new schema.

`Migrations/9.sql` creates a separate `msg` system with new explicit consent
on the student Admission record. Both SMS and WhatsApp are disabled at
installation. Authorized operators can open the dashboard Communication Center
to select recipients, compose and preview, inspect history, and access permitted
settings. **Confirm & Send remains disabled** until secured callbacks, a real
provider and end-to-end acceptance are complete. No automatic messaging.

Unknown legacy schema changes, external dependencies or unreviewed objects must
fail closed with rollback rather than delete unrelated data. Historical migrations
2–7 are immutable.

**Release gate:** PR #128 must not merge or publish before the user's restored
schema-7 test, SQL rollback checks and exact-commit release acceptance.

## Provider-neutral extension — schema 10 (merged; not yet published)

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
disabled pending actual provider acceptance. Migration 10 is still **draft**.

## Current UI and release distinction

Migration 10 updates the database but does **not** replace the installed FMSC executable. The manual handoff UI is in the merged source; an installation still running 1.0.15 will not show the new controls until a validated later application build is installed. Do not publish a new update merely because SQL migration 10 succeeded.

In the new WinForms build, open **Communication Center** from the dashboard (requires `Communication.View`). The only operational option in Compose is **WhatsApp Business (manual)**; SMS and Email provider delivery remain unavailable. An administrator with `Communication.Configure` opens **Settings** and chooses **Use Free WhatsApp**; the button then reads **Free WhatsApp Active** rather than offering a second activation. **Pause WhatsApp** disables new chat openings, and the same settings button changes to **Resume Free WhatsApp** while paused. Sending operators additionally need `Communication.Send` plus the corresponding `Students.View` / `Staff.View` and `Fees.View` for fee notices. Use Preview, then Confirm to open one chat at a time; **History > Resume Manual WhatsApp** restores unfinished work. No button sends automatically.

For local acceptance, first confirm which executable Windows is actually running, and use synthetic destinations in a disposable copy of the database. See `COMMUNICATION_MANUAL_WHATSAPP_ACCEPTANCE.md`.
