# 09 — Testing and evidence

Test procedures, results and Level 2/3 evidence are distinct records. Exact duplicate captures can represent different scenario IDs; preserve them. Do not treat simulated or focused checks as end-to-end native acceptance.

## Full records and their purposes

1. [LEVEL1 TESTING](LEVEL1_TESTING.md)
2. [LEVEL2 TESTING](LEVEL2_TESTING.md)
3. [LEVEL2 RESULTS](LEVEL2_RESULTS.md)
4. [summary](Level2Evidence/2026-10-16/summary.md)
5. [LEVEL3 TESTING](LEVEL3_TESTING.md)
6. [LEVEL3 RESULTS](LEVEL3_RESULTS.md)
7. [LEVEL3 LOCAL TEST PLAN](LEVEL3_LOCAL_TEST_PLAN.md)
8. [README](Level3Evidence/2026-10-16/README.md)
9. [FAQ COVERAGE MATRIX](FAQ_COVERAGE_MATRIX.md)
10. [WINDOWS VERIFICATION 2026 10 16](WINDOWS_VERIFICATION_2026-10-16.md)

## Retention and use

This is a numbered, consolidated subject document, not a copy of the source reports. Read the linked records for details and original evidence. Existing filenames, decisions and audits remain unchanged; this indexing work does not change source code, SQL or test status.

## Living regression contracts

Automated release gates protect user-visible behavior and data safety, not a fixed
implementation. A form, report, or database schema may evolve without being
required to retain private field names, exact panel proportions, dialog dimensions,
decorative colors, control order, or previously generated screenshots.

- **Mandatory safety contracts** remain strict: migration and historical fixture
  upgrade paths, consent and authorization, financial correctness, data integrity,
  installer/update behavior, and publication acceptance. Never bypass these checks.
- **UI and report checks** should assert user outcomes: actions can be found and
  used, relevant information is readable, data and calculated values are accurate,
  content is not clipped, and printing/exporting succeeds. Avoid prescribing one
  layout or internal implementation where several valid designs exist.
- **Golden images and visual snapshots** are review evidence, not release-blocking
  design specifications unless an explicit approved requirement needs pixel-perfect
  output. Keep visual reviews separate from behavior-oriented acceptance.
- **Changed requirements** require a deliberate test-contract review. Explain
  which requirement changed, keep or replace substantive coverage, and distinguish
  an outdated assertion from a genuine regression. Never mark a failure as
  harmless merely because the code change was intentional.
- **Database compatibility** is evaluated against production-facing contracts and
  supported upgrade paths. Immutable schema-3/schema-7 Sandbox MDF/LDF fixtures
  are historical inputs, not a requirement to preserve retired database objects.
  Add new migrations; do not edit the historical fixtures or immutable migrations.

Consolidate related edits on a review branch and run relevant checks once for the
finished candidate. Workflow 04 remains fail-closed on exact-commit private
Sandbox acceptance; test modernization does not authorize publishing.
