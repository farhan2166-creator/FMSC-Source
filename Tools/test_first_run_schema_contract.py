"""Inspect the record-free baseline's first-run and admission contracts.

Only SQL procedure source text is inspected. No database or user records are
opened, and no secrets, connection strings or credentials are printed.
"""
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
BASELINE = ROOT / "Tests/LifecycleSqlChecks/Baseline/schema.dacpac"


def bodies():
    with zipfile.ZipFile(BASELINE) as bundle:
        model = ET.fromstring(bundle.read("model.xml"))
    result = {}
    for element in model.iter():
        if element.tag.rsplit("}", 1)[-1] != "Element":
            continue
        if element.attrib.get("Type") != "SqlProcedure":
            continue
        name = element.attrib.get("Name", "")
        if not name:
            continue
        parts = []
        for prop in element.iter():
            if (prop.tag.rsplit("}", 1)[-1] == "Property" and
                    prop.attrib.get("Name") in ("Script", "BodyScript")):
                parts.append("".join(prop.itertext()))
        result[name] = "\n".join(parts)
    return result


class BaselineFirstRunContracts(unittest.TestCase):
    def test_inspect_critical_contracts(self):
        all_bodies = bodies()
        for name in ("[dbo].[AdmitStudent]", "[dbo].[BootstrapFirstAdministrator]",
                     "[dbo].[GetFirstRunStatus]", "[dbo].[GetSetupReadiness]"):
            self.assertIn(name, all_bodies)
            self.assertTrue(all_bodies[name].strip(), name)
        for name, markers in (
            ("[dbo].[AdmitStudent]", ("@MissingMandatoryFees", "@UsesTransport", "@UseSelectedFeeTypes")),
            ("[dbo].[BootstrapFirstAdministrator]", ("Administrator", "UserRoles")),
            ("[dbo].[GetSetupReadiness]", ("IsTransportLinked", "IsMandatory")),
        ):
            body = all_bodies[name]
            for marker in markers:
                self.assertIn(marker, body, (name, marker))
                if marker == "@MissingMandatoryFees":
                    start = body.index(marker)
                    print("BASELINE_ADMISSION_MANDATORY_GUARD_BEGIN")
                    print(body[start:start+1600])
                    print("BASELINE_ADMISSION_MANDATORY_GUARD_END")


if __name__ == "__main__":
    unittest.main()
