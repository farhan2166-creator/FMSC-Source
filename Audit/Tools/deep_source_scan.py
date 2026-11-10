#!/usr/bin/env python3
"""Read-only, repository-wide FMSC source/schema/UI inventory.

Run on a synthetic CI checkout. Scans C# source and the RECORD-FREE public
baseline DACPAC, not the private school MDF/LDF or root fmsc.dacpac.
All matches are triage candidates: this is NOT a compiler, SQL engine,
runtime test or proof of interactive operator behavior.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import xml.etree.ElementTree as ET
import zipfile
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TARGET = "Tests/LifecycleSqlChecks/Baseline/schema.dacpac"
PRODUCTION_ROOTS = ("Core/", "Pages/", "Services/", "DatabaseHelper.cs", "MainDashboardForm.cs", "Program.cs")
PATTERNS = {
    "async_mutex_candidate": re.compile(r"\bWaitOne\s*\([\s\S]*?\bawait\b[\s\S]*?\bReleaseMutex\s*\(", re.I),
    "empty_catch": re.compile(r"\bcatch\s*(?:\([^)]*\))?\s*\{\s*\}"),
    "interpolated_sql_command": re.compile(r"(?:new\s+SqlCommand|QuerySql(?:Async)?|ExecuteSql)\s*\(\s*\$[\"@]", re.I),
    "sync_async_wait": re.compile(r"\.GetAwaiter\s*\(\s*\)\s*\.GetResult\s*\(\s*\)|\.Wait\s*\(\s*\)"),
    "disable_certificate_busy_guard_candidate": re.compile(r"creatingCertificate[\s\S]{0,1600}ApplyWorkflowState", re.I),
}
PROC_DEF = re.compile(r"\b(?:CREATE\s+(?:OR\s+ALTER\s+)?|ALTER\s+)(?:PROC|PROCEDURE)\s+(?:\[?([a-zA-Z_][\w]*)\]?\s*\.\s*)?\[?([A-Za-z_][\w]*)\]?", re.I)
CALL = re.compile(r"\b(?:DatabaseHelper\.)?(?:Query(?:User)?(?:Async)?|Execute(?:User)?|Scalar(?:User)?|QueryMultiple(?:User)?)\s*\(\s*\"([A-Za-z_][A-Za-z0-9_.]*)\"", re.I)
COMMAND = re.compile(r"\bnew\s+SqlCommand\s*\(\s*\"(?:dbo\.)?([A-Za-z_][A-Za-z0-9_]*)\"", re.I)
UI_CLASS = re.compile(r"\b(?:class)\s+([A-Za-z_][\w]*)\s*:\s*([^\{\r\n]+)")
UI_ROOT = re.compile(r"\b(?:Form|V2Page|MasterPage|ApplicationWindowForm|UserControl|WorkspacePage)\b")
NAMESPACE_ELEMENT = "Element"


def line_of(text: str, offset: int) -> int:
    return text.count("\n", 0, offset) + 1


def repo_files(pattern: str) -> list[Path]:
    return sorted(p for p in ROOT.rglob(pattern) if ".git" not in p.parts and "bin" not in p.parts and "obj" not in p.parts)


def collect_cs() -> tuple[list[dict], list[dict], dict[str, str], list[dict]]:
    entries: list[dict] = []
    findings: list[dict] = []
    production: dict[str, str] = {}
    ui: list[dict] = []
    for path in repo_files("*.cs"):
        rel = path.relative_to(ROOT).as_posix()
        raw = path.read_bytes()
        content = raw.decode("utf-8-sig")
        is_prod = rel.startswith(PRODUCTION_ROOTS)
        if is_prod:
            production[rel] = content
        flags = []
        for key, pat in PATTERNS.items():
            for m in list(pat.finditer(content))[:5]:
                flags.append(key)
                findings.append({"kind": key, "path": rel, "line": line_of(content, m.start()),
                                 "excerpt": " ".join(content[m.start():m.start()+95].split())})
        entries.append({"path": rel, "bytes": len(raw), "lines": content.count("\n") + 1,
                        "sha256": hashlib.sha256(raw).hexdigest(),
                        "production": is_prod, "flags": sorted(set(flags))})
        if rel.startswith("Pages/"):
            for m in UI_CLASS.finditer(content):
                cls, base = m.groups()
                if UI_ROOT.search(base):
                    ui.append({"path": rel, "class": cls, "base": base.strip(),
                               "line": line_of(content, m.start()), "clickHandlers": content.count(".Click +="),
                               "actions": content.count("AddAction(") + content.count("AddBusyAction("),
                               "formClosingGuard": "FormClosing" in content,
                               "busyIndicator": any(k in content for k in ("IsBusy", "saveBusy", "isBusy", "creating"))})
    return entries, findings, production, ui


def local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def collect_sql() -> tuple[list[dict], list[dict], list[str]]:
    baseline = ROOT / TARGET
    if not baseline.is_file():
        raise FileNotFoundError("Required record-free test DACPAC missing: " + TARGET)
    procedures: list[dict] = []
    with zipfile.ZipFile(baseline) as archive:
        models = [n for n in archive.namelist() if n.lower().endswith("model.xml")]
        if len(models) != 1:
            raise ValueError("Expected exactly one schema model in record-free DACPAC")
        model = ET.fromstring(archive.read(models[0]))
        for element in model.iter():
            if local_name(element.tag) != NAMESPACE_ELEMENT:
                continue
            typ = element.attrib.get("Type", "")
            if "SqlProcedure" not in typ:
                continue
            name = element.attrib.get("Name", "")
            if not name:
                continue
            scripts = []
            for prop in element.iter():
                if local_name(prop.tag) == "Property" and prop.attrib.get("Name") in ("BodyScript", "Script"):
                    scripts.append("".join(prop.itertext()))
            body = "\n".join(scripts)
            procedures.append({"source": TARGET, "name": name, "type": typ,
                               "bodyAvailable": bool(body.strip()),
                               "bodyBytes": len(body.encode("utf8")),
                               "writes": bool(re.search(r"\b(INSERT|UPDATE|DELETE|MERGE|TRUNCATE)\b", body, re.I)),
                               "requiresPermission": bool(re.search(r"\bRequire(?:Any)?Permission\b", body, re.I))})
    migration_statements = []
    for path in sorted((ROOT / "Migrations").glob("*.sql"), key=lambda x: int(x.stem) if x.stem.isdigit() else 99999):
        rel = path.relative_to(ROOT).as_posix()
        body = path.read_text(encoding="utf-8-sig")
        defs = [{"schema": m.group(1) or "dbo", "name": m.group(2), "line": line_of(body, m.start())}
                for m in PROC_DEF.finditer(body)]
        migration_statements.append({"path": rel, "bytes": len(body.encode("utf8")), "procedureDefinitions": defs,
                                     "hasSchemaChange": bool(re.search(r"\b(ALTER TABLE|CREATE TABLE|DROP TABLE)\b", body, re.I))})
    return procedures, migration_statements, [p.name for p in repo_files("*.sql")]


def cross_reference(production: dict[str, str], procedures: list[dict], migrations: list[dict]) -> list[dict]:
    known = {re.sub(r"[^a-z0-9_]", "", x["name"].split(".")[-1].lower()) for x in procedures}
    known.update(x["name"].lower() for migration in migrations for x in migration["procedureDefinitions"])
    unknown = []
    for path, text in production.items():
        for pattern in (CALL,):
            for match in pattern.finditer(text):
                called = match.group(1).split(".")[-1].lower()
                if called not in known:
                    unknown.append({"path": path, "line": line_of(text, match.start()),
                                    "procedure": match.group(1),
                                    "note": "Not present in record-free schema-1 baseline or simple migration parser; manual resolution needed"})
    return unknown


def write_report(output: Path, data: dict) -> None:
    output.mkdir(parents=True, exist_ok=True)
    (output / "deep-source-inventory.json").write_text(json.dumps(data, indent=2, sort_keys=True), encoding="utf8")
    summary = data["summary"]
    lines = [
        "# FMSC read-only static coverage inventory", "",
        "> This is an automatically generated inventory, NOT an exhaustive semantic audit or Windows UI certification.", "",
        f"- C# files read: {summary['csRead']}",
        f"- Production C# files: {summary['productionCs']}",
        f"- Page/Form class candidates: {summary['uiClasses']}",
        f"- Record-free schema-1 DACPAC procedures indexed: {summary['baselineProcedures']}",
        f"- Baseline procedures with accessible body scripts: {summary['baselineProcedureBodies']}",
        f"- Migration SQL files indexed: {summary['migrationFiles']}",
        f"- Procedure CREATE/ALTER statements in migrations: {summary['migrationProcedureDefinitions']}",
        f"- Static risk matches: {summary['staticMatches']}",
        f"- Literal procedure calls unresolved by baseline/migration-name index: {summary['unresolvedProcedureCalls']}", "",
        "## Static code patterns — human review required", ""
    ]
    for entry in data["patterns"][:100]:
        lines.append("- {path}:{line} [{kind}] {excerpt}".format(**entry))
    lines += ["", "## SQL procedure call candidates requiring independent resolution", ""]
    for entry in data["unresolvedCalls"][:100]:
        lines.append("- {path}:{line} — {procedure}".format(**entry))
    lines += ["", "## UI classes and superficial action coverage", ""]
    for ui in data["uiClasses"][:150]:
        lines.append("- {path}:{line} {class} (click={clickHandlers}, actions={actions}, closingGuard={formClosingGuard}, busy={busyIndicator})".format(**ui))
    (output / "deep-source-summary.md").write_text("\n".join(lines) + "\n", encoding="utf8")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--out", type=Path, default=ROOT / "artifacts" / "deep-source-audit")
    args = parser.parse_args()
    cs, patterns, prod, ui = collect_cs()
    procs, migrations, sql_files = collect_sql()
    unresolved = cross_reference(prod, procs, migrations)
    result = {
        "purpose": "static source/SQL/UI inventory, not runtime certification",
        "sourceHead": "record separately from git checkout",
        "summary": {"csRead": len(cs), "productionCs": sum(1 for x in cs if x["production"]),
                    "uiClasses": len(ui), "baselineProcedures": len(procs),
                    "baselineProcedureBodies": sum(1 for p in procs if p["bodyAvailable"]),
                    "migrationFiles": len(migrations),
                    "migrationProcedureDefinitions": sum(len(m["procedureDefinitions"]) for m in migrations),
                    "sqlFiles": len(sql_files), "staticMatches": len(patterns),
                    "unresolvedProcedureCalls": len(unresolved)},
        "csharpFiles": cs, "baselineProcedures": procs,
        "migrations": migrations, "sqlFiles": sql_files, "uiClasses": ui,
        "patterns": patterns, "unresolvedCalls": unresolved,
    }
    write_report(args.out, result)
    print(json.dumps(result["summary"], sort_keys=True))
    print("Evidence written to: " + str(args.out))


if __name__ == "__main__":
    main()
