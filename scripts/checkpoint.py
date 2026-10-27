"""Bind source preflight and non-vacuous .NET runs to private checkpoint evidence.

This checks provenance and execution counts, not the scientific meaning of a test.
No application write, credential read, fetch, deployment or host change occurs.
"""
import argparse
import hashlib
import json
import os
import platform
import subprocess
import sys
import xml.etree.ElementTree as ET
from datetime import UTC, datetime
from pathlib import Path
from urllib.parse import urlsplit
from urllib.request import urlopen


class CheckpointError(Exception):
    pass


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def git(root, *args):
    return subprocess.check_output(['git', *args], cwd=root)


def source_state(root):
    names = sorted(set(git(root, 'ls-files', '-z', '--cached', '--others', '--exclude-standard').split(b'\0')) - {b''})
    digest = hashlib.sha256(); size = 0
    for name in names:
        path = root / os.fsdecode(name)
        if path.is_symlink():
            content = b'link:' + os.fsencode(os.readlink(path))
        elif not path.exists():
            content = b'deleted'
        elif path.is_file():
            length = path.stat().st_size; size += length
            if length > 8 * 1024 * 1024 or size > 64 * 1024 * 1024:
                raise CheckpointError('source-size: keep generated artifacts outside the repository')
            content = str(path.stat().st_mode & 0o111).encode() + b':' + path.read_bytes()
        else:
            raise CheckpointError('source-kind: unsupported tracked directory/submodule')
        digest.update(len(name).to_bytes(8, 'big') + name + hashlib.sha256(content).digest())
    branch = subprocess.run(['git', 'symbolic-ref', '--short', '-q', 'HEAD'], check=False, cwd=root,
                            capture_output=True, text=True).stdout.strip() or 'detached'
    return {'commit': git(root, 'rev-parse', 'HEAD').decode().strip(), 'branch': branch,
            'dirty': bool(git(root, 'status', '--porcelain')), 'files': len(names),
            'fingerprint': digest.hexdigest(), 'scheme': 'git-visible-files-v1'}


def check_baseline(source, expected_head, expected_branch):
    if source['commit'] != expected_head:
        raise CheckpointError('head-mismatch')
    if source['branch'] != expected_branch:
        raise CheckpointError('branch-mismatch')


def check_api(actual, expected_commit, expectations):
    if actual.get('meta', {}).get('commit') != expected_commit:
        raise CheckpointError('deployed-commit-mismatch')
    for expectation in expectations:
        try:
            name, encoded = expectation.split('=', 1); expected = json.loads(encoded)
            value = actual
            for key in name.split('.'):
                value = value[key]
        except (ValueError, KeyError, TypeError):
            raise CheckpointError('api-expectation-invalid-or-missing') from None
        # bool and int must not alias under Python equality.
        if type(value) is not type(expected) or value != expected:
            raise CheckpointError('api-contract-mismatch: ' + name)


def new_output(root, output):
    output = output.resolve()
    if output.is_relative_to(root.resolve()):
        raise CheckpointError('evidence-inside-repo')
    try:
        output.mkdir(mode=0o700, parents=True, exist_ok=False)
    except FileExistsError:
        raise CheckpointError('evidence-exists: choose a new attempt directory') from None
    return output


def trx_counts(paths, minimum, allowed_skips):
    if not paths:
        raise CheckpointError('test-results-missing')
    totals = {'total': 0, 'passed': 0, 'skipped': 0}
    for path in paths:
        try:
            if path.stat().st_size > 32 * 1024 * 1024:
                raise ValueError()
            root = ET.parse(path).getroot(); counters = root.findall('.//{*}Counters')
            if len(counters) != 1:
                raise ValueError()
            count = counters[0].attrib
            total, executed, passed, failed, skipped = [int(count[k]) for k in
                ('total', 'executed', 'passed', 'failed', 'notExecuted')]
            outcomes = [r.get('outcome') for r in root.findall('.//{*}UnitTestResult')]
            if (min(total, executed, passed, failed, skipped) < 0 or executed != passed + failed or
                total != passed + failed + skipped or len(outcomes) != total or
                outcomes.count('Passed') != passed or outcomes.count('NotExecuted') != skipped or
                outcomes.count('Failed') != failed):
                raise ValueError()
        except (OSError, ET.ParseError, ValueError, KeyError):
            raise CheckpointError('test-results-invalid') from None
        if failed:
            raise CheckpointError('test-failed')
        totals['total'] += total; totals['passed'] += passed; totals['skipped'] += skipped
    if totals['passed'] < minimum:
        raise CheckpointError('test-count: fewer passing tests than required')
    if totals['skipped'] > allowed_skips:
        raise CheckpointError('test-skipped: exceeds the declared allowance')
    return totals


def read_json(path):
    try:
        if path.stat().st_size > 16 * 1024 * 1024:
            raise ValueError()
        return json.loads(path.read_bytes())
    except (OSError, ValueError):
        raise CheckpointError('evidence-json-invalid') from None


def verify_receipts(current, preflight_path, tests_path):
    before, tests = read_json(preflight_path), read_json(tests_path)
    if (before.get('schema') != 'twenty-preflight-v1' or tests.get('schema') != 'twenty-tests-v1' or
        before.get('status') != 'passed' or tests.get('status') != 'passed'):
        raise CheckpointError('evidence-not-passed')
    if any(r.get('source', {}).get('fingerprint') != current['fingerprint'] for r in (before, tests)):
        raise CheckpointError('source-changed: regenerate preflight and rerun affected checks')
    directory = tests_path.parent.resolve(); trxs = []; seen = set()
    for item in tests.get('artifacts', []):
        name = item['path']; path = (directory / name).resolve()
        if Path(name).is_absolute() or '..' in Path(name).parts or not path.is_relative_to(directory) or name in seen:
            raise CheckpointError('artifact-path')
        seen.add(name)
        if not path.is_file() or sha256(path) != item['sha256']:
            raise CheckpointError('artifact-changed')
        if path.suffix == '.trx':
            trxs.append(path)
    counts = trx_counts(trxs, tests['minimumPassed'], tests['allowedSkips'])
    if counts != tests.get('counts'):
        raise CheckpointError('test-count-binding')
    return {'scope': 'source-and-test-evidence-only', 'source': current, 'counts': counts,
            'preflightSha256': sha256(preflight_path), 'testReceiptSha256': sha256(tests_path)}


def versions():
    result = {'python': platform.python_version(), 'os': platform.system(), 'architecture': platform.machine()}
    for tool in ('dotnet', 'node', 'uv'):
        try:
            value = subprocess.run([tool, '--version'], check=False, text=True, capture_output=True, timeout=10)
            result[tool] = value.stdout.strip()[:300] if value.returncode == 0 else 'probe-failed'
        except (OSError, subprocess.TimeoutExpired):
            result[tool] = 'unavailable'
    return result


def preflight(args, root):
    result = {'schema': 'twenty-preflight-v1', 'source': source_state(root), 'runtime': versions()}
    check_baseline(result['source'], args.expected_head, args.expected_branch)
    if args.api_base:
        parsed = urlsplit(args.api_base)
        if (parsed.scheme not in ('http', 'https') or not parsed.hostname or parsed.username or parsed.password or
            parsed.query or parsed.fragment or parsed.path not in ('', '/') or not args.expected_deployed):
            raise CheckpointError('api-base-invalid: use a host URL and explicit expected deployment commit')
        actual = {}
        for key, path in [('meta', '/api/meta'), ('capabilities', '/api/research/capabilities')]:
            with urlopen(args.api_base.rstrip('/') + path, timeout=10) as response:
                raw = response.read(1024 * 1024 + 1)
                if len(raw) > 1024 * 1024:
                    raise CheckpointError('api-response-size')
                actual[key] = json.loads(raw)
        check_api(actual, args.expected_deployed, args.expect_api)
        result['api'] = actual
    elif args.expected_deployed or args.expect_api:
        raise CheckpointError('api-base-required')
    return result


def dotnet(args, root, output):
    source = source_state(root); project = (root / args.project).resolve()
    if not project.is_relative_to(root) or not project.is_file() or project.suffix not in ('.csproj', '.slnx', '.sln'):
        raise CheckpointError('test-project-invalid')
    command = ['dotnet', 'test', str(project), '-c', 'Release', '-p:RestoreLockedMode=true',
               '--logger', 'trx', '--results-directory', str(output / 'results')]
    if args.filter:
        command.extend(['--filter', args.filter])
    result = {'schema': 'twenty-tests-v1', 'source': source, 'command': command,
              'minimumPassed': args.min_passed, 'allowedSkips': args.allow_skipped,
              'startedAt': datetime.now(UTC).isoformat(), 'status': 'failed'}
    with (output / 'dotnet.log').open('w') as log:
        try:
            result['exitCode'] = subprocess.run(command, check=False, cwd=root, stdout=log, stderr=subprocess.STDOUT,
                                               timeout=args.timeout_seconds).returncode
        except subprocess.TimeoutExpired:
            result['exitCode'] = -1; result['error'] = 'test-timeout'
        except OSError:
            result['exitCode'] = -1; result['error'] = 'test-tool-unavailable'
    result['finishedAt'] = datetime.now(UTC).isoformat()
    paths = sorted((output / 'results').glob('*.trx'))
    result['artifacts'] = [{'path': str(p.relative_to(output)), 'sha256': sha256(p)}
                           for p in [output / 'dotnet.log', *paths]]
    try:
        if result['exitCode'] != 0:
            raise CheckpointError(result.get('error', 'test-command-failed'))
        if source_state(root) != source:
            raise CheckpointError('source-changed-during-test')
        result['counts'] = trx_counts(paths, args.min_passed, args.allow_skipped)
        result['status'] = 'passed'
    except CheckpointError as error:
        result['error'] = str(error)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[1])
    sub = parser.add_subparsers(dest='action', required=True)
    pre = sub.add_parser('preflight'); pre.add_argument('--expected-head', required=True)
    pre.add_argument('--expected-branch', default='dev'); pre.add_argument('--api-base')
    pre.add_argument('--expected-deployed'); pre.add_argument('--expect-api', action='append', default=[])
    test = sub.add_parser('dotnet'); test.add_argument('--project', default='Twenty.slnx')
    test.add_argument('--filter'); test.add_argument('--min-passed', type=int, default=1)
    test.add_argument('--allow-skipped', type=int, default=0)
    test.add_argument('--timeout-seconds', type=int, default=1800)
    verify = sub.add_parser('verify'); verify.add_argument('--preflight', required=True, type=Path)
    verify.add_argument('--tests', required=True, type=Path)
    for command in (pre, test, verify):
        command.add_argument('--output-dir', required=True, type=Path)
    args = parser.parse_args(); root = args.root.resolve(); os.umask(0o077)
    if args.action == 'dotnet' and (args.min_passed < 1 or args.allow_skipped < 0 or args.timeout_seconds < 1):
        parser.error('minimum passing count and timeout must be positive; skipped allowance must be nonnegative')
    output = new_output(root, args.output_dir)
    try:
        if args.action == 'preflight':
            result = preflight(args, root)
        elif args.action == 'dotnet':
            result = dotnet(args, root, output)
        else:
            result = verify_receipts(source_state(root), args.preflight, args.tests)
        result.setdefault('status', 'passed')
    except (CheckpointError, OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError) as error:
        result = {'status': 'failed', 'error': str(error)}
    name = {'dotnet': 'tests', 'preflight': 'preflight', 'verify': 'verification'}[args.action]
    (output / (name + '.json')).write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps({'status': result['status'], 'counts': result.get('counts'),
                      'error': result.get('error'), 'evidence': str(output)}))
    return 0 if result['status'] == 'passed' else 1


if __name__ == '__main__':
    try:
        sys.exit(main())
    except CheckpointError as error:
        print(str(error), file=sys.stderr); sys.exit(1)
