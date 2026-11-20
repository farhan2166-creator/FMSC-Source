"""Advisory inventory of UI tests coupled to presentation-only string literals.

This is NOT a release gate. A literal can be a genuine contract (e.g. a
consent notice or a receipt amount), so human review is necessary.
"""
from __future__ import annotations

import os
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
TESTS = ROOT / "Tests" / "ThemeSmokeChecks"
COPY_ASSERTION = re.compile(
    r'\.(?:Text|HeaderText|AccessibleName)\s*(?:==|!=)\s*"[^"\n]+"'
    r'|\.(?:Text|HeaderText)\s*\.\s*(?:Contains|StartsWith|EndsWith)\s*\(\s*"[^"\n]+'
)


def candidates(source: str) -> list[tuple[int, str]]:
    """Return line-numbered candidates, not a judgment that a test is wrong."""
    return [
        (line_number, line.strip())
        for line_number, line in enumerate(source.splitlines(), 1)
        if COPY_ASSERTION.search(line) and not line.lstrip().startswith("//")
    ]


# Presentation-only geometry is reviewed, never used as a release gate.
# These patterns are intentionally conservative: many matches are valid
# mathematical/DPI/printing contracts, so automated deletion would be unsafe.
GEOMETRY_ASSERTION = re.compile(
    r'\b(?:ClientSize\.)?(?:Width|Height|Top|Left|Right|Bottom|RowCount|ColumnCount)'
    r'\s*(?:==|!=)\s*\d+'
    r'|\b(?:RowStyles|ColumnStyles)\s*\[\s*\d+\s*\]\s*\.\s*(?:Height|Width)'
    r'|\b(?:GetRow|GetColumn|GetRowSpan|GetColumnSpan|GetChildIndex)\s*\([^;\n]*\)'
    r'\s*(?:==|!=)\s*\d+'
    r'|\bClientSize\.(?:Width|Height)\s*(?:<=|>=)\s*\d+'
)


def geometry_candidates(source: str) -> list[tuple[int, str]]:
    """Flag suspicious UI test geometry for human review, without failing CI."""
    return [
        (number, line.strip())
        for number, line in enumerate(source.splitlines(), 1)
        if GEOMETRY_ASSERTION.search(line) and not line.lstrip().startswith("//")
    ]


def audit(root: Path = TESTS) -> dict[str, list[tuple[int, str]]]:
    result: dict[str, list[tuple[int, str]]] = {}
    for source in sorted(root.glob("Program*.cs")):
        found = candidates(source.read_text(encoding="utf-8-sig"))
        if found:
            result[source.name] = found
    return result


def main() -> int:
    try:
        results = audit()
        geometry = {
            name: found
            for name, found in (
                (source.name, geometry_candidates(source.read_text(encoding="utf-8-sig")))
                for source in sorted(TESTS.glob("Program*.cs"))
            )
            if found
        }
        total = sum(len(entries) for entries in results.values())
        geometry_total = sum(len(entries) for entries in geometry.values())
        headline = (
            f"UI presentation audit (advisory, never blocking): "
            f"{total} copy and {geometry_total} geometry candidates across "
            f"{len(set(results) | set(geometry))} test files. "
            "Exact values may be legitimate DPI, printing, money, identity or safety contracts."
        )
        print(headline)
        sample = [
            f"- copy {name}:{line}: {expr[:125]}"
            for name, entries in results.items()
            for line, expr in entries
        ][:6] + [
            f"- geometry {name}:{line}: {expr[:125]}"
            for name, entries in geometry.items()
            for line, expr in entries
        ][:6]
        if sample:
            print("\n".join(sample))
        summary_path = os.environ.get("GITHUB_STEP_SUMMARY")
        if summary_path:
            with open(summary_path, "a", encoding="utf-8") as summary:
                summary.write("### UI presentation-coupling review — advisory only\n\n")
                summary.write(headline + "\n\n")
                summary.write("\n".join(sample) + "\n\n")
        return 0
    except Exception as error:
        # An advisory must not become another historical release blocker.
        print(f"UI copy-dependency audit unavailable (non-blocking): {error}")
        return 0


if __name__ == "__main__":
    raise SystemExit(main())
