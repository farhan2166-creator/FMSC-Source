# Help & FAQ Center

Open **Help & FAQ** from the account menu. It works offline, needs no school database connection or additional action permission, and never grants access to the workflows it describes. The catalog has **111 questions in 14 topics**. Seventy of the 77 original questions remain; seven original Communication Center questions and eight later communication-only questions were removed with the unused module. Overlapping questions about departure and history, or partial collection and remaining balance, answer different operator concerns and are retained.

Search using ordinary words such as `unpaid fees`, `pending siblings`, `payment refresh`, `correction unknown`, `stop transport` or `photo zoom`. All meaningful search terms must match. Titles rank above keywords, workspace/rule labels and answer-only matches. Case, punctuation and explicit spelling aliases are normalized; there is no approximate substring or online AI search. An exact question ranks first. Explicit term groups cover dues/unpaid/balance, admission spellings, enrollment/enrolment, parent/guardian, family/sibling, payment/collection, cancellation spellings, database/LocalDB/SQL and more. Context-sensitive concepts such as transfer vs departure, and refund vs cancellation, remain distinct.

Combine **Topic** and **Rule type** filters with search. The count describes matching questions, not student records. A no-result state suggests fewer terms or clearing the two filters. Ctrl+F focuses and selects the search text. Arrow keys select a result; Enter moves to its answer. Related questions open on Enter or double-click, clear the current search/filters and select their stable destination. No related link executes an application operation.

The answer includes its question, plain-language rule labels, explanation, any important caution and **Where to go**. Everything stays in a scrollable reading area, avoiding fixed-height question clipping. The results move above the answer on narrower effective client widths. Existing shared theme fonts, colors and DPI helpers are retained. Related results also scroll; they never consume the whole answer area.

## Rule labels and technical ownership

An entry can carry more than one label:

| Label | Meaning |
|---|---|
| System rule | Protected behavior; an Administrator still cannot bypass data-integrity rules through ordinary actions. |
| School setting | A supported authorized editor can change the documented policy or setup value. |
| User choice | An operator selects among valid alternatives. |
| Permission required | The action requires the relevant access in addition to valid data. |
| Recovery guidance | Determine the recorded outcome or use the supported recovery path before retrying. |

Maintainer-only ownership records SQL, C#, configurable data and external provider/environment dependencies. It is separate from operator labels. Source references and related IDs live with each entry. See the [coverage matrix](FAQ_COVERAGE_MATRIX.md) for every question and the [retained historical audit evidence](../Audit/REVIEW_HISTORY.md#help--faq-source-audit--2026-10-16) for corrections and unresolved differences.

## Structure and maintenance

- `Pages/Help/HelpCatalog.cs`: model, rule labels, precomputed search index, fixed synonym groups, filters, ranking and related lookup. The existing two-argument `Find` API remains supported, with an optional rule filter.
- `HelpCatalog.Basics.cs`, `.Students.cs`, `.Finance.cs`: retained module content. The retired communication-only catalog is absent. One shared catalog/search API; no SQL-backed help store or new runtime dependency.
- `Pages/Help/HelpCenterPage.cs`: existing dashboard page enhanced with rule filtering, keyboard access, related questions and responsive themed reading.
- `Tests/HelpChecks`: portable production-catalog checks without WinForms, database or network access.
- `Tests/ThemeSmokeChecks/Program.HelpCenter.cs`: Windows control contracts, integrated into the existing suite and available independently with `--help-center`.

To change an answer:

1. Inspect the current screen, writer, SQL procedure/trigger/constraint and permission code. Do not treat historical audit prose as an executable rule.
2. Preserve stable IDs for retained questions; the retired communication-specific IDs are intentionally unavailable. Append new IDs; never derive them from mutable titles.
3. Explain the supported next action, relevant authorization and history/balance consequences. Add a caution for uncertain outcomes or financial effects. Avoid procedure names in the operator answer.
4. Attach precise existing source paths, with method/type/object symbols where practical. Record all relevant ownership layers. Mark genuine configuration only when an authorized editor exists.
5. Link a few useful, distinct follow-up questions by ID. No self-links, duplicates, broken references or cycles.
6. Update the coverage matrix and source audit when a rule or evidence boundary changes. A source reference resolving is necessary, but is not proof that the prose is correct.
7. Run the checks below and review the answer in Windows. Do not edit SQL, permissions or approved database assets to match an answer.

## Verification commands

From the repository root, using .NET 8:

```powershell
dotnet run --project Tests/HelpChecks/HelpChecks.csproj -c Release
dotnet run --project Tests/AuditFixChecks/AuditFixChecks.csproj -c Release
dotnet build FMSC_SMS.sln -c Release
dotnet run --project Tests/ThemeSmokeChecks/ThemeSmokeChecks.csproj -c Release -- --help-center
```

Linux compilation of the solution/Windows control project requires `-p:EnableWindowsTargeting=true`. The Windows control executable cannot run on Linux. The portable suite validates unique IDs/questions, required metadata, source paths/symbols, complete related graphs, real searches, ranking, combined filters, no results and offline dependencies. It also provides a bounded search timing smoke check, not a production performance certification.

The existing Desktop CI runs the portable Help checks and includes Help control checks in its focused post-fix suite; no installer/release workflow or existing check is removed.

The Windows control suite needs a disposable Windows profile and no database. It checks construction while signed out, search, empty state, related Enter behavior, theme changes, compact sizes and disposal. It is a control-contract suite, not real operator desktop acceptance.

## Windows acceptance still required

1. Open Help while signed in as Administrator and as a restricted user; compare the same offline catalog. Confirm protected destination access is unchanged.
2. Test keyboard Tab/Shift+Tab, Ctrl+F, result arrows, Enter into the answer, answer scrolling and related Enter/double-click. Check screen-reader names and reading order with Windows Narrator.
3. Use 1280×720, 1366×768, 1920×1080 and larger working areas at 100%, 125%, 150% and 200% scaling where available. Verify all filters, readable answer text, horizontal/vertical split and scrolling. Move between monitors with different DPI.
4. Switch regular and lightweight themes with an answer selected. Confirm contrast, selection and filters remain intact. Read the longest answer and a warning without clipping.
5. Ask an operator to resolve pending sibling registration, a payment with unknown outcome, Hold promotion, transport stopping and an older restore. Confirm the actual supported destination and permission.

No real school data is needed. These checks remain pending until actually performed. GitHub hosted checks are not reported green while the owner's Actions allowance is exhausted.

Windows follow-up: see [16 October verification](WINDOWS_VERIFICATION_2026-10-16.md) for the reproduced splitter defect, focused fix, native checks and remaining interactive limitations.
