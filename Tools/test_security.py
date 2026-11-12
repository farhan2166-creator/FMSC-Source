"""Check scanner detection with disposable fixtures, never real credentials."""
import argparse
import io
from pathlib import Path
import subprocess
import tempfile
import unittest
import zipfile
from check_security import forbidden_path, inspect_dacpac


class SafetyChecks(unittest.TestCase):
    def test_private_paths(self):
        for path in ['Database/school.MDF', 'archive.bak', '.env.local', 'credentials.json', 'private.pem']:
            self.assertTrue(forbidden_path(path), path)
        for path in ['.env.example', 'Core/PasswordPolicy.cs', 'schema.dacpac']:
            self.assertFalse(forbidden_path(path), path)

    def test_schema_package(self):
        def package(extra=None, exported='false'):
            data = io.BytesIO()
            with zipfile.ZipFile(data, 'w') as archive:
                archive.writestr('Origin.xml', '<ContainsExportedData>%s</ContainsExportedData>' % exported)
                archive.writestr('model.xml', '<Model/>')
                if extra:
                    archive.writestr(extra, 'fixture')
            return data.getvalue()
        self.assertIn('model.xml', inspect_dacpac(package()))
        for data in [package('Data/rows.bcp'), package(exported='true'), package('../outside')]:
            with self.assertRaises(ValueError):
                inspect_dacpac(data)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--gitleaks')
    args = parser.parse_args()
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(SafetyChecks)
    if not unittest.TextTestRunner(verbosity=2).run(suite).wasSuccessful():
        raise SystemExit(1)
    if not args.gitleaks:
        print('Scanner integration NOT RUN: supply --gitleaks.')
        return
    with tempfile.TemporaryDirectory(prefix='fmsc-secret-fixture-') as temp:
        fixture = Path(temp) / 'fixture.txt'
        fixture.write_text('password' + ' = "' + 'Disposable scanner fixture 12345' + '"\n')
        result = subprocess.run([args.gitleaks, 'dir', temp, '--config', '.gitleaks.toml', '--redact', '--no-banner'], capture_output=True)
        if result.returncode != 1:
            raise SystemExit('FAIL: scanner did not detect the synthetic literal password.')
        fixture.write_text('A harmless fixture without credentials.\n')
        result = subprocess.run([args.gitleaks, 'dir', temp, '--config', '.gitleaks.toml', '--redact', '--no-banner'], capture_output=True)
        if result.returncode != 0:
            raise SystemExit('FAIL: scanner rejected the harmless control fixture.')
    # Check that the public Dropbox app key is only exempt in the precise
    # publisher registration file. Never print matched values or tokens.
    import json
    registration = json.loads((Path(__file__).resolve().parent.parent / 'CloudBackupProviders.json').read_text())
    public_key = registration.get('DropboxAppKey', '')
    if public_key:
        with tempfile.TemporaryDirectory(prefix='fmsc-public-oauth-id-') as temp:
            with tempfile.TemporaryDirectory(prefix='fmsc-gitleaks-evidence-') as reports:
                folder = Path(temp)
                allowed = folder / 'CloudBackupProviders.json'
                allowed.write_text(json.dumps({'DropboxAppKey': public_key}) + '\n')
                def scan():
                    report = Path(reports) / 'findings.json'
                    return_code = subprocess.run([args.gitleaks, 'dir', temp, '--config',
                        str((Path(__file__).resolve().parent.parent / '.gitleaks.toml')),
                        '--redact', '--no-banner', '--report-format', 'json',
                        '--report-path', str(report)], capture_output=True).returncode
                    findings = json.loads(report.read_text()) if report.exists() else []
                    return return_code, sorted({str(item.get('RuleID')) for item in findings if isinstance(item, dict)})
                code, rules = scan()
                if code != 0:
                    raise SystemExit(f'FAIL: public Dropbox app key rejected in registration file (exit {code}; rules {rules}).')
                allowed.rename(folder / 'unreviewed-cloud-config.json')
                code, rules = scan()
                if code != 1:
                    raise SystemExit(f'FAIL: Dropbox scanner not limited to approved file (exit {code}; rules {rules}).')
    print('PASS: real scanner detected the disposable secret and accepted the clean control.')


if __name__ == '__main__':
    main()
