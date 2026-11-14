# Behavioral UI test contracts — prevent obsolete copy from blocking releases

## Rule

For the current application, a UI regression test must normally verify **what
the user can accomplish**, not the exact words printed on a button. Captions,
routine guidance, card placement, historical column labels, window titles and
cosmetic layout are allowed to evolve without breaking a release when the
current behavior is correct. This applies to tests in
`Tests/ThemeSmokeChecks`, acceptance scripts and new features.

The 14 November 2026 Admission example illustrates this: the pending sibling
counter correctly displayed **Pending: 1**, but an active test expected the
old phrase **Pending siblings: 1**. It had become an accidental release blocker.
The replacement test still verifies **one waiting sibling** (excluding the
loaded child), regardless of the caption.

## Test the correct contract

| Test category | Assert | Avoid |
| --- | --- | --- |
| Actions | Correct control is available; correct record ID and operation; cancellation / duplicate-click safety | Exact button text solely to locate the control |
| Admission and enrollment | Selected student, pending state, registration counter, permissions, confirmed writes | Frozen "Admit", "Pending", "Save" captions |
| Finances | Exact amounts, totals, discounts, allocations, receipts, payer scope, read/write permission | Exact fee-card titles and ordinary instructional paragraphs |
| Communication | Consent and opt-outs, eligible recipient set, idempotency, current phone, manual vs verified sent status | Wizard step counts, button wording, provider placeholder names |
| UI accessibility | No clipping, keyboard reachability, discoverable required actions, readable safety instructions | Fixed panel coordinates, hard-coded row numbers unless essential |
| Publication / recovery | Exact-source SHA, verified backups, compatibility and rollback checks | Old unrelated historical executable hashes, frozen versions |

### Exceptions that intentionally require text assertions

Exact text remains a valid **release-blocking** contract only when the text
itself is the functional requirement: legally mandated disclosures, explicit
consent notices, financial values or identities printed on official artifacts,
unambiguous dangerous-operation warnings, security-critical recovery guidance,
or owner-approved terminology where a mistaken meaning could cause harm.
Record the reason near the test. A test looking for a meaningful "not
delivered" distinction must remain strict about that distinction, even if
the formatting or surrounding sentence changes.

A renamed button must never be allowed to route to a different operation.
Tests should assert the underlying selected record, operation, permissions and
result. If the test can only locate an action by its visible caption, introduce
a stable semantic control identifier or use the dialog's `AcceptButton` /
`CancelButton` role when appropriate; do **not** remove the test entirely.

## Workflow when a UI test fails

1. Confirm the **actual application behavior** on the tested code, not a
   historical screenshot, fixture caption or release note.
2. Separate functional defects from presentation-only differences.
3. Correct an obsolete test by asserting the domain outcome instead of
   merely replacing one hard-coded string with another.
4. Preserve strict negative-path/security tests and real Windows rendering
   checks; do not blanket-skip the suite or mark a safety failure optional.
5. Rerun all affected current checks and the exact-commit Sandbox gate before
   any publication.

`Tools/audit_ui_test_copy.py` provides **advisory** inventory of candidates
for this cleanup. It does not fail builds, because some exact text matches are
legitimate data or compliance contracts. Treat its output as review leads,
not automatic permission to weaken an assertion.
