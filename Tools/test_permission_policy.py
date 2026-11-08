"""Check the pinned data-free SQL test fixture, embedded catalog and access consumers."""
import hashlib
import json
from pathlib import Path
import re
import unittest
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]


class PermissionPolicyChecks(unittest.TestCase):
    def setUp(self):
        self.catalog = json.loads((ROOT/'Core/PermissionCatalog.json').read_text(encoding='utf-8'))
        self.codes = {entry['Code'] for entry in self.catalog}

    def test_unique_base_catalog(self):
        # Pinned 28 school permissions plus three separately-authorized Communication permissions.
        self.assertEqual(31, len(self.codes))
        self.assertEqual(len(self.codes), len(self.catalog))

    def test_catalog_covers_actual_consumers_without_dead_options(self):
        with zipfile.ZipFile(ROOT/'Tests/LifecycleSqlChecks/Baseline/schema.dacpac') as package:
            root = ET.fromstring(package.read('model.xml'))
        ns = {'d': root.tag.split('}')[0][1:]}
        modules = {}
        for element in root.find('d:Model', ns):
            if element.get('Type') != 'SqlProcedure':
                continue
            body = element.find("d:Property[@Name='BodyScript']/d:Value", ns)
            modules[element.get('Name')] = body.text or ''
        sql_codes = set()
        for body in modules.values():
            for statement in re.findall(r'EXEC(?:UTE)?\s+dbo\.Require(?:Any)?Permission\b[^;]*;', body, re.I):
                sql_codes.update(re.findall(r"N?'([A-Za-z]+\.[A-Za-z.]+)'", statement))
        self.assertLessEqual(sql_codes, self.codes)
        old_catalog = {'Students.Edit', 'Students.Photo.Manage', 'System.Permissions',
                       'System.Roles.Deactivate', 'Students.Siblings'} | self.codes
        cs_codes = set()
        for path in ROOT.rglob('*.cs'):
            if any(part in ('Tests', 'bin', 'obj', '.git', 'RecoveryBackups') for part in path.relative_to(ROOT).parts):
                continue
            cs_codes.update(set(re.findall(r'"([A-Za-z]+\.[A-Za-z.]+)"', path.read_text(encoding='utf-8'))) & old_catalog)
        self.assertLessEqual(cs_codes, self.codes)
        self.assertEqual(self.codes, sql_codes | cs_codes)

    def test_application_and_installer_require_current_baseline(self):
        installer = re.search(r"\$DatabaseVersion = '(\d+)'", (ROOT/'Installer/core.ps1').read_text())
        self.assertIsNotNone(installer)
        required = int(installer.group(1))
        self.assertIn('GenerateFmscRequiredSchema', (ROOT/'Directory.Build.targets').read_text())
        self.assertNotRegex((ROOT/'DatabaseHelper.cs').read_text(),
                            r'RequiredDatabaseSchemaVersion\s*=\s*"[0-9]+"')
        migrations = {int(path.stem) for path in (ROOT/'Migrations').glob('*.sql')}
        self.assertGreaterEqual(required, 1)
        self.assertEqual(set(range(2, required + 1)), migrations)
        self.assertEqual({f'{version}.sql' for version in range(2, required + 1)},
                         {path.name for path in (ROOT/'Migrations').glob('*.sql')})
        self.assertFalse((ROOT/'Tests/LifecycleSqlChecks/Migrations').exists(), 'Pre-release migration replay has been retired.')

    def test_committed_synthetic_smoke_schema_package_integrity(self):
        # Root fmsc.dacpac is owner-managed manual inspection material.
        # The committed, data-free smoke fixture is independent of that file.
        with zipfile.ZipFile(ROOT/'Tests/LifecycleSqlChecks/Baseline/schema.dacpac') as package:
            origin = ET.fromstring(package.read('Origin.xml'))
            checksums = [node for node in origin.iter() if node.tag.endswith('}Checksum')]
            self.assertTrue(checksums, 'DACPAC must contain its model checksum.')
            for checksum in checksums:
                part = checksum.get('Uri').lstrip('/')
                self.assertEqual(hashlib.sha256(package.read(part)).hexdigest().upper(), checksum.text.upper(), part)

    def test_metadata_fits_sql_columns(self):
        for entry in self.catalog:
            self.assertTrue(entry['Code'] and len(entry['Code']) <= 100)
            self.assertTrue(entry['Name'] and len(entry['Name']) <= 100)
            self.assertTrue(entry['Description'] and len(entry['Description']) <= 250)


if __name__ == '__main__':
    unittest.main()
