# Free WhatsApp Business — Release acceptance checklist

This checklist applies only to the manually operated `WhatsAppBusinessManual`
delivery method. An external WhatsApp account or app is **not**
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
- [ ] One reviewed individual opens the first chat without a redundant bulk
  prompt. A bulk preview requires one explicit confirmation, then opens only
  its first eligible chat. Reconfirming an already saved batch never silently
  reopens a chat.
- [ ] An explicit **I Sent It · Next** or **Skip · Next** action records
  only the current recipient and opens the next after a fresh eligibility
  check. The next recipient remains Pending/Opened, never ReportedSent.
  Network or shell failures do not fabricate progress.
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

## WinForms visibility and local acceptance (do not send real messages)

Acceptance uses the actual installed executable and a disposable synthetic
school database. Tests assert **behavior and reachable controls**, not exact
captions, a fixed button count, historical version numbers or cosmetic card
positions. See the release-provenance checks under
[installed-release acceptance](../Audit/Reviews/FMSC_RELEASE_INSTALLED_ACCEPTANCE_2026-11-08.md).

1. Confirm the executable is from the intended exact commit, then open
   Communication Center with sender permission and verify the message composer
   is directly usable; History and Settings remain permission-gated.
2. With authorized test recipients, choose campus/class without individually
   selecting students, select a quick message or write one, and review. Check
   complete per-recipient text, masked contacts and accessible exclusion reasons.
3. Edit the message or change a recipient after review. The old confirmation
   must become invalid, and a new preview must be required.
4. For one reviewed individual, continue directly into one unsent WhatsApp
   chat. For a reviewed group, cancel the bulk prompt: no batch or chat may
   appear. Confirm on the second attempt and check that exactly the first chat
   opens without another button press.
5. Explicitly report the first synthetic chat as sent. Check that the **next**
   chat opens but is not marked sent or delivered. Skip another recipient and
   confirm progress and history counts. Finish Later and resume without
   duplicates or automatic re-opening of uncertain chats.
6. Exercise unavailable provider, missing permission, withdrawn Admission Yes,
   invalid or changed number, opt-out, inactive school membership, paused
   mode, failed browser launch and interrupted/recovered session. Every case
   must fail closed without fabricated sent/delivered evidence.
7. Check form usability at ordinary and increased Windows display scaling and
   smaller window heights: every essential action must remain reachable without
   clipped controls. Never use a production WhatsApp account or live contacts.
8. Publish only after the current exact-commit Windows behavioral checks,
   disposable SQL suite, security checks and private Sandbox acceptance pass.
