## What changed and why?

<!-- Explain the problem, the affected screen/workflow, the before/after behavior, and why this is the smallest safe change. -->

## Scope and safety

- [ ] Changes are focused and unrelated refactors are excluded.
- [ ] Existing student records, paid charges, whole-rupee rules, permissions, transaction outcomes and recovery semantics are preserved.
- [ ] Busy/unsaved-edit/navigation behavior remains correct where relevant.
- [ ] No school data, backup files, passwords or sensitive logs are included.
- [ ] No schema or migration changes, **or** the migration/version impact and safe rollback plan are explained below.
- [ ] No installer change, **or** full publishing and applicable disposable install checks are documented below.

**Database/schema/permission implications:**  
**Installer and backup/rollback implications:**  

## Tests and actual evidence

<!-- Mark only tests actually RUN, not merely compiled. Add results and links. -->

- [ ] Application Release build
- [ ] Windows-focused WinForms control tests (if applicable)
- [ ] Portable regression tests
- [ ] Performance/state checks
- [ ] DACPAC/schema provenance checks
- [ ] Disposable SQL integration/rollback tests (if applicable)
- [ ] Manual Windows GUI/printer/operator verification (if applicable)

**Exact commands, results, source commit and CI run URL:**  

**What remains untested / risks:**  

## Documentation and change history

- [ ] `CHANGELOG.md` updated for behavior or operational changes, or reason omitted is stated.
- [ ] Operator / installer / backup / architecture documentation updated as needed.
- [ ] Findings and remaining verification limits are recorded in this PR with actual evidence.

## Reviewer checklist

<!-- Do not merge solely because code builds. Check permissions, data integrity, failure/retry semantics and intended Windows user before production release. -->

**How to reproduce / manually verify the new behavior:**  
**Related issue(s):**  

> A merged PR or passing CI is **not** school-deployment sign-off. Verify actual school-PC installation, recovery and operator workflows before deployment.
