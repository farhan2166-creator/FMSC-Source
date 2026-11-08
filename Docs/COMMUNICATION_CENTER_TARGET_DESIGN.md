# FMSC Communication Center — stable product and delivery architecture

**Decision:** One school-facing Communication Center. Different delivery providers
are plugins of its delivery layer, **not separate school workflows**. The
architecture must support free/manual WhatsApp Business, ordinary school email,
and optional official provider APIs without another Communication Center rewrite.
This is a **target contract**, not a claim that these delivery paths work yet.
Current delivery remains disabled pending acceptance. Keep migrations 2–9 and
historical decisions intact. The unmerged schema-10 PR is the additive starting
point.

## 1. What the school operator sees

One straightforward, four-stage message workflow:

1. **Recipients:** choose Entire school, campus, class, outstanding fees,
   one student, one family/sibling group or one staff member. Bulk selection
   resolves automatically from active school records and Admission consent; no
   record-by-record picking for an ordinary campus/class send. Use familiar
   FMSC pickers only for a specific child/family/staff member.
2. **Message:** choose **WhatsApp, Email or SMS**, write a message or choose a
   purpose-appropriate template. The operator does **not** choose an HTTP API
   or copy credentials. A configured channel/mode is shown in readable terms:
   "WhatsApp Business — send yourself", "School email — send from FMSC", or
   "WhatsApp API — automatic submission". Show unavailable options honestly.
3. **Review:** current eligible destinations and counts, reasons for excluded
   recipients, consent/no-consent, invalid/missing numbers or email addresses,
   suppressed contacts, duplicate destinations, fee period/balance snapshot,
   chosen sending mode, estimated cost or explicit "provider limits may apply".
   Mask contact information for excluded persons.
4. **Confirm:** an explicit second confirmation. For a manual mode this opens
   one recipient handoff at a time; for an approved direct provider this
   creates one durable, idempotent batch and proceeds only through its
   permission-checked transport boundary. No implicit sends from Admission,
   Fee Collection, navigation, startup or a scheduled task.

Keep **History** and **Settings** on the same page. History shows actionable
pending, manually prepared, acknowledged, failed, uncertain and opt-out states.
Settings is simple: channel, sending method, identity, connection test,
limits, pause/unpause only after acceptance, and protected credentials.
Only administrators configure providers; permission to send is separate.

## 2. Independent concepts (do not merge these in code or SQL)

- **Audience:** school/campus/class/outstanding fees/student/family/staff.
- **Channel:** WhatsApp / Email / SMS.
- **Provider:** e.g. WhatsAppBusinessManual, MetaCloud, Twilio,
  EmailClientManual, Smtp. Provider names are internal, not operator jargon.
- **Delivery mode:** OperatorHandoff versus DirectProvider. A handoff
  cannot be processed by an API send/receipt adapter.
- **Destination:** validated per-recipient phone E.164 or email address,
  from the existing school record, snapshotted per batch. Not a new contact
  directory. Students and staff have different authoritative data paths.
- **Evidence:** local operator event, provider submission response, authentic
  delivery/bounce evidence; these are never interchangeable.

One stable service contract should coordinate: prepare audience, validate,
confirm, reserve/claim, deliver or hand off, record evidence, reconcile and
read history. Provider adapters implement only their respective transport
mode. Adding another provider does not alter school targeting or the main UI.

## 3. Practical operation without a school-owned server

### A. WhatsApp Business — free, operator handoff (first implementation)

Use the official click-to-chat `https://wa.me/<international-number>?text=<encoded-message>`
mechanism. FMSC checks consent and current number, previews the exact text,
then opens **one** chat; the user reviews and presses Send in the WhatsApp
account logged into their browser/app. **Opening a chat is not sending.**
Never bypass WhatsApp's operator interaction with browser scripting, unofficial
bulk automation, screen clicks or sessions stolen from the browser.

Show **Open next chat**, **Skip**, **Stop**, and **I sent this manually**.
The latter is clearly *operator-reported*, never "Delivered". Handoffs
must be durable and resumable so a Windows/WhatsApp crash does not silently
skip or resend anyone. Never open tens of tabs or automatically advance a
contact as sent. Verify the intended school WhatsApp account is signed in;
FMSC cannot reliably infer the account or prove a user pressed Send.
Provider receipts and WhatsApp API idempotency are not available in this mode.

Manual mode can be slow for hundreds of guardians; disclose this before the
operator starts. The same batch can later use Meta's official adapter only
after that adapter has passed acceptance, **not midway through an uncertain
manual batch**. Preserve attempt history and operator attribution.

### B. School email — direct from the Windows PC (first direct channel)

The school may use an existing mailbox (including a supported free account)
through authenticated SMTP or an approved mail provider API. FMSC need not
host an email server. Provide an account setup wizard with SMTP host/port,
TLS mode, From identity and authentication guidance. OAuth 2.0 is preferred
when supported; provider-issued app passwords may be an optional
provider-permitted fallback. Never request the user's ordinary mailbox
password for unsupported basic login. Google and Microsoft account rules
must be treated as provider-specific and subject to change.

No secret in SQL, source code, GitHub, logs, or installer; use a protected
Windows account vault and pause sending if credentials are lost or changed.
Require TLS and certificate validation; never fall back to plaintext.
Use small, explicit reviewed batches, conservative provider limits and
backoff **only where safe**. Send each guardian privately (not a visible
bulk "To" list). An SMTP acceptance means **accepted by the sending server**,
not delivered to the mailbox; only authenticated delivery/bounce evidence may
update delivery certainty. If an SMTP connection breaks after submission,
record **Unknown**, do not blindly resend.

An email client handoff (`mailto:`) may be offered as a manual fallback,
but a client being launched is not proof that any email was sent and cannot
serve as an unattended bulk mailer.

### C. Meta WhatsApp Cloud API — optional future direct mode

Meta hosts the official API; a school does not need to operate an outbound
server to submit messages. School-specific onboarding still needs a Meta
business setup, a registered sender, access tokens, approved templates where
required, usage/cost awareness and provider acceptance. Webhook-driven
delivery confirmations need a **publicly reachable trusted HTTPS ingress**.
If there is no such service, never claim verified real-time delivery.
An authenticated provider status read may be used **only if that provider
actually supports a correlatable query**. Otherwise retain an honest
submission/unknown state. Never fabricate receipts from a successful HTTP POST.

Do not silently switch a manual Business App account to Cloud API or assume
the same number can be shared across products without provider eligibility
checks. Never activate a paid provider or incur charges without an
administrator's deliberate approval and displayed cost/limit controls.

### D. SMS — optional

SMS is another channel using the same audience and consent policy, with a
provider-specific adapter and cost guard. An external SMS provider is
optional. The previously implemented Twilio adapter remains inert until real
credentials, verified callbacks/receipt reconciliation and release acceptance.

## 4. Consent, identity and destination rules

- The **existing Admission Yes/No** remains the one authority for guardian
  school communications. No duplicate consent page or preference table.
  Admission No and Not Recorded are excluded; phone/email presence does not
  infer consent. Channel-specific suppression/opt-out always overrides Yes.
- Before sending through a newly added medium, ensure that the notice shown
  at Admission **actually covered that medium**. Existing Yes values that
  preceded email disclosure must not silently become email permission:
  record a re-affirmed Yes and its disclosure version as evidence **within
  the existing Admission authority**, not as a second independent Yes/No.
  Keep the current SMS/WhatsApp scope unchanged until this is implemented.
- Emails for students' guardians are optional fields on the existing student
  record. Staff addresses are separate and require Staff permissions. No
  migration imports obsolete guardian contact emails.
- Resolve the live school record, campus/class and consent again at
  confirmation and immediately before each attempted direct send or manual
  handoff. Recheck changed numbers/emails, opt-outs and active enrollment.
- Deduplicate a shared destination for a common notice; for individual/fee
  messages never discard a sibling's separately owed fee. Display a deliberate
  household summary or distinct reviewed messages as appropriate.
- Never reassign a failed recipient to a different channel without explicit
  review. No automatic SMS/email fallback after an uncertain WhatsApp action.
  A destination change invalidates the old reviewed snapshot.

## 5. Status semantics and crash safety

A mode-aware ledger should distinguish:

- `Draft` / `Previewed` — nothing sent.
- `Prepared` / `OpenedExternally` — a WhatsApp/email client was opened,
  **not** evidence of a message being sent.
- `OperatorReportedSent` — the user explicitly reported clicking Send;
  never the same as provider-verified Accepted or Delivered.
- `Pending` / `Claimed` — queued/reserved by a reviewed direct batch.
- `Submitted` / `Accepted` — the direct provider received or acknowledged
  a request, but the destination may not have received it.
- `Delivered` — verified provider-specific proof only.
- `Failed` — definitive failure; a manual retry may be allowed.
- `Unknown` — a timeout, crash or uncorrelatable outcome; **never**
  automatically retry because it could duplicate a real message.
- `Excluded` — declined consent, missing/invalid destination, opt-out,
  inactive record, obsolete selection or insufficient authorization.

Distinct manual versus direct history evidence avoids fake delivery claims.
Every batch/recipient/attempt needs stable IDs and operator attribution;
duplicate-clicks return the same reviewed operation. Persist the state
**before** external side effects. Do not start sending automatically after
an application restart or database recovery. Provide pause, cancel remaining,
manual review/reconcile and human-readable explanations without exposing
private message bodies in system logs.

## 6. Practical school controls

- SMS/WhatsApp/Email are independently configurable and pausable. Providers
  can be changed **without deleting history**. A credential change pauses
  the corresponding channel until revalidated. Provider installation and
  provider *activation* are distinct actions.
- No automatic message when admitting a student, collecting fees, opening
  the dashboard or updating the application.
- Cost/volume caps, per-provider throughput, account quota guidance,
  per-batch and daily sending limits, separate school/fee templates,
  cancellation before the next attempt, and clear offline/connection errors.
  Never evade provider anti-spam limits.
- Permissions stay separate: Communication.View, Communication.Send,
  Communication.Configure, plus the underlying Student/Staff/Fees permissions.
  Protect individual student contact information; only show eligible
  destinations to authorized senders.
- No public inbound service or local listening port in the initial free
  desktop configuration. Future verified webhook hosting is an optional
  infrastructure component, not a required prerequisite for free manual
  WhatsApp or direct SMTP.
- Keep one working installation on a school PC with unreliable connectivity.
  Record safe pending work, never fake success, never silently retry after
  interruption, and avoid corrupting admission/fee transactions.
- Existing school data and already-applied migrations stay untouched.
  Every new schema change has a numbered migration, transactional rollback
  and verified pre-migration backup. Installer/core.ps1 remains the one
  required schema version authority.

## 7. Delivery milestones and acceptance gates

1. **Baseline / schema 10:** provider catalog, email fields, disabled profiles,
   manual-vs-direct mode contract; no UI sending and no provider activation.
2. **Free WhatsApp handoff:** safe per-recipient `wa.me`, separate audited
   manual states and persistence, explicit user confirmation, resume/skip;
   synthetic tests prove browser opening never means Sent or Delivered.
3. **School email:** Admission field + updated disclosure evidence, typed
   recipient resolution, live opt-out checks, protected SMTP/OAuth setup,
   test connection, send-once claim and evidence, failure/timeout tests,
   real consenting private mailbox acceptance.
4. **Optional Meta Cloud API:** separate provider identity, credentials,
   approved template configuration, cost cap, idempotent claim and authenticated
   reconciliation. Public callback service is required **only when verified
   webhook receipts are desired**; design it separately from the Windows UI.
5. **Release:** Windows desktop and native UI, disposable fresh/3-to-latest/
   7-to-latest/9-to-latest SQL upgrades, rollback/permissions/security,
   real-provider scenario tests, and fresh exact-source-SHA private Sandbox
   acceptance. Workflow 04 remains gated; no automatic release.

Tests should protect *behavioral contracts* (privacy, deduplication,
authorization, revalidation, uncertain-outcome handling), not freeze cosmetic
form/layout or implementation details. Changes can extend providers without
requiring a new Communication Center design. External provider policies,
security requirements, and future messaging needs may still require
additive enhancements; **no software design can guarantee zero future changes**.

## References

- [WhatsApp Help — Click to Chat](https://faq.whatsapp.com/5913398998672934/)
- [Meta — WhatsApp Business Platform developer hub](https://whatsappbusiness.com/developers/developer-hub/)
- [Google — Gmail SMTP and OAuth](https://developers.google.com/workspace/gmail/imap/imap-smtp)
- [Google — App passwords and 2-Step Verification](https://support.google.com/mail/answer/185833)

## Implementation status — draft PR #131

The free WhatsApp Business handoff is now implemented behind explicit admin
activation and consent-preserving SQL confirmation. It opens one verified
`wa.me` link per recipient, separately records operator-reported sends,
with skip/resume and permission checks. No automatic WhatsApp API submission,
verified message delivery, hosted infrastructure or server credentials exist
in this mode. Native and SQL regression tests must pass before this branch
can be considered ready; no application release has been authorized.

Direct SMTP with an optional school mailbox and official Meta API remain
**planned**, not operational; their adapters, credential onboarding, revised
email consent disclosure and delivery acceptance require subsequent stages.
