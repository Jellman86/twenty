"""Authored pending-dividend API/UI/cold-retry check in a copied loopback workspace.

Export a current-build fixture with create-pending.json and workspace/. Run with
uv run --no-project --with playwright python <script> --fixture <dir> --out <new dir>.
This owns its API process, removes broker configuration and never targets production.
"""
from __future__ import annotations

import argparse
import base64
import json
import os
import re
import shutil
import sqlite3
import subprocess
import time
from concurrent.futures import ThreadPoolExecutor
from decimal import Decimal
from http.client import HTTPException
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

from playwright.sync_api import sync_playwright
from raw_paper_dividend_workflow import digest, source


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--fixture', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    parser.add_argument('--kill-before-commit', action='store_true', help='Kill the owned API after writing the payment receipt but before its success commit')
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    out = args.out.resolve()
    out.mkdir(parents=True, exist_ok=False, mode=0o700)
    workspace = out / 'workspace'
    shutil.copytree(args.fixture / 'workspace', workspace)
    create_bytes = (args.fixture / 'create-pending.json').read_bytes()
    create = json.loads(create_bytes, parse_float=Decimal)
    before = source(repo)
    (out / 'source-before.json').write_text(json.dumps(before, indent=2) + '\n')
    checks = []
    server = log = base = None
    completed = False

    def save(name, value):
        (out / name).write_text(json.dumps(value, indent=2, default=str) + '\n')

    def check(name, condition):
        checks.append({'name': name, 'passed': bool(condition)})
        if not condition:
            raise AssertionError(name)

    def http(path, body=None, *, exact=True):
        data = body if isinstance(body, bytes) else None if body is None else json.dumps(body).encode()
        request = Request(base + path, data=data, headers={'Content-Type': 'application/json', 'X-Twenty-Request': '1'})
        try:
            with urlopen(request, timeout=120) as response:
                return json.loads(response.read(), parse_float=Decimal if exact else float)
        except HTTPError as error:
            save('http-error.json', {'path': path, 'status': error.code, 'body': error.read().decode()})
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

    def restore(hidden, name):
        destination = workspace / name
        if destination.exists():
            # Startup may recreate an empty root. Never merge or remove newly created data.
            if not destination.is_dir() or any(destination.iterdir()):
                raise RuntimeError('Refuse to restore over a nonempty workspace directory')
            destination.rmdir()
        shutil.move(workspace / hidden, destination)

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
        account = http('/api/research/raw-paper-accounts', create_bytes)
        path = '/api/research/raw-paper-accounts/' + account['id']
        check('new version freezes pending-dividend policy', account['version'] == 'raw-paper-dividend-claim-batches-v1'
              and account['policy']['dividends'][0]['markPolicy'] == create['dividends'][0]['markPolicy'])
        save('created.json', account)
        shutil.move(workspace / 'runs', workspace / 'runs-hidden')
        preview = http(path + '/prepare', {'expectedReceiptSha256': None})
        balance = preview['projectedBalance']
        check('first mark keeps cash/holdings separate from expected income', balance['cashGbp'] == 0 and balance['equityGbp'] == Decimal('19.75')
              and balance['dividendClaims']['netExpectedValueGbp'] == Decimal('.28625')
              and balance['dividendClaims']['expectedEquityGbp'] == Decimal('20.03625'))
        check('estimated fees do not become actual fees', balance['feesGbp'] == Decimal('.25')
              and balance['dividendClaims']['expectedFeesGbp'] == Decimal('.01'))
        check('preparation admits no operation or capital', http(path + '/operations') == []
              and http('/api/research/raw-paper-accounts')['accounts'][0]['balance']['contributionsGbp'] == 0)
        first_request = {'requestId': '294f891e-cfe6-4e3f-bf43-a0439cd786e1', 'expectedReceiptSha256': None, 'replaySha256': preview['replaySha256']}
        first = http(path + '/advance', first_request)
        check('first explicit apply accepts the pending mark', first['status'] == 'succeeded')
        if args.kill_before_commit:
            accepted = http('/api/research/raw-paper-accounts')['accounts'][0]
            pending = http(path + '/prepare', {'expectedReceiptSha256': first['receiptSha256']})
            request = {'requestId': '5f8f7ec5-03e3-4712-a637-b364df477da3', 'expectedReceiptSha256': first['receiptSha256'], 'replaySha256': pending['replaySha256']}
            first_receipt = (workspace / 'raw-paper-operations' / (first['id'] + '.receipt.json')).read_bytes()
            frozen_inputs = {str(p.relative_to(workspace / 'raw-paper-inputs')): digest(p) for p in (workspace / 'raw-paper-inputs').rglob('*') if p.is_file()}
            db = sqlite3.connect(workspace / 'research.db', timeout=.001)
            with ThreadPoolExecutor(max_workers=1) as pool:
                future = pool.submit(http, path + '/advance', request)
                try:
                    deadline = time.monotonic() + 10
                    while time.monotonic() < deadline:
                        row = db.execute('SELECT json FROM raw_paper_operations WHERE request_id=?', (request['requestId'],)).fetchone()
                        if row and json.loads(row[0])['status'] == 'running':
                            try:
                                db.execute('BEGIN IMMEDIATE')
                            except sqlite3.OperationalError:
                                time.sleep(.001)
                                continue
                            row = db.execute('SELECT json FROM raw_paper_operations WHERE request_id=?', (request['requestId'],)).fetchone()
                            running = json.loads(row[0])
                            check('writer lock follows durable running admission', running['status'] == 'running')
                            break
                        if future.done():
                            raise RuntimeError('Advance finished before the fault window; no kill proof claimed')
                        time.sleep(.001)
                    else:
                        raise RuntimeError('No durable running operation observed')
                    receipt_path = workspace / 'raw-paper-operations' / (running['id'] + '.receipt.json')
                    deadline = time.monotonic() + 10
                    while not receipt_path.exists() and time.monotonic() < deadline:
                        time.sleep(.001)
                    check('complete immutable receipt exists before success commit', receipt_path.is_file())
                    orphan = receipt_path.read_bytes()
                    proposed = json.loads(orphan)
                    result = json.loads(base64.b64decode(proposed['resultBase64'], validate=True), parse_float=Decimal)
                    check('uncommitted receipt contains the payment and second deposit', proposed['requestSha256'] == pending['replaySha256']
                          and result['cashGbp'] == Decimal('.24921875') and result['contributionsGbp'] == 40
                          and sum(e['kind'] == 'dividend-payment' for e in result['entries']) == 1)
                    row = db.execute('SELECT json FROM raw_paper_accounts WHERE id=?', (account['id'],)).fetchone()
                    not_committed = json.loads(row[0], parse_float=Decimal)
                    check('success is blocked with earlier accepted balance intact', not_committed['acceptedPeriods'] == 1
                          and not_committed['balance'] == accepted['balance'])
                    server.kill()
                    check('owned native API was killed at the blocked commit', server.wait(timeout=15) == -9)
                finally:
                    db.rollback()
                    db.close()
                disconnected = False
                try:
                    future.result(timeout=15)
                except (HTTPException, HTTPError, URLError, OSError, TimeoutError):
                    disconnected = True
                check('in-flight advance connection ended with the process', disconnected)
            stop()
            shutil.move(workspace / 'raw-paper-inputs', workspace / 'inputs-hidden')
            start('after-kill')
            restored = http('/api/research/raw-paper-accounts')['accounts'][0]
            retry = http(path + '/advance', request)
            check('restart retains the interrupted attempt and refuses receipt adoption', restored['status'] == 'failed' and restored['attempts'] == 2
                  and restored['acceptedPeriods'] == 1 and restored['balance'] == accepted['balance']
                  and retry['status'] == 'interrupted' and retry['errorCode'] == 'process-restarted' and retry.get('receiptSha256') is None)
            check('uncommitted payment cannot become cash or settle the accepted claim', restored['balance']['cashGbp'] == 0
                  and restored['balance']['contributionsGbp'] == 20 and restored['balance']['dividendClaims']['count'] == 1
                  and restored['balance']['dividendClaims']['netExpectedValueGbp'] == Decimal('.28625'))
            check('repeated UUID retry admits no replacement attempt', http(path + '/advance', request) == retry and len(http(path + '/operations')) == 2)
            check('accepted and orphan receipt bytes remain exact', first_receipt == (workspace / 'raw-paper-operations' / (first['id'] + '.receipt.json')).read_bytes()
                  and orphan == receipt_path.read_bytes())
            check('source study and trial register unchanged after kill', all(http(p) == value for p, value in initial.items()))
            restore('inputs-hidden', 'raw-paper-inputs')
            restore('runs-hidden', 'runs')
            check('frozen inputs survive the killed process unchanged', frozen_inputs == {str(p.relative_to(workspace / 'raw-paper-inputs')): digest(p) for p in (workspace / 'raw-paper-inputs').rglob('*') if p.is_file()})
            check('source reports survive the killed process unchanged', reports == {str(p.relative_to(workspace)): digest(p) for p in (workspace / 'runs').rglob('*') if p.is_file()})
            save('fault-state.json', {'acceptedBefore': accepted, 'acceptedAfter': restored, 'interruptedOperation': retry, 'orphanReceiptSha256': digest(receipt_path)})
            completed = True
        else:
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
                        page.get_by_role('heading', name='Batches', exact=True).wait_for()
                        text = page.locator('main').inner_text()
                        (out / f'pending-{width}.txt').write_text(text)
                        page.screenshot(path=str(out / f'pending-{width}.png'), full_page=True)
                        check(f'{width}: UI recognises the frozen pending-dividend mode', 'Pending dividends' in text and 'Unsupported mode' not in text)
                        check(f'{width}: authored provenance stays visible', 'authored inputs' in text)
                        check(f'{width}: expected equity is labelled separately', page.get_by_text('Estimated equity', exact=True).count() > 0 and '£20.04' in text
                              and 'Holdings and cash' in text and '£19.75' in text)
                        check(f'{width}: receivable and fee estimate are disclosed', 'Estimated dividend receivable' in text
                              and 'Estimated dividend fees' in text and '£0.29' in text and '£0.01' in text)
                        check(f'{width}: page load is read-only and receipt-free', not writes and not receipts and not errors)
                        check(f'{width}: no horizontal overflow', page.evaluate('document.documentElement.scrollWidth <= innerWidth'))
                        if width == 390:
                            page.get_by_role('button', name='Prepare next batch', exact=True).click()
                            page.get_by_role('heading', name='Projected batch 2 · Not yet applied', exact=True).wait_for(timeout=120000)
                            check('browser preparation keeps one accepted period', http('/api/research/raw-paper-accounts')['accounts'][0]['acceptedPeriods'] == 1)
                            page.screenshot(path=str(out / 'pending-projected-390.png'), full_page=True)
                            with page.expect_response(lambda r: r.request.method == 'POST' and r.url.endswith(path + '/advance'), timeout=120000) as response:
                                page.get_by_role('button', name='Apply batch', exact=True).click()
                            second = json.loads(response.value.body(), parse_float=Decimal)
                            second_request = json.loads(response.value.request.post_data)
                            check('browser applies the prepared second batch', response.value.status == 200 and second['status'] == 'succeeded')
                            page.get_by_text('£39.75', exact=True).first.wait_for()
                        page.close()
                    restore('runs-hidden', 'runs')
                    page = browser.new_page(viewport={'width': 390, 'height': 960})
                    page.goto(base + '/#paper')
                    page.get_by_role('heading', name='Paper accounts', exact=True).wait_for()
                    page.get_by_text('New paper account', exact=True).click()
                    page.get_by_role('combobox', name='Costs').select_option('stress')
                    page.get_by_label('Dataset SHA-256', exact=True).fill(create['datasetSha256'])
                    page.get_by_label('Calendar pins (JSON)', exact=True).fill(json.dumps(create['calendars']))
                    page.get_by_label('Decision dates (JSON)', exact=True).fill(json.dumps(create['decisions']))
                    page.get_by_role('combobox', name=re.compile('^Corporate actions')).select_option('dividend-claims-v1')
                    term = create['dividends'][0]
                    values = {
                        'Listing ID': term['listingId'], 'Action ID': term['actionId'], 'Observation ID': term['observationId'],
                        'Entitlement time (UTC)': term['evidence']['entitlementAt'], 'Cutoff proof available (UTC)': term['evidence']['availableAt'],
                        'Cutoff source reference': term['evidence']['sourceReference'], 'Cutoff content SHA-256': term['evidence']['contentSha256'],
                        'Source payment ID': term['delivery']['sourcePaymentId'], 'Payment source reference': term['delivery']['sourceReference'],
                        'Payment content SHA-256': term['delivery']['contentSha256'], 'Paid time (UTC)': term['delivery']['paidAt'],
                        'Payment available (UTC)': term['delivery']['availableAt'], 'Payment currency': term['delivery']['currency'],
                        'Gross per cutoff share': str(term['delivery']['grossPerShare']), 'Withheld per cutoff share': str(term['delivery']['withheldPerShare']),
                        'Fixed fee per entitled account (GBP)': str(term['delivery']['fixedFeeGbp']), 'Payment FX observation ID': term['delivery']['fxObservationId'] or '',
                        'Expected withholding per cutoff share': str(term['markPolicy']['expectedWithheldPerShare']),
                        'Expected fee per entitled account (GBP)': str(term['markPolicy']['expectedFeeGbp']),
                        'Expected policy available (UTC)': term['markPolicy']['availableAt'], 'Expected policy source reference': term['markPolicy']['sourceReference'],
                        'Expected policy content SHA-256': term['markPolicy']['contentSha256'],
                    }
                    for label, value in values.items():
                        page.get_by_label(label, exact=True).fill(value)
                    creation_posts = []
                    page.on('request', lambda r: creation_posts.append(r.post_data) if r.method == 'POST' and r.url.endswith('/api/research/raw-paper-accounts') else None)
                    page.get_by_label('Expected withholding per cutoff share', exact=True).fill('3')
                    page.get_by_role('button', name='Create account', exact=True).click()
                    page.get_by_role('alert').filter(has_text='Expected withholding per cutoff share cannot exceed gross.').wait_for()
                    check('invalid expected deductions admit no HTTP request or account', not creation_posts
                          and len(http('/api/research/raw-paper-accounts')['accounts']) == 1)
                    page.get_by_label('Expected withholding per cutoff share', exact=True).fill(values['Expected withholding per cutoff share'])
                    check('new claim form fits mobile width', page.evaluate('document.documentElement.scrollWidth <= innerWidth'))
                    page.screenshot(path=str(out / 'pending-authoring-390.png'), full_page=True)
                    with page.expect_response(lambda r: r.request.method == 'POST' and r.url.endswith('/api/research/raw-paper-accounts'), timeout=120000) as response:
                        page.get_by_role('button', name='Create account', exact=True).press('Enter')
                    stress = json.loads(response.value.body(), parse_float=Decimal)
                    stress_request_bytes = response.value.request.post_data.encode()
                    check('graphical creation freezes a separate expected policy', response.value.status == 200
                          and stress['policy']['dividends'][0]['markPolicy'] == term['markPolicy'])
                    page.once('dialog', lambda dialog: dialog.accept())
                    page.get_by_role('button', name='Stop account', exact=True).click()
                    page.get_by_text('This account is stopped', exact=False).wait_for()
                    check('stopping an unstarted claim account creates no capital', http('/api/research/raw-paper-accounts')['accounts'][1]['balance']['contributionsGbp'] == 0)
                    page.close()
                    advertised = http('/api/research/raw-paper-accounts', exact=False)

                    def advertise(payload):
                        def route_response(route):
                            if route.request.method == 'GET':
                                route.fulfill(status=200, content_type='application/json', body=json.dumps(payload))
                            else:
                                route.continue_()
                        return route_response

                    for missing in ('support', 'scope', 'mode'):
                        variant = json.loads(json.dumps(advertised))
                        if missing == 'support':
                            variant['actionSupport'].pop('pendingDividendClaims')
                        elif missing == 'scope':
                            variant['pendingDividendScope'] = 'future-scope'
                        else:
                            variant['pendingDividendMode'] = 'dividend-claims-v2'
                        page = browser.new_page(viewport={'width': 390, 'height': 960})
                        page.route('**/api/research/raw-paper-accounts', advertise(variant))
                        page.goto(base + '/#paper')
                        page.get_by_role('heading', name='Batches', exact=True).wait_for()
                        check(f'unknown {missing}: existing claim account is read-only', page.get_by_role('button', name='Prepare next batch', exact=True).count() == 0
                              and 'read-only' in page.locator('main').inner_text())
                        page.get_by_text('New paper account', exact=True).click()
                        check(f'unknown {missing}: claims creation is hidden while paid mode remains', page.locator('option[value="dividend-claims-v1"]').count() == 0
                              and page.locator('option[value="cash-dividends-v1"]').count() == 1)
                        page.close()
                finally:
                    browser.close()
            accounts = http('/api/research/raw-paper-accounts')['accounts']
            current = next(a for a in accounts if a['id'] == account['id'])
            check('payment replaces the claim once without funding earlier shares', current['acceptedPeriods'] == 2
                  and current['balance']['cashGbp'] == Decimal('.24921875') and current['balance']['equityGbp'] == Decimal('39.74921875')
                  and current['balance']['holdings']['aaa']['quantity'] == Decimal('.49375') and current['balance']['feesGbp'] == Decimal('.51')
                  and current['balance']['dividendClaims']['count'] == 0)
            receipt_hashes = {p.name: digest(p) for p in (workspace / 'raw-paper-operations').glob('*.json')}
            frozen_inputs = {str(p.relative_to(workspace / 'raw-paper-inputs')): digest(p) for p in (workspace / 'raw-paper-inputs').rglob('*') if p.is_file()}
            stop()
            shutil.move(workspace / 'raw-paper-inputs', workspace / 'inputs-hidden')
            shutil.move(workspace / 'runs', workspace / 'runs-hidden')
            start('cold')
            check('cold advance UUID replay needs no originals or source reports', http(path + '/advance', second_request) == second)
            check('cold creation retry returns the stopped account without originals', http('/api/research/raw-paper-accounts', stress_request_bytes)['status'] == 'stopped')
            check('cold restart preserves all balances and receipt/request bytes', http('/api/research/raw-paper-accounts')['accounts'] == accounts
                  and receipt_hashes == {p.name: digest(p) for p in (workspace / 'raw-paper-operations').glob('*.json')})
            check('source study and trial register unchanged', all(http(p) == value for p, value in initial.items()))
            restore('inputs-hidden', 'raw-paper-inputs')
            restore('runs-hidden', 'runs')
            check('frozen input files retain their names and exact bytes', frozen_inputs == {str(p.relative_to(workspace / 'raw-paper-inputs')): digest(p) for p in (workspace / 'raw-paper-inputs').rglob('*') if p.is_file()})
            check('source report files retain their exact bytes', reports == {str(p.relative_to(workspace)): digest(p) for p in (workspace / 'runs').rglob('*') if p.is_file()})
            completed = True
    finally:
        stop()
        after = source(repo)
        save('source-after.json', after)
        checks.append({'name': 'complete workflow reached cold-retry and preservation checks', 'passed': completed})
        checks.append({'name': 'source and runtime unchanged during workflow', 'passed': before == after})
        result = {'passed': sum(c['passed'] for c in checks), 'failed': sum(not c['passed'] for c in checks), 'checks': checks,
                  'authoredInputs': True, 'realLocalApiAndBrowser': not args.kill_before_commit, 'realLocalApi': True, 'actualSigkill': args.kill_before_commit and completed, 'productionTouched': False, 'genuineBrokerIntegration': False}
        save('verification.json', result)
        print(json.dumps({k: result[k] for k in ('passed', 'failed', 'productionTouched')}))
    if not all(c['passed'] for c in checks):
        raise SystemExit(1)


if __name__ == '__main__':
    main()
