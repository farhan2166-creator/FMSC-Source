# Free WhatsApp Business — Release acceptance checklist

This checklist applies only to the manually operated `WhatsAppBusinessManual`
delivery method on PR #131. An external WhatsApp account or app is **not**
required for synthetic tests. These instructions do not authorize sending
messages to real guardians during QA.

## Functional behavior

- [ ] Installer upgrade from 9 to 10 adds optional email columns and the
  `msg.ManualBatch` / `msg.ManualHandoff` ledger without losing student,
  staff, attendance, billing or admission consent records.
- [ ] `Use Free WhatsApp` is absent for nonadministrators. The administrator
  can opt in, and `Pause WhatsApp` stops new handoff openings.
- [ ] Select school, campus, class, outstanding-fee, one student, family or
  one staff member with the established FMSC selectors. There is never a
  requirement to click/select every individual child for bulk audiences.
- [ ] Admission Yes is mandatory for guardians; No and Not Recorded remain
  excluded. A changed guardian number, withdrawn Yes or WhatsApp opt-out
  prevents opening a previously reviewed chat.
- [ ] Preview shows masked destinations, eligible/excluded counts and the
  complete reviewed message. Confirm writes an idempotent batch only after
  re-checking all eligible school records.
- [ ] The native handoff dialog opens exactly one `wa.me` chat containing the
  reviewed destination and URL-encoded text. Nothing is sent automatically.
  The user must verify their logged-in school account and press Send.
- [ ] `I Sent It` requires a prior opened chat and is explicitly
  **operator-reported**, never delivered. `Skip` does not send. Both choices
  survive application restart and can be viewed in History.
- [ ] Close and reopen a partially completed session from History. Re-opening
  a Pending/Opened chat never silently marks it sent; ReportedSent/Skipped/
  Excluded cannot be opened again as new sends without an explicit new batch.
- [ ] History displays the mode and operator-report counts separately from
  provider Delivered; no manual action adds a row to `msg.Attempt` or
  `msg.Receipt`.
- [ ] Direct MetaCloud, SMTP, SMS/Twilio and Email handoff profiles remain
  unavailable for live sends. No background sender, auto-retry, automation,
  paid provider or public webhook is installed.

## Failure and security acceptance

- [ ] An unauthorized account cannot enable manual WhatsApp, confirm a
  batch, read its recipients, open a chat or report a send.
- [ ] Database trigger refuses attempts attributed to manual providers even
  if `msg.ClaimRecipient` is invoked directly.
- [ ] Closing the FMSC app, interruption of Windows/browser launch, network
  loss or switching FMSC accounts cannot produce a fabricated Delivered or
  Accepted receipt; an Opened chat remains resumable.
- [ ] Email/message URLs, guardian phone numbers and message body never
  appear in telemetry, public CI logs or GitHub artifacts.
- [ ] No live school database, detached MDF/LDF or public-repository credential
  is used in QA. Use a disposable test copy and synthetic contacts only.
- [ ] Exact-commit Windows build, focused UI tests, disposable SQL migrations,
  older-schema upgrades, rollback and private Sandbox release acceptance
  are all reviewed before merging or publishing.

## Limitations acknowledged by the operator

Free WhatsApp Business is a **manual click-to-chat** mechanism. WhatsApp's
own interface is where the operator sends. FMSC cannot infer which Business
account the browser uses, prove that Send was clicked, or verify delivery.
For very large school-wide sends, individual handoff is slow. An optional
official Cloud API can later replace the handoff under the same school
audience/consent workflow after separate provider acceptance and pricing
approval. Neither SMTP nor Meta Cloud API is yet enabled in this PR.
