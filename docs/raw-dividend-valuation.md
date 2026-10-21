# Explicit unpaid-dividend valuation

`ValueWithDividendReceivables` extends the offline [dividend reference](raw-dividend-accounting.md) with a separately labelled estimate of unpaid income. It has no web/broker/study route. The existing `Value` method still refuses unpaid claims, and existing report types and dataset schemas are unchanged. This is a reference accounting dependency, not completed changing-universe backtest integration or investment evidence.

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
