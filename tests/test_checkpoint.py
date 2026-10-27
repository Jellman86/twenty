"""Process regressions: a successful command is insufficient validation evidence."""
import importlib.util
import json
import subprocess
import tempfile
import unittest
from pathlib import Path

SPEC = importlib.util.spec_from_file_location(
    "checkpoint", Path(__file__).resolve().parents[1] / "scripts/checkpoint.py")
checkpoint = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(checkpoint)


def trx(path, passed=1, skipped=0, failed=0):
    total = passed + skipped + failed
    results = ''.join(f'<UnitTestResult outcome="{outcome}" />'
                      for outcome, count in [("Passed", passed), ("NotExecuted", skipped), ("Failed", failed)]
                      for _ in range(count))
    path.write_text(f'''<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
      <Results>{results}</Results><ResultSummary><Counters total="{total}"
      executed="{passed + failed}" passed="{passed}" failed="{failed}"
      notExecuted="{skipped}" /></ResultSummary></TestRun>''')


class CheckpointTests(unittest.TestCase):
    def test_zero_match_success_and_missing_results_are_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'empty.trx'
            trx(path, passed=0)
            with self.assertRaisesRegex(checkpoint.CheckpointError, 'test-count'):
                checkpoint.trx_counts([path], minimum=1, allowed_skips=0)
            with self.assertRaisesRegex(checkpoint.CheckpointError, 'test-results-missing'):
                checkpoint.trx_counts([], minimum=1, allowed_skips=0)

    def test_skips_and_minimum_are_explicit_and_never_count_as_passes(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'results.trx'
            trx(path, passed=2, skipped=1)
            with self.assertRaisesRegex(checkpoint.CheckpointError, 'test-skipped'):
                checkpoint.trx_counts([path], minimum=2, allowed_skips=0)
            with self.assertRaisesRegex(checkpoint.CheckpointError, 'test-count'):
                checkpoint.trx_counts([path], minimum=3, allowed_skips=1)
            self.assertEqual({'total': 3, 'passed': 2, 'skipped': 1},
                             checkpoint.trx_counts([path], minimum=2, allowed_skips=1))

    def test_failed_malformed_and_inconsistent_results_are_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'results.trx'
            trx(path, failed=1)
            with self.assertRaisesRegex(checkpoint.CheckpointError, 'test-failed'):
                checkpoint.trx_counts([path], minimum=1, allowed_skips=0)
            path.write_text('<broken>')
            with self.assertRaisesRegex(checkpoint.CheckpointError, 'test-results-invalid'):
                checkpoint.trx_counts([path], minimum=1, allowed_skips=0)
            trx(path)
            path.write_text(path.read_text().replace('total="1"', 'total="20"'))
            with self.assertRaisesRegex(checkpoint.CheckpointError, 'test-results-invalid'):
                checkpoint.trx_counts([path], minimum=1, allowed_skips=0)

    def test_capability_and_deployed_revision_mismatch_fail_before_claiming_alignment(self):
        actual = {'meta': {'commit': 'a' * 40}, 'capabilities': {'method': 'old-v1'}}
        checkpoint.check_api(actual, 'a' * 40, ['capabilities.method="old-v1"'])
        for sha, checks in [('b' * 40, []), ('a' * 40, ['capabilities.method="new-v2"']),
                            ('a' * 40, ['capabilities.missing=true'])]:
            with self.subTest(sha=sha, checks=checks), self.assertRaises(checkpoint.CheckpointError):
                checkpoint.check_api(actual, sha, checks)

    def test_evidence_directory_cannot_overwrite_old_results_or_enter_repo(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / 'repo'; root.mkdir()
            with self.assertRaisesRegex(checkpoint.CheckpointError, 'evidence-inside-repo'):
                checkpoint.new_output(root, root / 'runs')
            outside = Path(directory) / 'evidence'
            checkpoint.new_output(root, outside)
            with self.assertRaisesRegex(checkpoint.CheckpointError, 'evidence-exists'):
                checkpoint.new_output(root, outside)

    def test_baseline_refuses_wrong_host_revision_or_branch(self):
        source = {'commit': 'a' * 40, 'branch': 'dev'}
        checkpoint.check_baseline(source, 'a' * 40, 'dev')
        for sha, branch in [('b' * 40, 'dev'), ('a' * 40, 'main')]:
            with self.assertRaises(checkpoint.CheckpointError):
                checkpoint.check_baseline(source, sha, branch)

    def test_source_identity_catches_edits_untracked_files_and_deletions(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            subprocess.run(['git', 'init', '-q', '-b', 'dev', str(root)], check=True)
            file = root / 'contract.cs'; file.write_text('version one')
            subprocess.run(['git', 'add', '.'], cwd=root, check=True)
            subprocess.run(['git', '-c', 'user.name=Authored test', '-c', 'user.email=test@example.invalid',
                            '-c', 'core.hooksPath=/dev/null', '-c', 'commit.gpgsign=false',
                            'commit', '-qm', 'fixture'], cwd=root, check=True)
            original = checkpoint.source_state(root)['fingerprint']
            file.write_text('version two')
            self.assertNotEqual(original, checkpoint.source_state(root)['fingerprint'])
            file.write_text('version one'); self.assertEqual(original, checkpoint.source_state(root)['fingerprint'])
            (root / 'new-policy.cs').write_text('unregistered change')
            self.assertNotEqual(original, checkpoint.source_state(root)['fingerprint'])
            (root / 'new-policy.cs').unlink(); file.unlink()
            self.assertNotEqual(original, checkpoint.source_state(root)['fingerprint'])

    def test_verify_rejects_stale_source_and_tampered_trx(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); evidence = root / 'evidence'; evidence.mkdir()
            path = evidence / 'results.trx'; trx(path)
            source = {'fingerprint': 'current'}
            before = {'schema': 'twenty-preflight-v1', 'status': 'passed', 'source': source}
            receipt = {'schema': 'twenty-tests-v1', 'status': 'passed', 'source': source,
                       'minimumPassed': 1, 'allowedSkips': 0, 'counts': {'total': 1, 'passed': 1, 'skipped': 0},
                       'artifacts': [{'path': 'results.trx', 'sha256': checkpoint.sha256(path)}]}
            preflight = root / 'preflight.json'; preflight.write_text(json.dumps(before))
            test_receipt = evidence / 'tests.json'; test_receipt.write_text(json.dumps(receipt))
            checkpoint.verify_receipts(source, preflight, test_receipt)
            with self.assertRaisesRegex(checkpoint.CheckpointError, 'source-changed'):
                checkpoint.verify_receipts({'fingerprint': 'other'}, preflight, test_receipt)
            path.write_text(path.read_text() + '\n')
            with self.assertRaisesRegex(checkpoint.CheckpointError, 'artifact-changed'):
                checkpoint.verify_receipts(source, preflight, test_receipt)

    def test_manifest_cannot_read_files_outside_its_evidence_directory(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); evidence = root / 'evidence'; evidence.mkdir()
            outside = root / 'outside.trx'; trx(outside)
            source = {'fingerprint': 'current'}
            preflight = root / 'preflight.json'
            preflight.write_text(json.dumps({'schema': 'twenty-preflight-v1', 'status': 'passed', 'source': source}))
            receipt = evidence / 'tests.json'
            receipt.write_text(json.dumps({'schema': 'twenty-tests-v1', 'status': 'passed', 'source': source,
                'artifacts': [{'path': '../outside.trx', 'sha256': checkpoint.sha256(outside)}]}))
            with self.assertRaisesRegex(checkpoint.CheckpointError, 'artifact-path'):
                checkpoint.verify_receipts(source, preflight, receipt)
