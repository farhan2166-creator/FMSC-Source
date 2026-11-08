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

**Known incomplete pieces while this is a draft:** provider-specific credential storage, live SMS/WhatsApp adapters, safe attempt-claim/dispatch and callback verification, administrator-controlled retry/reconciliation, editable template UI, direct integration of confirmation with a validated provider, and end-to-end private Sandbox acceptance. Confirmed SQL batches are not delivered until an approved dispatcher explicitly claims and transmits them.

## Implementation checkpoint — automatic selection and preview

- `Core/CommunicationRecipientResolver.cs` now resolves **all active students, one campus, one campus/class, unpaid balances, one student, a family, or one staff member** directly from live school records. It never requires a picker for bulk campus/class/school selections. An Admission Yes qualifies automatically; No and Not Recorded are counted as excluded. Only current valid Pakistan mobile destinations can be considered, channel opt-outs override Yes, and shared guardian numbers deduplicate in the same preview.
- `Pages/Communication/CommunicationCenterPage.cs` implements the four visible steps, including a live SQL-backed read-only preview and exclusion counts. **Confirm & Send is deliberately disabled**, and the page is reachable by users with the Communication.View permission; it is not a working delivery feature.
- `Tests/ThemeSmokeChecks/Program.Communication.cs` checks phone normalization, automatic Yes eligibility, exclusion/deduplication and the disabled send action; desktop CI invokes the focused suite.
- **Still required before delivery:** transactionally authorized, idempotent send confirmation and live revalidation; provider adapters and secret storage, verified callbacks, retries/unknown recovery; History/Settings; real installer and private exact-commit acceptance. No messages have been sent by the rebuild.

## Checkpoint — protected confirmation, History and Settings

- Migration 9 now installs **three distinct permissions** (\`Communication.View\`, \`Communication.Send\`, \`Communication.Configure\`) without granting them to any ordinary role. It also installs \`msg.RecipientSelection\`, a mobile-normalization function and \`msg.ConfirmBatch\`. The stored procedure rejects stale previews by independently selecting the **complete** current school/campus/class/fee/family/staff audience, validating Admission Yes, destinations and channel opt-outs. It serializes identical requests and rejects changed content or recipients. It never performs network I/O.
- \`Core/CommunicationBatchStore.cs\` prepares an exact preview snapshot for SQL and requires the calling UI to preserve a stable client request GUID. The production **Confirm & Send** action remains disabled and does not call this store until a verified transport exists.
- \`msg.ReadHistory\` returns batch-level status counts without a contact export; \`msg.ReadChannelSettings\`, \`msg.ReadTemplates\`, \`msg.SaveTemplate\` and \`msg.PauseChannel\` are permission-gated. The Communication page can navigate to **History** and **Settings**, display stored templates, and pause either channel. It has no enable-provider button and stores no credentials in SQL.
- The synthetic lifecycle suite checks unauthorized confirmation/history/settings, default channel refusal, exact consent matching, duplicate clicks, stale/withdrawn consent, changed numbers, opt-out, template persistence and pausing. It enables a **synthetic** no-network provider only in its disposable SQL fixture.
- **No live provider, callback receiver, dispatcher or actual delivery is implemented yet.** No message has been sent as part of this work. Both PRs remain draft/unmerged until the complete release acceptance.

## Protected provider credential foundation (draft-only)

- `Core/CommunicationProviderCredentialStore.cs` now seals per-channel provider credentials with Windows DPAPI **CurrentUser** and a credential-specific entropy value under the Windows user's LocalAppData folder. The directory ACL is restricted to that Windows identity; writes are temporary-file-then-replace. Secret bytes are zeroed after sealing/unsealing. No API key, callback secret, or account password is placed in SQL, GitHub, release assets, logs, or the settings grid.
- Installing or changing a protected credential requires `Communication.Configure`; reading the secrets for a future dispatcher requires `Communication.Send` and a matching expected provider code. Settings may read only non-secret provider metadata. This code **does not** activate channels, connect to the internet, create a send worker, or expose a credential editor yet.
- A future real provider adapter must be explicitly selected, verify callback signatures, and reconcile attempts before Settings can enable channels and the UI can expose Confirm & Send. Windows account loss/reinstallation requires provider credential re-enrollment; SQL backups do not contain secrets.
- `msg.ClaimRecipient` holds a transactional recipient lock, rechecks the persisted batch audience against live school records, checks provider/channel state and opt-outs, and records a uniquely keyed **Unknown** attempt before any potential HTTP call. No automatic retry of an unknown outcome is allowed.

## Consolidated dispatch, receipts, retries and administrator templates (draft-only)

- Schema 9 includes `msg.RecordProviderResult` and `msg.ApplyVerifiedReceipt`. A request accepted by the provider is **not Delivered**. Delivery only advances after a verified signed webhook or an authenticated provider read. Receipt event IDs are unique; conflicting replay payloads are rejected; late Accepted receipts cannot overwrite Delivered. Early verified receipts can be correlated once the provider message SID is persisted.
- `msg.ApproveDefinitiveRetry` requires `Communication.Send`, a documented manual reason, an actual definitive Failed attempt, and at most **three** total attempts. Unknown outcomes are **never** eligible for retry, including after a crash, timeout or cancellation. A fresh call to `msg.ClaimRecipient` rechecks the original selected audience, current Admission Yes, active placement/fee eligibility, valid current phone and opt-outs.
- `Core/CommunicationTwilioTransport.cs` adds an optional concrete Twilio SMS/WhatsApp REST adapter with one-shot submission, no automatic network retries, bounded JSON responses, explicit status mapping, signature verification over the original trusted HTTPS callback URL and all form values, account-SID correlation, and authenticated **GET-only** provider reconciliation. WhatsApp sender configuration must use its approved `whatsapp:` prefix; actual WhatsApp business template and session requirements remain provider-policy-dependent.
- `Core/CommunicationDispatchStore.cs` is an internal manual-only claim/send/reconcile boundary. The SQL claim is committed before any HTTP POST; an interrupted or unpersisted outcome stays Unknown. It never starts automatically from the app, Fee Collection, Admission, migration or navigation.
- Administrator Settings now provides one protected provider setup dialog and a compact New/Edit template dialog; configuration immediately pauses the channel before changing DPAPI secrets and updates the permitted provider identity in SQL without enabling the channel. Compose offers only active, purpose/channel-appropriate templates to authorized senders. No second consent/preferences were added.
- SQL lifecycle tests cover forbidden sends, provider mismatch, verified receipt replay and monotonic delivery, early/late state transitions, manual-only failure retries capped at three, and paused provider configuration. Synthetic Windows checks cover Twilio status mapping, trusted callback HMAC validation and a disabled send action.

**Still blocked:** There is intentionally no automatic worker, publicly hosted trusted webhook, verified real Twilio SMS/WhatsApp end-to-end credential acceptance, safe callback ingress deployment, or production confirmation/navigation activation. The client cannot receive a public Twilio webhook behind arbitrary school NAT networks without an approved HTTPS service. Schema 9 remains a draft, both PRs must stay unmerged, and Workflow 04/publication remains blocked until real provider, installer and private exact-commit Sandbox acceptance have passed. The existing workflows have not been weakened.

## Explicit legacy-data retirement decision (PR #128)

Historical `comm` rows and `dbo.StudentGuardianCommunication` values are not needed
for the replacement. Migration 8 deliberately discards them even if nonempty;
there is no `legacy_comm_v7` archive. This includes old contact records,
campaigns, messages, attempts and the old module's audit evidence, **not** any
application-wide audit restoration. The verified pre-migration backup is the
only retained recovery copy. A fresh schema-9 Admission Yes/No is never inferred
from historical WhatsApp or guardian preferences. Unknown dependencies or
schema extensions fail closed. Before deployment, verify this behavior on
a **disposable restored schema-7 school backup** without altering the working
1.0.14/schema-9 installation.
