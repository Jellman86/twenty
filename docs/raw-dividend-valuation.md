# Explicit unpaid-dividend valuation

`ValueWithDividendReceivables` extends the offline [dividend reference](raw-dividend-accounting.md) with a separately labelled estimate of unpaid income. It has no web/broker/study route. The existing `Value` method still refuses unpaid claims, and existing report types and dataset schemas are unchanged. This is a reference accounting dependency, not completed changing-universe backtest integration or investment evidence.

The later [complete claim mark and explicit replay](raw-claim-replay.md) includes pending terminal cash/stock consideration alongside dividends. This dividend-only method retains its pending-terminal refusal.

## Every pending claim and every assumption

The caller supplies exactly one `RawDividendMarkAssumption` for each unpaid entitlement, keyed by listing/action identity. Missing, duplicate, unknown, already-paid or null entries fail closed. There is no zero-tax/zero-fee default. Each assumption names the original cash currency, expected withholding in local major units, expected GBP fee, availability time, source reference and SHA-256. Currency must match, withholding must lie within gross, fees must be non-negative and the source/assumption must already be available at the mark time. Hash shape validation does not authenticate its source contents.

For every pending claim, expected local net income is `gross − expected withholding`. A positive net is marked through fresh available FX of the caller's explicit purpose and direction at the **valuation time**. Reference FX remains a research mark, with purpose and observation retained. It does not become the later executable payment conversion. A fully withheld claim needs no FX; a GBP fee still creates an estimated negative balance. Fees are not silently capped at the dividend amount.

The result separates cash-and-holdings, per-claim estimates, their sum and `ExpectedEquityGbp = holdings-and-cash equity + expected dividend balance`. It retains each frozen entitlement, full assumption, FX identifier/rate and expected local/GBP amount. Entries, cash, shares, actual fees, paid status and timestamps do not change. Estimated income cannot fund a purchase. Ordering is stable by listing/action identity, regardless of assumption input order.

## Raw prices, payment and timing

The underlying holdings use the same raw-price/denomination, age, action-evidence and temporal guards as ordinary valuation. The method cannot bypass an unsupported action, missing/revised booked evidence or absent/stale raw prices. Estimated income is added once to raw share values; adjusted total-return bars are never used as a substitute for raw prices plus dividends.

Payment removes the claim from the unpaid set and credits actual net GBP cash using its pinned payment conversion. A later estimate requires no assumption for that paid claim; stale paid assumptions are refused. Under identical price/FX/withholding/fee inputs, estimated equity is conserved across payment, while spendable cash increases. Different actual withholding, fees or payment FX change the outcome explicitly. Earlier mark objects and receipts remain unchanged.

Future observations are excluded by the validated slice. Neither a later payment rate nor later policy evidence can improve an earlier estimate. Changing assumptions can change estimated equity; genuine study integration must declare the entire policy/input family before performance comparison, retain all trials and validate source evidence. This method does not establish historical ownership, cash entitlement, tax treatment, executable rates, achievable returns or absence of upstream data gaps.

## Authored validation

Thirty new cases bring the full suite to 708 .NET tests. The first case failed on the previous unpaid-dividend refusal. A deliberate omission of the receivable from total estimated equity also fails the regression and is restored. Cases cover complete assumption coverage/availability, payment without double counting, unchanged spendable cash, raw ex-dividend value conservation, reference versus later executable FX, future-rate perturbation, sales/splits, fully withheld fee liabilities, pence units, action revisions and missing/stale/wrong-purpose FX.

An authored example buys two 100 USD shares at 0.8 GBP/USD from 300 GBP. Cash is 140 GBP. At an ex-dividend raw price of 98 USD, shares are worth 156.8 GBP; a four-USD entitlement with one-USD expected withholding and a 0.2 GBP fee is worth 2.2 GBP, giving estimated equity 299 GBP. Matching payment moves 2.2 GBP into cash and removes the receivable without changing total equity. With no expected deductions, raw price decline plus the 3.2 GBP gross claim instead preserves 300 GBP. At a current reference rate of 0.75, estimated equity is 289.05 GBP; actual payment at 0.7 produces 288.9 GBP under the same share mark, making the 0.15 GBP FX difference visible.

Five exported C# scenarios match independently recomputed Python Decimal receivables, cash and payment reconciliation, including gross, net, reference-FX, pence and fully-withheld cases. Private proof is under `/mnt/apps/developer/jellman86/artifacts/twenty/raw-receivables-20261021`. All inputs are authored. No vendor/broker request, production trial, genuine final evaluation or accelerator is used. Terminal events, durable replay, verified source imports, registered raw backtests and combined application acceptance remain open.


## Publication and deployment acceptance

Source commit `7d809384b5bccb12d6eca4b5bfc406e7c8ff7455` passed [CI 39634608835](https://github.com/Jellman86/twenty/actions/runs/39634608835): 708 .NET tests, Python tests, Ruff, Svelte check/build, research-boundary checks, full-history/files secret scans, image smoke tests and registry publication. Local Python validation passed 39 tests; Svelte reported no errors or warnings. The previously restored receivable regression was rerun green after the deliberate negative control.

A fresh private compatibility clone under the updated scientific identity preserved a completed authored universe study's exact result/plan and ten request/report pairs. Reusing its final request replayed the original final pair; a replacement final request remained refused, with ten trials unchanged. This was an offline clone rehearsal, not a production final evaluation. Proof is under `/mnt/apps/developer/jellman86/artifacts/twenty/universe-study-cross-version-receivables-20261021`.

After checking that no production runs or studies were active, the published `dev` image was verified against the commit-specific registry digest and deployed through Git-backed Dockhand sync/deploy. The application reports the exact source commit and one running healthy container. All eight stored collections, 58 byte-identical reports, four diagnostic responses and completed study plans were preserved; the trial register remains 61 model trials and zero universe trials. Broker connection and automatic purchases remain disabled.

Read-only shared Playwright checks passed at desktop and 390px widths with no page errors, horizontal overflow or research/execution writes. Native T3 preview was attempted first and reported missing host browser libraries. Private deployment receipts are under `/mnt/apps/developer/jellman86/artifacts/twenty/deploy-raw-receivables-20261021`. These checks establish this reference checkpoint's publication and state preservation; initial-v1 gates remain partial pending terminal handling, registered raw accounting, source acceptance and combined operational validation.
