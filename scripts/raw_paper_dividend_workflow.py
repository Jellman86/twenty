"""Private authored HTTP/browser/restart acceptance for cash-dividends-v1.

Requires an exported current-build fixture directory containing create.json and
workspace/. Never targets production: copies the fixture and launches its own
loopback-only API with authentication/collector off and broker variables removed.
Run with: uv run --no-project --with playwright python <script> --fixture <dir> --out <new dir>
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import time
from decimal import Decimal
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen

from playwright.sync_api import sync_playwright


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def source(repo):
    files = subprocess.check_output(['git', 'ls-files', '--cached', '--others', '--exclude-standard'], cwd=repo, text=True).splitlines()
    files += [str(p.relative_to(repo)) for folder in ('src/Twenty.Api/wwwroot', 'src/Twenty.Api/bin/Release/net10.0')
              for p in (repo / folder).rglob('*') if p.is_file()]
    return {name: digest(repo / name) for name in sorted(set(files)) if (repo / name).is_file()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--fixture', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    out = args.out.resolve()
    out.mkdir(parents=True, exist_ok=False, mode=0o700)
    workspace = out / 'workspace'
    shutil.copytree(args.fixture / 'workspace', workspace)
    create = json.loads((args.fixture / 'create.json').read_bytes())
    before = source(repo)
    (out / 'source-before.json').write_text(json.dumps(before, indent=2) + '\n')
    checks = []
    server = None
    log = None
    base = None
    completed = False

    def check(name, condition):
        checks.append({'name': name, 'passed': bool(condition)})
        if not condition:
            raise AssertionError(name)

    def http(path, body=None):
        data = None if body is None else json.dumps(body).encode()
        req = Request(base + path, data=data, headers={'Content-Type': 'application/json', 'X-Twenty-Request': '1'})
        try:
            with urlopen(req, timeout=120) as response:
                return json.loads(response.read())
        except HTTPError as error:
            (out / 'http-error.json').write_text(json.dumps({'path': path, 'status': error.code, 'body': error.read().decode()}, indent=2) + '\n')
            raise

    def stop():
        if server is not None:
            server.terminate()
            try:
                server.wait(timeout=15)
            except subprocess.TimeoutExpired:
                server.kill()
                server.wait()
        if log is not None:
            log.close()

    def start(label):
        nonlocal server, log, base
        env = {k: v for k, v in os.environ.items() if not k.startswith(('TWENTY_', 'ASPNETCORE_'))}
        env.update(TWENTY_DATA_ROOT=str(workspace), TWENTY_AUTH='off', TWENTY_COLLECTOR_ROLE='off', ASPNETCORE_URLS='http://127.0.0.1:0')
        path = out / f'api-{label}.log'
        log = path.open('w')
        server = subprocess.Popen(['dotnet', str(repo / 'src/Twenty.Api/bin/Release/net10.0/Twenty.Api.dll')],
                                  cwd=repo / 'src/Twenty.Api', env=env, stdout=log, stderr=subprocess.STDOUT)
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            match = re.search(r'Now listening on: (http://127\.0\.0\.1:\d+)', path.read_text())
            if match:
                base = match.group(1)
                return
            if server.poll() is not None:
                raise RuntimeError('Owned API exited; inspect private log')
            time.sleep(.1)
        raise RuntimeError('Owned API did not start')

    try:
        start('first')
        initial = {path: http(path) for path in ('/api/research/raw-studies', '/api/research/raw-trials')}
        reports = {str(p.relative_to(workspace)): digest(p) for p in (workspace / 'runs').rglob('*') if p.is_file()}
        check('fixture has no paper accounts', http('/api/research/raw-paper-accounts')['accounts'] == [])
        account = http('/api/research/raw-paper-accounts', create)
        path = '/api/research/raw-paper-accounts/' + account['id']
        (out / 'created.json').write_text(json.dumps(account, indent=2) + '\n')
        # Hide source study reports: preparation must use its own pinned originals.
        shutil.move(workspace / 'runs', workspace / 'runs-hidden')
        preview = http(path + '/prepare', {'expectedReceiptSha256': None})
        check('preview uses payment-time FX, net withholding and one account fee',
              Decimal(str(preview['projectedBalance']['cashGbp'])) == Decimal('.24921875')
              and Decimal(str(preview['projectedBalance']['equityGbp'])) == Decimal('19.99921875'))
        check('preview admits no operation or accepted capital', http(path + '/operations') == []
              and http('/api/research/raw-paper-accounts')['accounts'][0]['balance']['contributionsGbp'] == 0)
        first_request = {'requestId': 'ae185d76-350e-455d-a38f-475578756134', 'expectedReceiptSha256': None, 'replaySha256': preview['replaySha256']}
        first = http(path + '/advance', first_request)
        check('explicit first apply succeeded', first['status'] == 'succeeded')
        shutil.move(workspace / 'runs-hidden', workspace / 'runs')
        with sync_playwright() as playwright:
            browser = playwright.chromium.launch(headless=True)
            try:
                for width in (1280, 390):
                    page = browser.new_page(viewport={'width': width, 'height': 960})
                    writes, receipts, errors = [], [], []
                    page.on('request', lambda r, saved=writes: saved.append(r.url) if r.method != 'GET' else None)
                    page.on('request', lambda r, saved=receipts: saved.append(r.url) if '/receipt' in r.url else None)
                    page.on('pageerror', lambda e, saved=errors: saved.append(str(e)))
                    page.goto(base + '/#paper')
                    page.get_by_role('heading', name='Paper accounts', exact=True).wait_for()
                    page.get_by_text('£20.00', exact=True).first.wait_for()
                    content = page.locator('main').inner_text()
                    page.screenshot(path=str(out / f'paper-dividend-{width}.png'), full_page=True)
                    check(f'{width}: dividend mode has a plain operational label', 'Cash dividends' in content and 'Unsupported mode' not in content)
                    check(f'{width}: dividend account can prepare its next batch', page.get_by_role('button', name='Prepare next batch', exact=True).count() == 1)
                    check(f'{width}: page load does not write or fetch receipts', not writes and not receipts and not errors)
                    check(f'{width}: no horizontal overflow', page.evaluate('document.documentElement.scrollWidth <= innerWidth'))
                    if width == 390:
                        page.get_by_role('button', name='Prepare next batch', exact=True).click()
                        page.get_by_role('heading', name='Projected batch 2 · Not yet applied', exact=True).wait_for(timeout=120000)
                        check('browser preparation keeps accepted state at one period', http('/api/research/raw-paper-accounts')['accounts'][0]['acceptedPeriods'] == 1)
                        page.screenshot(path=str(out / 'paper-dividend-prepared-390.png'), full_page=True)
                        with page.expect_response(lambda r: r.request.method == 'POST' and r.url.endswith(path + '/advance'), timeout=120000) as response:
                            page.get_by_role('button', name='Apply batch', exact=True).click()
                        second = response.value.json()
                        (out / 'browser-apply-response.json').write_text(json.dumps({'status': response.value.status, 'body': second}, indent=2) + '\n')
                        second_request = json.loads(response.value.request.post_data)
                        check('browser applies the prepared batch once', response.value.status == 200 and second.get('status') == 'succeeded')
                        page.get_by_text('£39.75', exact=True).first.wait_for()
                    page.close()
            finally:
                browser.close()
        current = http('/api/research/raw-paper-accounts')['accounts'][0]
        check('second period reinvests only settled income', current['acceptedPeriods'] == 2
              and Decimal(str(current['balance']['equityGbp'])) == Decimal('39.74921875')
              and Decimal(str(current['balance']['feesGbp'])) == Decimal('.51'))
        receipt_path = workspace / 'raw-paper-operations' / (second['id'] + '.receipt.json')
        receipt = receipt_path.read_bytes()
        accepted = json.dumps(current, sort_keys=True)
        stop()
        shutil.move(workspace / 'raw-paper-inputs', workspace / 'inputs-hidden')
        start('cold')
        check('cold UUID replay works without originals', http(path + '/advance', second_request) == second)
        check('cold restart preserves accepted state and receipt bytes', json.dumps(http('/api/research/raw-paper-accounts')['accounts'][0], sort_keys=True) == accepted
              and receipt_path.read_bytes() == receipt)
        check('source studies and trial rows unchanged', all(http(p) == value for p, value in initial.items()))
        check('all original source report bytes preserved', reports == {str(p.relative_to(workspace)): digest(p) for p in (workspace / 'runs').rglob('*') if p.is_file()})
        shutil.move(workspace / 'inputs-hidden', workspace / 'raw-paper-inputs')
        completed = True
    finally:
        stop()
        after = source(repo)
        (out / 'source-after.json').write_text(json.dumps(after, indent=2) + '\n')
        checks.append({'name': 'complete workflow reached cold-retry and preservation checks', 'passed': completed})
        checks.append({'name': 'source and runtime artifacts unchanged during workflow', 'passed': before == after})
        result = {'passed': sum(c['passed'] for c in checks), 'failed': sum(not c['passed'] for c in checks), 'checks': checks,
                  'authoredFinancialInputs': True, 'realLocalApiAndBrowser': True, 'genuineBrokerIntegration': False,
                  'productionTouched': False, 'sourceManifestSha256': hashlib.sha256(json.dumps(before, sort_keys=True).encode()).hexdigest()}
        (out / 'verification.json').write_text(json.dumps(result, indent=2) + '\n')
        print(json.dumps({k: result[k] for k in ('passed', 'failed', 'productionTouched')}))
    if not all(c['passed'] for c in checks):
        raise SystemExit(1)


if __name__ == '__main__':
    main()
