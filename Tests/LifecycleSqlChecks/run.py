"""Own one ephemeral SQL container; publish schema only, execute real SQL tests, and erase it.

Raw child output, credentials and the record-free SQL test package remain private temporary files.
Only a fixed report/summary allowlist is exported. No production connection option exists.
"""
from pathlib import Path
import argparse
import hashlib
import json
import os
import re
import secrets
import signal
import subprocess
import sys
import tempfile
import time
import uuid
import zipfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
PROJECT = Path(__file__).resolve().parent
IMAGE = "mcr.microsoft.com/mssql/server@sha256:4402d880dd4c34bfa7d8705e56a86cd6c88da80a1f6bbbe741f999e76264a090"
LABEL = "fmsc.level1.owner"
STAGES = ["Environment and tooling", "Database provisioning", "Lifecycle SQL", "Quick Registration SQL", "Mutation SQL", "Administration SQL", "Portable regressions", "Performance checks", "Cleanup"]


def verify_baseline():
    """Check the isolated SQL test fixture, not the original exported DACPAC.

    The test fixture is deliberately not a production-schema approval source.
    The real SQL scenarios and live runtime contract enforce their own behavior.
    """
    installer = re.search(r"\$DatabaseVersion\s*=\s*'(\d+)'", (ROOT / "Installer/core.ps1").read_text())
    if installer is None:
        raise ValueError("Required schema missing from Installer/core.ps1.")
    target = int(installer.group(1))
    if 'GenerateFmscRequiredSchema' not in (ROOT / "Directory.Build.targets").read_text():
        raise ValueError("The build must generate the application schema from core.ps1.")
    migrations = {int(p.stem) for p in (ROOT / "Migrations").glob("*.sql") if p.stem.isdecimal()}
    if migrations != set(range(2, target + 1)):
        raise ValueError("Numbered migration chain must be complete through the required schema.")
    provenance = json.loads((PROJECT / "Baseline/provenance.json").read_text())
    if provenance["schemaVersion"] != 1 or provenance["containsSchoolData"] is not False:
        raise ValueError("Tests must start from the complete data-free baseline 1.")
    if hashlib.sha256((PROJECT / "Baseline/schema.dacpac").read_bytes()).hexdigest() != provenance["packageSha256"]:
        raise ValueError("Baseline fixture checksum differs from the reviewed provenance.")
    # SQL test context classes must use the same generated schema constant.
    for context in ("QuickRegistrationSqlChecks", "MutationSqlChecks"):
        fixture = (ROOT / "Tests" / context / "DatabaseContext.cs").read_text()
        if 'public static partial class DatabaseHelper' not in fixture or re.search(
                r'RequiredDatabaseSchemaVersion\s*=', fixture):
            raise ValueError(context + " must use the generated schema; no duplicate literal allowed.")

    def provider_version(project):
        return next(e.get("Version") for e in ET.parse(project).getroot().iter("PackageReference") if e.get("Include") == "Microsoft.Data.SqlClient")
    version = provider_version(ROOT / "FMSC_SMS.csproj")
    for project in ("LifecycleSqlChecks", "QuickRegistrationSqlChecks", "MutationSqlChecks", "AdministrationSqlChecks", "PerformanceChecks"):
        if provider_version(ROOT / "Tests" / project / (project + ".csproj")) != version:
            raise ValueError("SQL test provider differs from the production package; review the dependency update.")

    package = PROJECT / "Baseline/schema.dacpac"
    with zipfile.ZipFile(package) as fixture:
        allowed = {"model.xml", "Origin.xml", "DacMetadata.xml", "[Content_Types].xml", "_rels/.rels"}
        names = fixture.namelist()
        if len(names) != len(allowed) or set(names) != allowed:
            raise ValueError("Unexpected fixture content; exported data/scripts are forbidden.")
        if fixture.testzip() is not None:
            raise ValueError("Damaged SQL test fixture entry.")
        origin = ET.fromstring(fixture.read("Origin.xml"))
        if any(e.tag.split("}")[-1] in ("ExportStatistics", "Server", "DataPhaseTables") or
               e.tag.endswith("ContainsExportedData") and (e.text or "").strip().lower() != "false"
               for e in origin.iter()):
            raise ValueError("Export/source metadata must not enter the data-free fixture.")
        relationships = ET.fromstring(fixture.read("_rels/.rels"))
        if any("Target" in e.attrib for e in relationships.iter()):
            raise ValueError("External or data relationships are forbidden in the SQL test fixture.")
        model = ET.fromstring(fixture.read("model.xml"))
        ns = {"d": model.tag.split("}")[0][1:]}
        schema = model.find("d:Model", ns)
        if schema is None or not any(e.get("Type") == "SqlProcedure" for e in schema):
            raise ValueError("SQL test fixture requires a deployable schema and stored procedures.")
    return True


def docker_env():
    env = os.environ.copy()
    for key in ("DOCKER_HOST", "DOCKER_CONTEXT", "DOCKER_TLS", "DOCKER_TLS_VERIFY", "DOCKER_CERT_PATH"):
        env.pop(key, None)
    return env


def docker_command():
    return ["docker", "--host=unix:///var/run/docker.sock"]


def remove_owned(state):
    if not state.exists():
        return
    owner = json.loads(state.read_text())
    token = str(uuid.UUID(owner["owner"]))
    name = owner["container"]
    if not re.fullmatch(r"fmsc-level1-[a-f0-9]{32}", name):
        raise ValueError("Refusing cleanup of an invalid container name.")
    command, env = docker_command(), docker_env()
    found = subprocess.run(command + ["ps", "-a", "--filter", "name=^/" + name + "$", "--format", "{{.ID}}"], env=env, capture_output=True, text=True, timeout=30)
    if found.returncode:
        raise RuntimeError("Unable to verify disposable-container cleanup.")
    if found.stdout.strip():
        marker = subprocess.run(command + ["inspect", "--format", '{{index .Config.Labels "' + LABEL + '"}}', name], env=env, capture_output=True, text=True, timeout=30)
        if marker.returncode or marker.stdout.strip() != token:
            raise ValueError("Container ownership marker differs; cleanup refused.")
        removed = subprocess.run(command + ["rm", "-f", "-v", name], env=env, capture_output=True, text=True, timeout=60)
        if removed.returncode:
            raise RuntimeError("Disposable-container removal failed.")
    confirmed = subprocess.run(command + ["ps", "-a", "--filter", "name=^/" + name + "$", "--format", "{{.ID}}"], env=env, capture_output=True, text=True, timeout=30)
    if confirmed.returncode or confirmed.stdout.strip():
        raise RuntimeError("Disposable-container removal was not confirmed.")
    state.unlink()


def write_evidence(output, report):
    output.mkdir(parents=True, exist_ok=True)
    (output / "report.json").write_text(json.dumps(report, indent=2))
    rows = report["scenarios"]
    summary = ["# FMSC_SMS Level 1 automated evidence", "", f"Commit: `{report['commit']}`", f"Run: `{report['runId']}`",
               f"Engine: SQL Server 2022 ({report.get('engine') or 'unverified'}); required schema: {report.get('schemaVersion') or 'unverified'}", "",
               f"PASS: {sum(s['result']=='PASS' for s in rows)} · FAIL: {sum(s['result']=='FAIL' for s in rows)} · BLOCKED: {sum(s['result']=='BLOCKED' for s in rows)}",
               "", "| Scenario | Result | Assertions | Seconds |", "|---|---|---:|---:|"]
    summary += [f"| {s['name'].replace('|','/')} | {s['result']} | {s.get('assertions',0)} | {s.get('seconds',0):.2f} |" for s in rows]
    for row in rows:
        if row.get("assertion"):
            summary += ["", f"**{row['name']}**: {row['assertion']}", f"Category: {row.get('category','Prerequisite failure')}"]
    summary += ["", f"Expected refusals verified: {report.get('expectedRefusals',0)}", "", "## Coverage limits", ""]
    summary += ["- " + item for item in report["limitations"]]
    (output / "summary.md").write_text("\n".join(summary) + "\n")


def validate_evidence(output, forbidden=()):
    allowed = {"commit", "runId", "result", "schemaVersion", "engine", "scenarios", "limitations", "expectedRefusals", "measurements", "workingTreeDirty"}
    report = json.loads((output / "report.json").read_text())
    if set(report) - allowed or report["result"] not in ("PASS", "FAIL", "BLOCKED"):
        raise ValueError("Unexpected evidence metadata.")
    permitted = {"name", "result", "assertions", "seconds", "assertion", "category", "sqlError"}
    for scenario in report["scenarios"]:
        if set(scenario) - permitted or scenario["result"] not in ("PASS", "FAIL", "BLOCKED"):
            raise ValueError("Unexpected scenario payload.")
    for key, value in report.get("measurements", {}).items():
        if not re.fullmatch(r"[a-zA-Z][a-zA-Z0-9_]{0,79}", key) or type(value) not in (float, int) or not 0 <= value < 1e12:
            raise ValueError("Unexpected numeric measurement.")
    if report["result"] == "PASS" and any(s["result"] != "PASS" for s in report["scenarios"]):
        raise ValueError("Failed/blocked scenarios cannot be reported as PASS.")
    payload = (output / "report.json").read_text() + (output / "summary.md").read_text()
    blocked = ["Password=", "MSSQL_SA_PASSWORD", "FMSC_AUDIT_CONNECTION", "ConnectionString", "PasswordHash", "PasswordSalt",
               "Ayaan Example", "Zara Example", "Fictional student address"] + list(forbidden)
    if any(value and value.lower() in payload.lower() for value in blocked):
        raise ValueError("Sensitive/student payload detected in evidence; upload refused.")
    if {p.name for p in output.iterdir()} != {"report.json", "summary.md"}:
        raise ValueError("Evidence directory must contain exactly the two approved files.")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--state", type=Path)
    parser.add_argument("--sqlpackage", type=Path)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--cleanup", action="store_true")
    parser.add_argument("--level3", action="store_true", help="Also execute bounded Level 3 faults, backup/restore and measured workloads in a separate owned catalog.")
    parser.add_argument("--diagnostics", type=Path, help="Optional local private diagnostic file; never upload it.")
    parser.add_argument("--inject-provision-failure", action="store_true", help="Exercise failure evidence and container cleanup; always exits nonzero.")
    args = parser.parse_args()
    if args.output is None or args.state is None:
        parser.error("--output and --state are required for execution/cleanup")
    if args.cleanup:
        remove_owned(args.state)
        return 0
    token = uuid.uuid4()
    output = args.output.resolve()
    report = {"commit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(), "runId": str(token), "workingTreeDirty": bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=ROOT, text=True).strip()), "scenarios": [], "measurements": {"HostLogicalCpus": os.cpu_count() or 0, "HostMemoryMiB": os.sysconf("SC_PHYS_PAGES") * os.sysconf("SC_PAGE_SIZE") / (1024 * 1024)}, "limitations": [
        "Linux real-SQL lifecycle/integration tests; existing desktop CI separately runs Windows control contracts and PDF export.",
        "No interactive visual, printing, installer, Windows LocalDB/operator-account or actual network-loss acceptance.",
        "No real school rows are imported: the approved data-free test baseline starts directly at version 1, followed by any future numbered migrations in each owned catalog. The reference DACPAC is never published.",
        "Test-only seeds set baseline schema versions and repair DacFx recovery-scope type; this does not prove the owner's distributed MDF/LDF snapshot.",
        "Owned disposable fixtures are used; production databases are never selected."]}
    current_stage = None
    private = tempfile.TemporaryDirectory(prefix="fmsc-level1-private-")
    secret = "A!" + secrets.token_urlsafe(32) + "9z"
    env = docker_env()
    env.update(SQLCMDPASSWORD=secret, FMSC_TEST_RUN_ID=str(token), FMSC_TEST_PRIVATE_ROOT=private.name,
               XDG_DATA_HOME=str(Path(private.name) / "data"), DOTNET_CLI_TELEMETRY_OPTOUT="1")
    if os.environ.get("GITHUB_ACTIONS") == "true":
        print("::add-mask::" + secret, flush=True)

    def execute(command, timeout=240, cwd=ROOT):
        result = subprocess.run(command, cwd=cwd, env=env, capture_output=True, text=True, timeout=timeout)
        # Never stream child logs or command arguments. Expected source labels/counts are summarized below.
        if result.returncode:
            if args.diagnostics:
                args.diagnostics.parent.mkdir(parents=True, exist_ok=True)
                diagnostic = (result.stdout + result.stderr).replace(secret, "[REDACTED]")
                if env.get("FMSC_AUDIT_CONNECTION"):
                    diagnostic = diagnostic.replace(env["FMSC_AUDIT_CONNECTION"], "[CONNECTION REDACTED]")
                args.diagnostics.write_text(diagnostic)
                args.diagnostics.chmod(0o600)
            errors = re.findall(r"(?:error (?:CS|NETSDK|MSB|SQL)[0-9]+[^\r\n]*|Unhandled exception\. [^:]+)", result.stdout + result.stderr)
            # SQL errors may include record values after the message header. Keep only
            # numeric codes and SQL severity/state, never the SQL or raw output.
            sql_codes = re.findall(r"Msg (\d+), Level (\d+), State (\d+)", result.stdout + result.stderr)
            codes = ["SQL Msg " + code + " level " + level + " state " + state for code, level, state in sql_codes[-3:]]
            # Only labels emitted by the owned synthetic mutation test's Check()
            # helper are eligible for evidence; reject punctuation that could
            # carry a connection string, SQL statement or student record.
            fixture_labels = re.findall(r"^MUTATION_CHECK_FAIL: ([A-Za-z0-9 ,.'()/-]{1,140})\s*$",
                                        result.stdout + result.stderr, re.MULTILINE)
            fixture_error = ["Mutation fixture assertion: " + fixture_labels[-1]] if fixture_labels else []
            # The synthetic-only runtime schema compatibility fixture may
            # emit one sanitized metadata label. Never propagate raw SQL text,
            # school rows, credentials, connection URLs or full exception data.
            manifest_labels = re.findall(r"^SCHEMA_CONTRACT_FAIL: ([A-Za-z0-9 .()/-]{1,120})\s*$",
                                         result.stdout + result.stderr, re.MULTILINE)
            manifest_error = ["Synthetic schema contract: " + manifest_labels[-1]] if manifest_labels else []
            # A numeric source line in the owned AdministrationSqlChecks fixture
            # identifies a missing synthetic prerequisite without leaking SQL,
            # connection values, real names, or the raw stack trace.
            admin_lines = re.findall(r"Tests[/\\]AdministrationSqlChecks[/\\]Program\.cs:line (\d+)",
                                     result.stdout + result.stderr)
            admin_location = ["Administration fixture source line " + admin_lines[-1]] if admin_lines else []
            raise RuntimeError("Child command failed with exit " + str(result.returncode) +
                               ("; " + " | ".join((errors + codes + fixture_error + manifest_error + admin_location)[-3:])
                                if errors or codes or fixture_error or manifest_error or admin_location else ""))
        return result.stdout + result.stderr

    def stage(name, action):
        nonlocal current_stage
        current_stage = name
        start = time.monotonic()
        value = action()
        report["scenarios"].append({"name": name, "result": "PASS", "assertions": 0, "seconds": time.monotonic() - start})
        print("PASS: " + name, flush=True)
        return value

    def sql(catalog, body):
        script = Path(private.name) / "command.sql"
        if script.exists():
            script.chmod(0o600)
        script.write_text(body)
        script.chmod(0o444)  # docker cp retains mode; SQL Server runs as its non-root mssql user.
        execute(docker_command() + ["cp", str(script), container + ":/tmp/level1.sql"])
        # -I is important for schema migrations containing CREATE OR ALTER PROC:
        # SQL Server persists the session's QUOTED_IDENTIFIER flag in each module.
        # With sqlcmd's default OFF, later DML against filtered/computed indexes
        # can fail with SQL 1934 even if the caller sets its own session options.
        return execute(docker_command() + ["exec", "--env", "SQLCMDPASSWORD", container, "/opt/mssql-tools18/bin/sqlcmd", "-S", "localhost", "-U", "sa", "-C", "-b", "-I", "-d", catalog, "-i", "/tmp/level1.sql"])

    def provision(suffix, current_schema=False, legacy_nonempty=False):
        def checked(label, action):
            try:
                return action()
            except RuntimeError as ex:
                raise RuntimeError("Disposable provisioning " + label + ": " + str(ex)) from ex
        catalog = "FMSC_AuditChecks_" + suffix + "_" + token.hex
        env["FMSC_AUDIT_CONNECTION"] = f"Server=127.0.0.1,{port};Database={catalog};User ID=sa;Password={secret};Encrypt=False;TrustServerCertificate=True;Connect Timeout=20;Pooling=True"
        # Every owned catalog starts from the data-free baseline, never imported school data.
        # The root fmsc.dacpac is a reference artifact, never a deployment/test source.
        package = str(Path(private.name) / "schema-only.dacpac")
        prefix = [str(args.sqlpackage)] if args.sqlpackage else [args.dotnet, "tool", "run", "sqlpackage", "--"]
        checked("schema-only publish", lambda: execute(prefix + ["/Action:Publish", "/SourceFile:" + package, "/TargetConnectionString:" + env["FMSC_AUDIT_CONNECTION"], "/p:CreateNewDatabase=True"], timeout=240, cwd=PROJECT))
        checked("owner registration", lambda: sql(catalog, "DECLARE @Owner uniqueidentifier='" + str(token) + "'; EXEC sys.sp_addextendedproperty @name=N'FMSC_Level1Owner',@value=@Owner;"))
        checked("baseline 1 initialization", lambda: sql(catalog, (PROJECT / "Baseline/Initialize.sql").read_text(encoding="utf-8-sig")))
        def wrapped(migration):
            return "SET XACT_ABORT ON; BEGIN TRANSACTION;\n" + migration.read_text(encoding="utf-8-sig") + "\nCOMMIT TRANSACTION;"

        def verify_version7_rollback():
            return sql(catalog, """
                IF (SELECT TOP(1) TRY_CONVERT(int,VersionNumber)
                    FROM dbo.DatabaseVersion ORDER BY VersionId DESC)<>7
                    THROW 51000,'Expected failed Migration 8 to retain schema 7.',1;
                IF SCHEMA_ID(N'comm') IS NULL
                   OR (SELECT COUNT_BIG(*) FROM comm.Contact)<>1
                   OR (SELECT COUNT_BIG(*) FROM comm.Message)<>1
                   OR OBJECT_ID(N'dbo.StudentGuardianCommunication',N'U') IS NULL
                   OR (SELECT COUNT_BIG(*) FROM dbo.FMSC_Test_Migration8SchoolSentinel)<>1
                    THROW 51000,'Failed migration did not preserve legacy and school records.',1;
                """)

        def expect_migration8_refusal(migration, expected_code):
            # The shared SQL harness intentionally sanitizes messages to numeric
            # SQL codes; never inspect or export raw SQL text or school records.
            try:
                sql(catalog, wrapped(migration))
            except RuntimeError as ex:
                if ("SQL Msg " + str(expected_code) + " level 16 state 1") not in str(ex):
                    raise RuntimeError("Migration 8 rejected with the wrong safe diagnostic: " + str(ex)) from ex
            else:
                raise RuntimeError("Migration 8 incorrectly accepted unsupported legacy schema state.")
            checked("migration-8 refusal rollback", verify_version7_rollback)

        for migration in sorted((ROOT / "Migrations").glob("*.sql"), key=lambda p: int(p.stem)):
            if legacy_nonempty and migration.stem == "8":
                checked("seed nonempty legacy communication tables",
                    lambda: sql(catalog, (PROJECT / "LegacyCommunicationV7Seed.sql").read_text(encoding="utf-8")))
                # An unexpected column must fail without retiring anything.
                checked("create unsupported guardian extension",
                    lambda: sql(catalog, "ALTER TABLE dbo.StudentGuardianCommunication ADD FMSC_Test_UnsupportedExtension int NULL;"))
                expect_migration8_refusal(migration, 51042)
                checked("remove unsupported guardian extension",
                    lambda: sql(catalog, "ALTER TABLE dbo.StudentGuardianCommunication DROP COLUMN FMSC_Test_UnsupportedExtension;"))
                # The guardian table's own schema-5 CHECK constraint must not
                # be mistaken for an independently owned SQL dependency.
                # An actual dbo view referencing guardian communication must fail.
                checked("create external guardian SQL dependency",
                    lambda: sql(catalog, """
                        EXEC(N'CREATE VIEW dbo.FMSC_Test_Migration8ExternalGuardianView
                            AS SELECT AdmissionId FROM dbo.StudentGuardianCommunication;');
                        """))
                expect_migration8_refusal(migration, 51045)
                checked("remove external guardian SQL dependency",
                    lambda: sql(catalog, "DROP VIEW dbo.FMSC_Test_Migration8ExternalGuardianView;"))
                # An external FK must also fail safely instead of being dropped.
                checked("create external legacy dependency",
                    lambda: sql(catalog, """
                        CREATE TABLE dbo.FMSC_Test_Migration8UnknownDependency(
                            ContactId uniqueidentifier NOT NULL REFERENCES comm.Contact(ContactId));
                        INSERT dbo.FMSC_Test_Migration8UnknownDependency(ContactId)
                            SELECT ContactId FROM comm.Contact;
                        """))
                expect_migration8_refusal(migration, 51047)
                checked("remove external legacy dependency",
                    lambda: sql(catalog, "DROP TABLE dbo.FMSC_Test_Migration8UnknownDependency;"))
            checked("migration " + migration.stem, lambda m=migration: sql(catalog, wrapped(m)))
        if legacy_nonempty:
            # Compare the completed migration chain to the installer's declared
            # schema, not the last schema at the time this test was written.
            declared = re.search(r"\$DatabaseVersion\s*=\s*'(\d+)'",
                                 (ROOT / "Installer/core.ps1").read_text()).group(1)
            legacy_check = (PROJECT / "LegacyCommunicationV9Verify.sql").read_text(encoding="utf-8")
            if legacy_check.count("__EXPECTED_SCHEMA__") != 1:
                raise ValueError("Legacy migration test schema placeholder is missing.")
            checked("discard nonempty legacy records without archive and keep dbo data",
                lambda: sql(catalog, legacy_check.replace("__EXPECTED_SCHEMA__", declared)))
        return catalog

    def run_lifecycle(mode, report_name):
        path = Path(private.name) / report_name
        env["FMSC_TEST_REPORT_PATH"] = str(path)
        command = [args.dotnet, "run", "--project", str(PROJECT / "LifecycleSqlChecks.csproj"), "-c", "Release", "--no-build", "--"] + mode
        result = subprocess.run(command, cwd=ROOT, env=env, capture_output=True, text=True, timeout=240)
        if path.exists():
            data = json.loads(path.read_text())
            report["engine"] = data.get("engine")
            report["schemaVersion"] = data.get("schemaVersion")
            report.setdefault("measurements", {}).update(data.get("measurements", {}))
            report["expectedRefusals"] = report.get("expectedRefusals", 0) + data.get("expectedRefusals", 0)
            report["scenarios"] += [{"name": s["Name"], "result": s["Result"], "assertions": s["Assertions"], "seconds": s["Seconds"],
                "assertion": s.get("Assertion"), "category": s.get("Category"), "sqlError": s.get("SqlError")} for s in data["scenarios"]]
        if result.returncode:
            # Capture only synthetic schema *metadata* labels, not child SQL,
            # connection strings or data rows (all raw output remains private).
            labels = re.findall(r"^SCHEMA_CONTRACT_FAIL: ([A-Za-z0-9 .()/-]{1,120})\s*$",
                                result.stdout + result.stderr, re.MULTILINE)
            hint = ("; Synthetic schema contract: " + labels[-1]) if labels else ""
            raise RuntimeError("Lifecycle runner reported failed or blocked scenarios; inspect the sanitized assertions." + hint)

    def existing(project, suffix, mode=None):
        provision(suffix)
        run_lifecycle(mode or ["--fixture-only"], suffix + ".json")
        command = [args.dotnet, "run", "--project", str(ROOT / "Tests" / project / (project + ".csproj")), "-c", "Release", "--no-build"]
        modes = [[], ["--", "--adjustment-types"], ["--", "--bank"], ["--", "--additional-fee"]] if project == "MutationSqlChecks" else [[]]
        count = 0
        for arguments in modes:
            text = execute(command + arguments, timeout=240)
            totals = re.findall(r"PASS:\s+(\d+)\s", text)
            count += int(totals[-1]) if totals else 0
        return count

    def interrupted(signum, _frame):
        raise SystemExit(128 + signum)

    signal.signal(signal.SIGTERM, interrupted)
    signal.signal(signal.SIGINT, interrupted)
    failed = False
    try:
        def tooling():
            execute([args.dotnet, "--version"])
            execute(docker_command() + ["info", "--format", "{{.ServerVersion}}"])
            verify_baseline()
            if not args.sqlpackage:
                execute([args.dotnet, "tool", "restore", "--tool-manifest", str(PROJECT / ".config/dotnet-tools.json")], timeout=300)
            for project in ["LifecycleSqlChecks", "QuickRegistrationSqlChecks", "MutationSqlChecks", "AdministrationSqlChecks", "AuditFixChecks", "PerformanceChecks"]:
                execute([args.dotnet, "build", str(ROOT / "Tests" / project / (project + ".csproj")), "-c", "Release"], timeout=300)
        stage("Environment and tooling", tooling)
        verify_baseline()
        import shutil
        shutil.copyfile(PROJECT / "Baseline/schema.dacpac", Path(private.name) / "schema-only.dacpac")
        container = "fmsc-level1-" + token.hex
        args.state.parent.mkdir(parents=True, exist_ok=True)
        args.state.write_text(json.dumps({"owner": str(token), "container": container}))
        def start_container():
            credentials = Path(private.name) / "sql.env"
            credentials.write_text("ACCEPT_EULA=Y\nMSSQL_SA_PASSWORD=" + secret + "\n")
            credentials.chmod(0o600)
            execute(docker_command() + ["run", "-d", "--name", container, "--label", LABEL + "=" + str(token), "--env-file", str(credentials),
                                       "--memory", "3g", "--cpus", "2", "-p", "127.0.0.1::1433", IMAGE], timeout=300)
            deadline = time.monotonic() + 150
            while time.monotonic() < deadline:
                result = subprocess.run(docker_command() + ["exec", "--env", "SQLCMDPASSWORD", container, "/opt/mssql-tools18/bin/sqlcmd", "-S", "localhost", "-U", "sa", "-C", "-b", "-Q", "SELECT 1;"], env=env, capture_output=True, text=True, timeout=10)
                if result.returncode == 0:
                    break
                time.sleep(1)
            else:
                raise RuntimeError("SQL Server readiness timed out after bounded connection probes.")
            match = re.fullmatch(r"127\.0\.0\.1:([0-9]+)\s*", execute(docker_command() + ["port", container, "1433/tcp"]))
            if not match:
                raise RuntimeError("SQL port is not exclusively bound to loopback.")
            return match.group(1)
        port = stage("SQL container readiness", start_container)
        current_stage = "Database provisioning"
        if args.inject_provision_failure:
            raise RuntimeError("Controlled provisioning failure for cleanup verification.")
        stage("Database provisioning", lambda: provision("Lifecycle"))
        stage("Lifecycle SQL", lambda: run_lifecycle([], "lifecycle.json"))
        stage("Current release schema provisioning", lambda: provision("Current", current_schema=True))
        stage("Current release school scenarios", lambda: run_lifecycle(["--school-scenarios"], "school-scenarios.json"))
        stage("Nonempty legacy communication migration, refusal and rollback",
              lambda: provision("LegacyCommunication", legacy_nonempty=True))
        report["scenarios"][-1]["assertions"] = 16
        for label, project, suffix, mode in [
            ("Quick Registration SQL", "QuickRegistrationSqlChecks", "Quick", None),
            ("Mutation SQL", "MutationSqlChecks", "Mutation", ["--mutation-fixture"]),
            ("Administration SQL", "AdministrationSqlChecks", "Administration", None)]:
            count = stage(label, lambda p=project, s=suffix, m=mode: existing(p, s, m))
            report["scenarios"][-1]["assertions"] = count
        if args.level3:
            stage("Level 3 database provisioning", lambda: provision("Reliability"))
            stage("Level 3 SQL reliability", lambda: run_lifecycle(["--level3"], "reliability.json"))
        for label, project in [("Portable regressions", "AuditFixChecks"), ("Performance checks", "PerformanceChecks")]:
            text = stage(label, lambda p=project: execute([args.dotnet, "run", "--project", str(ROOT / "Tests" / p / (p + ".csproj")), "-c", "Release", "--no-build"], timeout=180))
            report["scenarios"][-1]["assertions"] = sum(map(int, re.findall(r"PASS:\s+(\d+)\s", text)))
    except (Exception, SystemExit) as error:
        failed = True
        # Child output and parameters deliberately stay out of artifacts/logs.
        report["scenarios"].append({"name": current_stage or "Environment and tooling", "result": "FAIL", "assertions": 0, "seconds": 0,
            "assertion": str(error) if isinstance(error, RuntimeError) else "Unexpected " + type(error).__name__,
            "category": "Test environment/provisioning" if current_stage in (None, "Environment and tooling", "Database provisioning") else "Inspect recorded assertion; distinguish fixture from production defect"})
        print("FAIL: " + (current_stage or "Environment and tooling"), flush=True)
    finally:
        try:
            stage("Cleanup", lambda: remove_owned(args.state))
        except Exception:
            failed = True
            report["scenarios"].append({"name": "Cleanup", "result": "FAIL", "assertions": 0, "seconds": 0, "assertion": "Owned container removal could not be verified.", "category": "Test environment/provisioning"})
        private.cleanup()
        existing_names = {s["name"] for s in report["scenarios"]}
        report["scenarios"] += [{"name": name, "result": "BLOCKED", "assertions": 0, "seconds": 0, "assertion": "An earlier prerequisite did not complete."} for name in STAGES if name not in existing_names]
        report["result"] = "FAIL" if failed or any(s["result"] != "PASS" for s in report["scenarios"]) else "PASS"
        write_evidence(output, report)
        try:
            validate_evidence(output, [secret, env.get("FMSC_AUDIT_CONNECTION", "")])
        except ValueError:
            # Replace unsafe content with a fixed failure, never a captured payload.
            write_evidence(output, {"commit": report["commit"], "runId": str(token), "result": "FAIL", "limitations": ["Unsafe evidence was withheld."],
                "scenarios": [{"name": "Evidence sanitization", "result": "FAIL", "assertions": 0, "seconds": 0,
                               "assertion": "Sensitive/unexpected payload detected; original evidence was withheld.", "category": "Test infrastructure"}]})
            raise
        print((output / "summary.md").read_text(), flush=True)
    return 1 if report["result"] != "PASS" else 0


if __name__ == "__main__":
    raise SystemExit(main())
