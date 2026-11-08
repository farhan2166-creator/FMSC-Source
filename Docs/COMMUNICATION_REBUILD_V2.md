# Communication Center v2 — clean rebuild contract (schema 9)

**Status:** implementation branch, **not deployable** until real SQL, native UI, provider and private-Sandbox acceptance pass. Schema 8 is an intentionally unreleasable retirement waypoint. This design does not reuse `comm` or `dbo.StudentGuardianCommunication`.

## Operator workflow

One primary page, four visible steps:

1. **Select Recipients.** **Whole school, Campus, Class and Outstanding Fees are automatic group targets:** the administrator selects a scope once, not each student. Every active matching student whose Admission consent is **Yes** is automatically a candidate. The existing `StudentPicker` and `StaffPicker` are used **only for one specific record**, and one Student Picker selection may target an entire sibling/family group. No isolated communication contact editor or separate consent screen is permitted. Recipients always resolve from the current school record, placement and financial data; never from a second contact directory.
2. **Compose Message.** Choose SMS or WhatsApp and a template or enter an approved direct message. Only relevant template placeholders appear; reject unexpanded fields.
3. **Preview.** Display exact destination count, included recipients, suppressed or invalid destinations, missing Admission consent, duplicates, and estimated SMS segments. For outstanding fees display the billing period and amount snapshot and warn that balances may change.
4. **Confirm & Send.** Explicit second confirmation after preview. No send at Admission, Fee Collection, opening the page, scheduling, or application start. No default enabled automation.

Keep **History** reachable from the same page, with Pending, Submitted, Accepted, Delivered, Failed and Unknown distinguished. Never report submission as delivery. Keep **Settings** administrator-only with only SMS/WhatsApp provider profile, enable/pause and templates; provider credentials live in the protected local credential store, not SQL.

## Consent authority

- **Admission Yes is final.** A recorded Yes automatically qualifies the student for supported guardian SMS, WhatsApp, school notices and fee reminders whenever the student matches the administrator's chosen group; no second confirmation, verification or consent step. A recorded No never qualifies a student. The authoritative value is `dbo.Students.SchoolMessageConsent`; NULL means **not recorded** and is excluded.
- The existing `ContactNumber` and `AlternateContactNumber` are unchanged. Never infer consent from a phone number, a family/sibling relationship or historical migration 5–7 choices.
- `msg.ConsentAudit` logs changes without becoming another consent source. The write procedure checks `Students.Admit` or `Students.Administer`, holds a row lock, is idempotent and updates the admission authority.
- Separate `msg.Suppression` holds channel-specific opt-outs only, not a second contact book. Opt-out overrides Admission Yes. A phone changed since preview, a withdrawn consent or an opt-out must exclude the recipient again at claim time.
- Existing Admission saves remain authoritative for school/student data. The separate consent write can fail after the admission save; the UI must explicitly warn and leave communication eligibility **false** until a verified consent record exists. Tighten the consent and admission transaction boundary before release if this failure mode is unacceptable.

## Delivery persistence and safety

The fresh `msg` schema holds only the necessary operational state: `ChannelSettings`, `Template`, `Batch`, `Recipient`, `Attempt`, `Receipt`, `Suppression`, and append-only `ConsentAudit`. `msg.Recipient.PhoneE164` is an immutable *per-send snapshot*, not a contact directory.

- `ClientRequestId` is unique per Confirm operation; repeated UI confirmations must return the existing batch rather than create another. Sender claims and attempts have separate unique identifiers.
- Dispatch must atomically claim one recipient, recheck actor authorization, active school identity, consent, suppression, valid current number and provider configuration; then insert one auditable Attempt before any outbound call.
- A timeout **after** a network request is `Unknown`. Do **not** automatically retry an Unknown; reconcile by provider message ID / verified callback / explicit administrator review first.
- `Submitted` means request issued, `Accepted` means provider acknowledged, `Delivered` means verified delivery receipt, `Failed` means definitive negative result, `Unknown` means cannot determine whether it was sent.
- Callback handlers must verify authenticity *before writing*, limit payload size, correlate a provider message ID to an Attempt, deduplicate by provider event ID and enforce monotonic status (Delivered must not regress to Accepted).
- Retry only definitive failures, with bounded attempts, an explicit operator action and a fresh consent/opt-out/destination check. No unbounded background worker.
- Never log credentials, raw webhook secrets, API tokens or unrestricted recipient exports. Protect sending with distinct application permissions, including defense-in-depth checks inside SQL procedures.

## Migration and release order

1. Retire unused legacy objects in `8.sql` (PR #124), preserving migrations 2–7 and school data.
2. Add `9.sql` with a fresh disabled foundation and single Admission consent. No provider is enabled, no delivery is performed by the migration.
3. Complete the familiar pickers, preview, permission-protected SQL confirmation/claim/callback services, provider adapters and controlled history/settings. Test duplicate-click, unknown-response, stale/changed consent, opted-out phone, failed callback authentication, crash/restart and safe recovery.
4. Run Windows build, SQL disposable 3→9 and 7→9 upgrade tests, fresh 9 installation, and private Sandbox schema-3/schema-7 MDF/LDF acceptance for **the exact Source commit**. Do not modify the frozen fixture files.
5. Merge only a reviewed, fully accepted replacement. Workflow 04 must remain manual and require fresh exact-commit private Sandbox attestation. Never publish a schema-8 retirement-only build.

**Known incomplete pieces while this is a draft:** recipient query engine, send/claim SQL procedures, encrypted provider configuration, real adapters, verified webhooks, user-facing Communication Center, and end-to-end acceptance. The database tables alone do not authorize sending.

## Implementation checkpoint — automatic selection and preview

- `Core/CommunicationRecipientResolver.cs` now resolves **all active students, one campus, one campus/class, unpaid balances, one student, a family, or one staff member** directly from live school records. It never requires a picker for bulk campus/class/school selections. An Admission Yes qualifies automatically; No and Not Recorded are counted as excluded. Only current valid Pakistan mobile destinations can be considered, channel opt-outs override Yes, and shared guardian numbers deduplicate in the same preview.
- `Pages/Communication/CommunicationCenterPage.cs` implements the four visible steps, including a live SQL-backed read-only preview and exclusion counts. **Confirm & Send is deliberately disabled**, and the page is not yet in production navigation; it is not a working delivery feature.
- `Tests/ThemeSmokeChecks/Program.Communication.cs` checks phone normalization, automatic Yes eligibility, exclusion/deduplication and the disabled send action; desktop CI invokes the focused suite.
- **Still required before exposure:** transactionally authorized, idempotent send confirmation and live revalidation; provider adapters and secret storage, verified callbacks, retries/unknown recovery; History/Settings; real installer and private exact-commit acceptance. No messages have been sent by the rebuild.
