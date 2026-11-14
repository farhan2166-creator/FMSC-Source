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
        total = sum(len(entries) for entries in results.values())
        headline = (
            f"UI copy-dependency audit (advisory): {total} candidates "
            f"in {len(results)} test files. Exact copy is allowed for "
            "mandatory notices, money, identities and safety semantics."
        )
        print(headline)
        sample = [
            f"- {name}:{line}: {expr[:125]}"
            for name, entries in results.items()
            for line, expr in entries
        ][:12]
        if sample:
            print("\n".join(sample))
        summary_path = os.environ.get("GITHUB_STEP_SUMMARY")
        if summary_path:
            with open(summary_path, "a", encoding="utf-8") as summary:
                summary.write("### UI wording-coupling review — advisory only\n\n")
                summary.write(headline + "\n\n")
                summary.write("\n".join(sample) + "\n\n")
        return 0
    except Exception as error:
        # An advisory must not become another historical release blocker.
        print(f"UI copy-dependency audit unavailable (non-blocking): {error}")
        return 0


if __name__ == "__main__":
    raise SystemExit(main())
