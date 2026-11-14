# Family Admission & Fees — quick counter

An optional start-of-year counter workflow. It coordinates existing registration,
return confirmation, fee review, collection and RDLC documents; it does not replace
full admission, student corrections or the normal finance screens.

Family details and the children list stack in the left 40% of the workspace. The
selected child's details and fees occupy the right 60%, spanning both left cards.
The family fee overview combines frequency with the fee name, concession with the
discount, and effective dates, status and last posting in one period column. Raw
amounts and assignment identities are retained for the established fee actions;
other fee screens keep their detailed columns. Fee actions wrap at smaller widths.

## One counter visit

1. Find any child to load the family, or choose **New Family**. Check only the
   children being handled in this visit. Selecting a row opens its editor;
   checking a row includes it in preparation, billing and payment.
2. Add new siblings with **Add New Child**. Shared parent details carry forward.
   Enter each child's name, placement, joining date, fees and concessions. Each
   draft stays in place when switching children. Existing families' shared details
   are read-only here; detailed corrections use the existing admission screens.
3. Use **Save & Prepare Selected**. New children remain pending registrations;
   official admission numbers and full admission come later. Promoted children
   use the existing reviewed return confirmation. Existing ready children need no
   admission write. Fee-term changes retain their established explicit reviews.
4. Use **Collect Payment / Receipt**. The established Fee Collection screen opens
   over this workspace with exactly the selected children. Enter the amount
   received, review or change the suggested per-child Pay amounts, confirm and
   print the existing receipt. Close it to return to the retained family.
5. If payment will happen later, use **Print Selected Bill** instead. A bill is
   neither payment nor full admission. Fully waived plans remain visible, including
   monthly concessions beginning in a future month, without creating money or
   artificial charges. A child with no dues does not need a zero-value payment.

## Progress and recovery

Preparation saves children individually. If a child fails, earlier successful
children stay saved; the status identifies how many completed and where to resume.
Correct the remaining child and retry. Already saved registrations are not recreated.
New drafts and excluded children remain in the workspace. Refresh retains drafts
and invalidates members whose family membership can no longer be verified.

**Check Previous Save** opens a separate recovery editor. An unrelated previous
request cannot replace a loaded child's identity. Unknown outcomes must be checked
before starting more registrations or payments. The established collection recovery
notice, transaction boundary and saved receipt history remain authoritative for an
interrupted payment. Never repeat a payment just because a refresh or print failed.

Collection retains both children's remaining bills after a partial payment and
clears the submitted amounts. Closing with unrecorded payment entries asks whether
to discard them. Recorded money and receipt snapshots remain saved.

## Delivery and verification

The required schema comes from `Installer/core.ps1`. Apply supported versioned
migrations through the existing verified backup/transaction deployment process.

Focused native checks: `ThemeSmokeChecks --family-admission-workspace`.
Owned SQL/native/RDLC checks: `Tools/Run-Level2.ps1 -FamilyCounterOnly`.
The latter creates fictional data in a unique owned instance and isolated profile;
it does not accept a live database. Actual operator speed/comprehension and physical
printer acceptance must be observed separately from automated tests.
