"""Offline transfer-boundary tests; all account/market contents are authored."""
import hashlib
import importlib.util
import json
import os
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

SPEC = importlib.util.spec_from_file_location(
    'recovery_bundle', Path(__file__).resolve().parents[1] / 'scripts/recovery_bundle.py')
bundle = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(bundle)


class RecoveryBundleTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.snapshot = self.root / 'snapshot'; self.snapshot.mkdir(mode=0o700)
        with sqlite3.connect(self.snapshot / 'research.db') as db:
            db.execute('CREATE TABLE accounts(cash TEXT)')
            db.execute("INSERT INTO accounts VALUES('19.75')")
        (self.snapshot / 'report.json').write_text('{"equityGbp":19.75}')
        for path in self.snapshot.iterdir():
            path.chmod(0o600)
        self.manifest = self.root / 'manifest.json'

    def seal(self):
        return bundle.seal(self.snapshot, self.manifest, 'a' * 40)

    def test_portable_copy_matches_all_original_bytes_and_read_only_sqlite(self):
        pin = self.seal()
        self.assertEqual(hashlib.sha256(self.manifest.read_bytes()).hexdigest(), pin)
        target = self.root / 'other-host'; shutil.copytree(self.snapshot, target)
        proof = bundle.verify(target, self.manifest, pin)
        self.assertEqual(2, proof['files'])
        self.assertEqual(['research.db'], proof['sqliteChecked'])
        self.assertFalse((target / 'research.db-wal').exists())
        self.assertFalse((target / 'research.db-shm').exists())
        rows = json.loads(self.manifest.read_bytes())['files']
        report = next(row for row in rows if row['path'] == 'report.json')
        self.assertEqual(hashlib.sha256(b'{"equityGbp":19.75}').hexdigest(), report['sha256'])

    def test_modified_missing_or_additional_file_refuses(self):
        pin = self.seal(); original = (self.snapshot / 'report.json').read_bytes()
        (self.snapshot / 'report.json').write_bytes(original + b' ')
        with self.assertRaisesRegex(bundle.BundleError, 'bundle-content'):
            bundle.verify(self.snapshot, self.manifest, pin)
        (self.snapshot / 'report.json').unlink()
        with self.assertRaisesRegex(bundle.BundleError, 'bundle-content'):
            bundle.verify(self.snapshot, self.manifest, pin)
        (self.snapshot / 'report.json').write_bytes(original); (self.snapshot / 'report.json').chmod(0o600)
        extra = self.snapshot / 'unregistered'; extra.write_bytes(b'new'); extra.chmod(0o600)
        with self.assertRaisesRegex(bundle.BundleError, 'bundle-content'):
            bundle.verify(self.snapshot, self.manifest, pin)

    def test_wrong_or_changed_manifest_pin_refuses(self):
        pin = self.seal()
        with self.assertRaisesRegex(bundle.BundleError, 'manifest-pin'):
            bundle.verify(self.snapshot, self.manifest, 'b' * 64)
        self.manifest.write_bytes(self.manifest.read_bytes() + b'\n')
        with self.assertRaisesRegex(bundle.BundleError, 'manifest-pin'):
            bundle.verify(self.snapshot, self.manifest, pin)

    def test_wal_shm_and_rollback_journals_are_refused_even_when_empty(self):
        for suffix in ('-wal', '-shm', '-journal'):
            with self.subTest(suffix=suffix):
                sidecar = self.snapshot / ('research.db' + suffix); sidecar.touch(mode=0o600)
                with self.assertRaisesRegex(bundle.BundleError, 'sqlite-sidecar'):
                    self.seal()
                sidecar.unlink()

    def test_corrupt_database_cannot_be_sealed_as_good(self):
        (self.snapshot / 'research.db').write_bytes(b'not a sqlite database')
        with self.assertRaisesRegex(bundle.BundleError, 'sqlite-integrity'):
            self.seal()

    def test_symlink_and_hardlink_cannot_read_outside_the_bundle(self):
        outside = self.root / 'private'; outside.write_text('authored private value')
        link = self.snapshot / 'link'; link.symlink_to(outside)
        with self.assertRaisesRegex(bundle.BundleError, 'bundle-link'):
            self.seal()
        link.unlink(); os.link(outside, link)
        with self.assertRaisesRegex(bundle.BundleError, 'bundle-link'):
            self.seal()

    def test_case_collisions_are_refused_before_transfer_to_a_mac(self):
        path = self.snapshot / 'REPORT.json'
        if not path.exists():
            path.write_bytes(b'other'); path.chmod(0o600)
            with self.assertRaisesRegex(bundle.BundleError, 'bundle-portability'):
                self.seal()
        else:
            # A case-insensitive receiver cannot create both source files; reject the manifest instead.
            self.seal(); value = json.loads(self.manifest.read_bytes())
            row = dict(next(r for r in value['files'] if r['path'] == 'report.json')); row['path'] = 'REPORT.json'
            value['files'].append(row); value['files'].sort(key=lambda r: r['path'])
            self.manifest.write_text(json.dumps(value))
            pin = hashlib.sha256(self.manifest.read_bytes()).hexdigest()
            with self.assertRaisesRegex(bundle.BundleError, 'manifest-contract'):
                bundle.verify(self.snapshot, self.manifest, pin)

    def test_non_ascii_names_refuse_instead_of_changing_normalization_on_transfer(self):
        path = self.snapshot / 'caf\u00e9.json'; path.write_bytes(b'authored'); path.chmod(0o600)
        with self.assertRaisesRegex(bundle.BundleError, 'bundle-portability'):
            self.seal()

    def test_in_place_manifest_overwrite_and_repo_locations_refuse(self):
        with self.assertRaisesRegex(bundle.BundleError, 'manifest-location'):
            bundle.seal(self.snapshot, self.snapshot / 'manifest.json', 'a' * 40)
        self.seal()
        with self.assertRaisesRegex(bundle.BundleError, 'manifest-exists'):
            self.seal()
        with self.assertRaisesRegex(bundle.BundleError, 'bundle-inside-repo'):
            bundle.inventory(Path(__file__).resolve().parents[1])

    def test_publicly_readable_account_files_refuse(self):
        (self.snapshot / 'report.json').chmod(0o644)
        with self.assertRaisesRegex(bundle.BundleError, 'bundle-permissions'):
            self.seal()

    def test_manifest_traversal_and_duplicate_entries_refuse(self):
        self.seal(); original = json.loads(self.manifest.read_bytes())
        for change in ('traversal', 'duplicate'):
            value = json.loads(json.dumps(original))
            if change == 'traversal':
                value['files'][0]['path'] = '../outside'
            else:
                value['files'].append(value['files'][0])
            self.manifest.write_text(json.dumps(value))
            pin = hashlib.sha256(self.manifest.read_bytes()).hexdigest()
            with self.subTest(change=change), self.assertRaisesRegex(bundle.BundleError, 'manifest-contract'):
                bundle.verify(self.snapshot, self.manifest, pin)

    def test_actual_cli_exits_nonzero_for_a_corrupt_copy_without_printing_contents(self):
        pin = self.seal(); (self.snapshot / 'report.json').write_text('authored secret control')
        result = subprocess.run([sys.executable, str(SPEC.origin), 'verify', '--snapshot', str(self.snapshot),
            '--manifest', str(self.manifest), '--expected-sha256', pin], capture_output=True, text=True, check=False)
        self.assertNotEqual(0, result.returncode)
        self.assertNotIn('authored secret control', result.stdout + result.stderr)
