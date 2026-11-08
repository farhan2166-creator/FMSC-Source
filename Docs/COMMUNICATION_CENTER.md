# Communication Center — legacy retirement and preview-only replacement

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

## Provider-neutral extension — schema 10 (draft)

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

Release blocker: implement/test secure provider-specific credentials, live
Email and Meta provider adapters, the authoritative email audience and
transactional claim, callbacks, failure/reconciliation and opt-outs. Keep
Confirm & Send disabled and demand exact-SHA disposable SQL, Windows and
private Sandbox acceptance before release.

## Stable operator workflow and free/basic integrations

Provider independence is now the product design contract, including manual
WhatsApp Business and email-client handoff paths. **Staging either provider in
schema 10 does not make it send-capable.** Manual handoff has no provider
receipt; it must never be recorded as automatic submission or delivery.

See [the long-term Communication Center product and provider contract](COMMUNICATION_CENTER_TARGET_DESIGN.md)
for free/basic rollout, no-server operation, permissions, recipient eligibility,
status semantics, risk controls, and the no-redesign extension boundary.
