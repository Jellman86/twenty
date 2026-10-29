"""Private authored terminal-account API/UI/cold-retry and actual SIGKILL checks.

Copies an exported fixture and owns a loopback-only native API. Never targets
production; inherited app/broker configuration is removed. For --kill-before-commit
export a cash action in the second period, so earlier accepted capital survives.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import sqlite3
import subprocess
import threading
import time
import uuid
from decimal import Decimal
from http.client import HTTPException
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen

from playwright.sync_api import sync_playwright
from raw_paper_dividend_workflow import digest, source


def main(pending_claims=False):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--fixture', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    parser.add_argument('--kill-before-commit', action='store_true')
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    out = args.out.resolve(); out.mkdir(mode=0o700, parents=True, exist_ok=False)
    workspace = out / 'workspace'; shutil.copytree(args.fixture / 'workspace', workspace)
    create = json.loads((args.fixture / 'create.json').read_bytes())
    term = create['terminals'][0]; delivery = term['delivery']
    kind = 'zero' if delivery is None else 'cash' if delivery['currency'] else 'stock'
    mode = 'terminal-claims-v1' if pending_claims else 'terminal-settlements-v1'
    version = 'raw-paper-terminal-claim-batches-v1' if pending_claims else 'raw-paper-terminal-batches-v1'
    before = source(repo); checks = []; completed = False
    server = log = base = None

    def save(name, value):
        (out / name).write_text(json.dumps(value, indent=2, default=str) + '\n')

    def check(name, condition):
        checks.append({'name': name, 'passed': bool(condition)})
        if not condition:
            raise AssertionError(name)

    def http(path, body=None):
        request = Request(base + path, data=None if body is None else json.dumps(body).encode(),
                          headers={'Content-Type': 'application/json', 'X-Twenty-Request': '1'})
        try:
            with urlopen(request, timeout=120) as response:
                return json.loads(response.read(), parse_float=Decimal)
        except HTTPError as error:
            save('http-error.json', {'path': path, 'status': error.code, 'body': error.read().decode()})
            raise

    def stop(kill=False):
        if server is not None and server.poll() is None:
            server.kill() if kill else server.terminate()
            try:
                server.wait(timeout=15)
            except subprocess.TimeoutExpired:
                server.kill(); server.wait()
        if log is not None:
            log.close()

    def start(label):
        nonlocal server, log, base
        env = {k: v for k, v in os.environ.items() if not k.startswith(('TWENTY_', 'ASPNETCORE_'))}
        env.update(TWENTY_DATA_ROOT=str(workspace), TWENTY_AUTH='off', TWENTY_COLLECTOR_ROLE='off', ASPNETCORE_URLS='http://127.0.0.1:0')
        path = out / ('api-' + label + '.log'); log = path.open('w')
        server = subprocess.Popen(['dotnet', str(repo / 'src/Twenty.Api/bin/Release/net10.0/Twenty.Api.dll')],
                                  cwd=repo / 'src/Twenty.Api', env=env, stdout=log, stderr=subprocess.STDOUT)
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            match = re.search(r'Now listening on: (http://127\.0\.0\.1:\d+)', path.read_text())
            if match:
                base = match.group(1); return
            if server.poll() is not None:
                raise AssertionError('Owned native API exited; inspect its private log')
            time.sleep(.1)
        raise AssertionError('Owned native API did not become ready')

    def restore(hidden, target):
        old, new = workspace / hidden, workspace / target
        if new.exists():
            if any(new.iterdir()):
                raise AssertionError('Refuse to replace a nonempty startup directory')
            new.rmdir()
        old.rename(new)

    def hashes(folder):
        return {str(p.relative_to(workspace)): digest(p) for p in (workspace / folder).rglob('*') if p.is_file()}

    save('source-before.json', before)
    try:
        start('first')
        check('new authored fixture has no paper accounts', http('/api/research/raw-paper-accounts')['accounts'] == [])
        initial = {p: http(p) for p in ['/api/research/raw-studies', '/api/research/raw-trials']}
        reports, inputs = hashes('runs'), None
        account = http('/api/research/raw-paper-accounts', create); path = '/api/research/raw-paper-accounts/' + account['id']
        check('new version freezes the declared terminal catalog', account['version'] == version
              and 'terminals' in account['policy'] and 'dividends' not in account['policy'])
        inputs = hashes('raw-paper-inputs')
        (workspace / 'runs').rename(workspace / 'runs-hidden')
        first = http(path + '/prepare', {'expectedReceiptSha256': None})
        expected = 0 if pending_claims else Decimal('19.75') if args.kill_before_commit else {'cash': Decimal('17.27125'), 'stock': Decimal('19.75'), 'zero': 0}[kind]
        check('first projection matches independent accounting', first['projectedBalance']['equityGbp'] == expected)
        if pending_claims:
            balance = first['projectedBalance']; claim = balance['terminalClaims']
            check('pending claims never become spendable cash or delivered holdings', balance['cashGbp'] == 0 and balance['holdings'] == {}
                  and balance['feesGbp'] == Decimal('.25') and claim == {'count': 0 if kind == 'zero' else 1,
                  'netExpectedValueGbp': 0 if kind == 'zero' else Decimal('19.74'), 'expectedFeesGbp': 0 if kind == 'zero' else Decimal('.01'),
                  'expectedEquityGbp': 0 if kind == 'zero' else Decimal('19.74')})
        check('preparation admits no operation or capital', http(path + '/operations') == []
              and http('/api/research/raw-paper-accounts')['accounts'][0]['balance']['contributionsGbp'] == 0)
        one_request = {'requestId': str(uuid.uuid4()), 'expectedReceiptSha256': None, 'replaySha256': first['replaySha256']}
        one = http(path + '/advance', one_request); check('first explicit apply succeeds', one['status'] == 'succeeded')
        accepted = http('/api/research/raw-paper-accounts')['accounts'][0]
        if args.kill_before_commit:
            check('kill fixture preserves the earlier accepted mark', kind == 'cash'
                  and accepted['balance']['equityGbp'] == (0 if pending_claims else Decimal('19.75'))
                  and accepted['balance']['contributionsGbp'] == 20 and accepted['balance']['feesGbp'] == Decimal('.25'))
            second = http(path + '/prepare', {'expectedReceiptSha256': one['receiptSha256']})
            request = {'requestId': str(uuid.uuid4()), 'expectedReceiptSha256': one['receiptSha256'], 'replaySha256': second['replaySha256']}
            disconnected = []

            def advance():
                try:
                    http(path + '/advance', request)
                except (OSError, HTTPException) as error:
                    disconnected.append(type(error).__name__)

            thread = threading.Thread(target=advance, daemon=True); thread.start()
            lock = None; operation = None; deadline = time.monotonic() + 120
            while time.monotonic() < deadline:
                with sqlite3.connect(workspace / 'research.db', timeout=.05) as db:
                    rows = [json.loads(r[0]) for r in db.execute('SELECT json FROM raw_paper_operations')]
                operation = next((o for o in rows if o['requestId'] == request['requestId'] and o['status'] == 'running'), None)
                if operation:
                    lock = sqlite3.connect(workspace / 'research.db', timeout=1); lock.execute('BEGIN IMMEDIATE')
                    row = json.loads(lock.execute('SELECT json FROM raw_paper_operations WHERE id=?', (operation['id'],)).fetchone()[0])
                    if row['status'] == 'running':
                        break
                    lock.rollback(); lock.close(); lock = None
                time.sleep(.01)
            check('writer lock follows durable running admission', lock is not None)
            try:
                receipt_path = workspace / 'raw-paper-operations' / (operation['id'] + '.receipt.json')
                while time.monotonic() < deadline and not receipt_path.exists():
                    time.sleep(.01)
                check('complete immutable terminal receipt precedes success commit', receipt_path.exists())
                orphan = receipt_path.read_bytes()
                import base64
                result = json.loads(base64.b64decode(json.loads(orphan)['resultBase64']), parse_float=Decimal)
                check('orphan contains the uncommitted retirement and actual settlement', result['contributionsGbp'] == 40
                      and result['cashGbp'] == (Decimal('17.27125') if pending_claims else Decimal('34.5525')) and len(result['terminalEntitlements']) == 1
                      and sum(e['kind'] == 'terminal-settlement' for e in result['entries']) == 1)
                row = json.loads(lock.execute('SELECT json FROM raw_paper_accounts WHERE id=?', (account['id'],)).fetchone()[0], parse_float=Decimal)
                check('earlier accepted capital is intact before the kill', row['acceptedPeriods'] == 1 and row['balance'] == accepted['balance'])
                stop(kill=True); check('owned native API receives actual SIGKILL', server.returncode == -9)
            finally:
                if lock is not None:
                    lock.rollback(); lock.close()
            thread.join(timeout=10); check('in-flight request disconnects with the owned process', not thread.is_alive() and bool(disconnected))
            (workspace / 'raw-paper-inputs').rename(workspace / 'inputs-hidden'); start('after-kill')
            restored = http('/api/research/raw-paper-accounts')['accounts'][0]; retry = http(path + '/advance', request)
            check('restart keeps the interrupted attempt without adopting terminal proceeds', restored['status'] == 'failed'
                  and restored['acceptedPeriods'] == 1 and restored['attempts'] == 2 and restored['balance'] == accepted['balance']
                  and retry['status'] == 'interrupted' and retry['errorCode'] == 'process-restarted')
            check('same UUID cannot replace the interrupted result', retry == http(path + '/advance', request)
                  and len(http(path + '/operations')) == 2 and orphan == receipt_path.read_bytes())
            check('original accepted operation still replays without originals', one == http(path + '/advance', one_request))
            save('fault-state.json', {'before': accepted, 'after': restored, 'interrupted': retry, 'orphanSha256': digest(receipt_path)})
            restore('inputs-hidden', 'raw-paper-inputs')
        else:
            with sync_playwright() as playwright:
                browser = playwright.chromium.launch(headless=True)
                try:
                    for width in (1280, 390):
                        page = browser.new_page(viewport={'width': width, 'height': 960}); writes = []; receipts = []; errors = []
                        page.on('request', lambda r, saved=writes: saved.append(r.url) if r.method != 'GET' else None)
                        page.on('request', lambda r, saved=receipts: saved.append(r.url) if '/receipt' in r.url else None)
                        page.on('pageerror', lambda e, saved=errors: saved.append(str(e)))
                        page.goto(base + '/#paper'); page.get_by_role('heading', name='Batches', exact=True).wait_for()
                        text = page.locator('main').inner_text(); (out / f'paper-{width}.txt').write_text(text)
                        check(f'{width}: frozen terminal mode and authored provenance are visible', ('Pending terminal claims' if pending_claims else 'Terminal settlements') in text
                              and 'Unsupported mode' not in text and 'authored inputs' in text)
                        if pending_claims:
                            check(f'{width}: estimates are labelled separately from actual holdings', 'Estimated terminal receivable' in text
                                  and 'Estimated terminal fees' in text and 'Delivered holdings and cash' in text and 'undelivered shares' in text)
                        check(f'{width}: inspection is write-free and receipt-free', not writes and not receipts and not errors)
                        check(f'{width}: layout fits viewport', page.evaluate('document.documentElement.scrollWidth <= innerWidth'))
                        page.screenshot(path=str(out / f'paper-{width}.png'), full_page=True)
                        if width == 390:
                            # A failed response must be visible in evidence before waiting for projection.
                            # Only a witnessed busy refusal may receive one explicit user retry.
                            for attempt in range(2):
                                before_prepare_ops = http(path + '/operations')
                                with page.expect_response(lambda r: r.request.method == 'POST' and r.url.endswith(path + '/prepare'), timeout=120000) as response:
                                    page.get_by_role('button', name='Prepare next batch', exact=True).click()
                                payload = json.loads(response.value.body(), parse_float=Decimal)
                                save(f'browser-prepare-{attempt + 1}.json', {'status': response.value.status, 'body': payload,
                                     'request': json.loads(response.value.request.post_data)})
                                if response.value.status == 200:
                                    break
                                page.get_by_role('alert').wait_for()
                                (out / f'browser-prepare-error-{attempt + 1}.txt').write_text(page.get_by_role('alert').inner_text())
                                check('only an explicit busy refusal permits a preview retry', attempt == 0 and payload.get('error') == 'research-worker-busy')
                                check('busy preview leaves operations and accepted capital unchanged', http(path + '/operations') == before_prepare_ops
                                      and http('/api/research/raw-paper-accounts')['accounts'][0]['balance'] == accepted['balance'])
                            check('browser preview response succeeds', response.value.status == 200)
                            page.get_by_role('heading', name='Projected batch 2 · Not yet applied', exact=True).wait_for(timeout=10000)
                            check('browser preparation keeps accepted capital unchanged', http('/api/research/raw-paper-accounts')['accounts'][0]['balance'] == accepted['balance'])
                            with page.expect_response(lambda r: r.request.method == 'POST' and r.url.endswith(path + '/advance'), timeout=120000) as response:
                                page.get_by_role('button', name='Apply batch', exact=True).click()
                            two = json.loads(response.value.body(), parse_float=Decimal); two_request = json.loads(response.value.request.post_data)
                            check('browser explicitly applies the second batch', response.value.status == 200 and two['status'] == 'succeeded')
                        page.close()
                    # Author the second cost account using the real form, including explicit outcome-specific terms.
                    restore('runs-hidden', 'runs')
                    page = browser.new_page(viewport={'width': 390, 'height': 960}); page.goto(base + '/#paper')
                    page.get_by_role('heading', name='Batches', exact=True).wait_for(); page.get_by_text('New paper account', exact=True).click()
                    page.get_by_role('combobox', name='Costs', exact=True).select_option('stress')
                    page.get_by_role('textbox', name='Dataset SHA-256', exact=True).fill(create['datasetSha256'])
                    page.get_by_role('textbox', name='Calendar pins (JSON)', exact=True).fill(json.dumps(create['calendars']))
                    page.get_by_role('textbox', name='Decision dates (JSON)', exact=True).fill(json.dumps(create['decisions']))
                    page.get_by_role('combobox', name='Corporate actions', exact=True).select_option(mode)
                    page.get_by_role('combobox', name='Terminal outcome', exact=True).select_option(kind)
                    values = {'Retiring listing ID': term['listingId'], 'Terminal action ID': term['actionId'], 'Terminal observation ID': term['observationId'],
                              'Terminal cutoff time (UTC)': term['evidence']['entitlementAt'], 'Terminal cutoff proof available (UTC)': term['evidence']['availableAt'],
                              'Terminal cutoff source reference': term['evidence']['sourceReference'], 'Terminal cutoff content SHA-256': term['evidence']['contentSha256']}
                    if delivery:
                        values.update({'Source settlement ID': delivery['sourceSettlementId'], 'Settlement source reference': delivery['sourceReference'],
                                       'Settlement content SHA-256': delivery['contentSha256'], 'Settled time (UTC)': delivery['settledAt'],
                                       'Settlement available (UTC)': delivery['availableAt'], 'Settlement fee per entitled account (GBP)': str(delivery['fixedFeeGbp'])})
                        if kind == 'cash':
                            values.update({'Settlement currency': delivery['currency'], 'Terminal gross per cutoff share': str(delivery['grossPerShare']),
                                           'Terminal withholding per cutoff share': str(delivery['withheldPerShare']), 'Settlement FX observation ID': delivery['fxObservationId'] or ''})
                        else:
                            values.update({'Delivered successor listing ID': delivery['successorListingId'], 'Successor shares per cutoff share': str(delivery['successorSharesPerShare'])})
                    if pending_claims and delivery:
                        policy = term['markPolicy']
                        values.update({'Expected terminal fee per entitled account (GBP)': str(policy['expectedFeeGbp']),
                                       'Expected terminal policy available (UTC)': policy['availableAt'],
                                       'Expected terminal policy source reference': policy['sourceReference'],
                                       'Expected terminal policy content SHA-256': policy['contentSha256']})
                        if kind == 'cash':
                            values['Expected terminal withholding per cutoff share'] = str(policy['expectedWithheldPerShare'])
                    for label, value in values.items():
                        page.get_by_role('textbox', name=label, exact=True).fill(value)
                    check('terminal authoring fits mobile width', page.evaluate('document.documentElement.scrollWidth <= innerWidth'))
                    with page.expect_response(lambda r: r.request.method == 'POST' and r.url.endswith('/api/research/raw-paper-accounts'), timeout=120000) as response:
                        page.get_by_role('button', name='Create account', exact=True).click()
                    authored = json.loads(response.value.body(), parse_float=Decimal)
                    actual_delivery = authored['policy']['terminals'][0]['delivery']
                    expected_delivery = account['policy']['terminals'][0]['delivery']
                    check('graphical creation freezes explicit terminal terms only', response.value.status == 200 and authored['policy']['actionMode'] == mode
                          and 'dividends' not in authored['policy'] and (actual_delivery is None and expected_delivery is None or
                          actual_delivery is not None and expected_delivery is not None and
                          {k: v for k, v in actual_delivery.items() if k != 'booking'} == {k: v for k, v in expected_delivery.items() if k != 'booking'}))
                    if pending_claims:
                        check('graphical creation freezes independent expected policy', authored['policy']['terminals'][0].get('markPolicy') == account['policy']['terminals'][0].get('markPolicy'))
                    page.on('dialog', lambda dialog: dialog.accept()); page.get_by_role('button', name='Stop account', exact=True).click()
                    page.get_by_text('This account is stopped and accepts no more batches.', exact=True).wait_for()
                    page.close()
                    caps = http('/api/research/raw-paper-accounts')
                    for field in (('pendingTerminalMode', 'pendingTerminalScope', 'support') if pending_claims else ('terminalMode', 'terminalScope', 'support')):
                        variant = json.loads(json.dumps(caps, default=float))
                        if field == 'support':
                            variant['actionSupport'].pop('pendingTerminalClaims' if pending_claims else 'terminals')
                        else:
                            variant[field] = 'unknown'
                        page = browser.new_page()
                        page.route('**/api/research/raw-paper-accounts', lambda route, _request, body=variant: route.fulfill(json=body))
                        page.goto(base + '/#paper'); page.get_by_role('heading', name='Batches', exact=True).wait_for()
                        check(field + ': unsupported scope hides calculation controls', page.get_by_role('button', name='Prepare next batch', exact=True).count() == 0)
                        page.get_by_text('New paper account', exact=True).click()
                        modes = page.get_by_role('combobox', name='Corporate actions', exact=True).locator('option').evaluate_all('(options)=>options.map(o=>o.value)')
                        check(field + ': terminal authoring hidden while paid dividends remain', mode not in modes and 'cash-dividends-v1' in modes
                              and (not pending_claims or 'terminal-settlements-v1' in modes))
                        page.close()
                finally:
                    browser.close()
            balance = http('/api/research/raw-paper-accounts')['accounts'][0]['balance']
            check('second accepted mark matches independent settled accounting', balance['equityGbp'] ==
                  {'cash': Decimal('37.02125'), 'stock': Decimal('39.49') if pending_claims else Decimal('39.5'), 'zero': Decimal('19.75')}[kind]
                  and balance['contributionsGbp'] == 40 and balance['cashGbp'] == (Decimal('17.27125') if pending_claims and kind == 'cash' else 0))
            if pending_claims:
                check('settlement removes estimates and books actual fees once', balance['terminalClaims']['count'] == 0
                      and balance['terminalClaims']['expectedEquityGbp'] == balance['equityGbp']
                      and balance['feesGbp'] == (Decimal('.5') if kind == 'zero' else Decimal('.51')))
            stored = http('/api/research/raw-paper-accounts')['accounts']; operations = http(path + '/operations')
            receipts = hashes('raw-paper-operations'); (workspace / 'raw-paper-inputs').rename(workspace / 'inputs-hidden')
            (workspace / 'runs').rename(workspace / 'runs-hidden'); stop(); start('cold')
            check('cold UUID retries need no originals or source reports', http(path + '/advance', two_request) == two
                  and http(path + '/advance', one_request) == one and http('/api/research/raw-paper-accounts', create)['id'] == account['id'])
            check('cold restart retains account and operation bytes', http('/api/research/raw-paper-accounts')['accounts'] == stored
                  and http(path + '/operations') == operations and hashes('raw-paper-operations') == receipts)
            restore('inputs-hidden', 'raw-paper-inputs')
        check('source study and trial register remain unchanged', all(http(p) == value for p, value in initial.items()))
        restore('runs-hidden', 'runs')
        check('frozen inputs preserve exact filenames and bytes', hashes('raw-paper-inputs') == inputs)
        check('source reports preserve exact filenames and bytes', hashes('runs') == reports)
        completed = True
    finally:
        stop(); after = source(repo); save('source-after.json', after)
        checks.append({'name': 'complete workflow reached preservation checks', 'passed': completed})
        checks.append({'name': 'source and runtime stayed unchanged', 'passed': before == after})
        proof = {'passed': sum(c['passed'] for c in checks), 'failed': sum(not c['passed'] for c in checks), 'checks': checks,
                 'authoredInputs': True, 'realNativeApi': True, 'realBrowser': not args.kill_before_commit, 'actualSigkill': args.kill_before_commit and completed,
                 'productionTouched': False, 'brokerRequests': 0, 'pendingTerminalClaims': pending_claims}
        save('verification.json', proof)
        print(json.dumps({k: proof[k] for k in ['passed', 'failed', 'actualSigkill', 'productionTouched']}))
    if not completed or proof['failed']:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
