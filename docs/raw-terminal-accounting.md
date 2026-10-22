# Raw terminal entitlement and settlement

`RawGbpLedger` now handles explicitly supplied pure zero, cash and stock terminal outcomes offline. `CaptureTerminal` retires the old position and freezes its claim; `SettleTerminal` records one exact later payment/delivery. This is a reference accounting dependency, not a registered raw backtest or automatic corporate-action scheduler. Old notional reports and dataset schemas are unchanged.

## Entitlement and retirement

An available, effective delisting or pure cash/zero merger must have supported terms. Missing outcomes, mixed cash/stock consideration, spinoffs, symbol changes and additional split terms are refused. The schema represents simple stock consideration as a delisting with a distinct successor and positive share ratio. Unrepresented rights such as CVRs cannot be independently discovered by this ledger; genuine source review must establish that the supplied terms are complete.

The caller supplies `RawTerminalEvidence`: an entitlement cutoff equal to the action's effective instant, an availability time, source reference and SHA-256. These are explicit source assertions. Hash shape validation does not authenticate the original, prove legal ownership or establish that a last trading session is the entitlement cutoff. A converter's final-session action must not be treated as that separate proof automatically.

The old position must predate the cutoff. Post-cutoff fills or later splits prevent retroactive inference of its quantity. Earlier unresolved actions must be handled first; ambiguous simultaneous/later economic events are refused. Listing denomination is checked immediately before exit, so capture does not require the old listing to remain active at booking. The original shares are removed exactly once and the full original action, ownership start time, frozen quantity and evidence remain in immutable receipts.

A confirmed zero outcome writes off the shares without creating cash, fees or FX. Cash claims freeze `quantity × cash-per-share` in the explicitly declared payout currency's **major units**, regardless of the old quote units/currency. Stock claims freeze `quantity × exchange ratio` successor shares; no price or currency conversion is inferred. An old listing cannot be repurchased after retirement, or through a stale active assertion when a terminal action is already known.

## Pending claims are not spendable holdings

Cash and stock claims remain pending until explicitly settled. Neither cash nor successor shares are credited on capture. Ordinary valuation and the dividend-receivable wrapper refuse `raw-ledger-terminal-unsettled` rather than omit the pending value. Other purchases can spend existing cash only. Terminal receivable valuation, event scheduling and portfolio returns across unresolved settlement lags remain unimplemented; retirement does not make the claim economically worthless.

## One exact settlement

`RawTerminalSettlement` records a source-scoped physical settlement identifier, source reference/hash, actual settled time, availability time and booking slice. Settlement must follow entitlement; actual settlement and its evidence must be known by booking. Partial payments/deliveries and replacement terms are refused. Identifiers cannot be reused across terminal settlements or dividend payments; callers must qualify provider identifiers consistently.

Cash settlement must match the frozen gross and currency, declare withholding within gross and a non-negative GBP fee, and carry no stock consideration. Net GBP cash is `(gross − withheld) × explicit rate − fee`. Foreign conversion pins one exact foreign→GBP `Executable` observation, observed and available by settlement within the caller's age bound measured at settlement. This is a conservative model requirement, not acceptance of every real statement format. A newer booking-date or reference rate cannot substitute. GBP needs no FX identifier; a fully withheld payment needs no conversion at all, while its actual GBP fee can still reduce cash. Borrowing is refused.

Stock settlement must deliver the exact frozen successor/quantity, declare no cash/withholding/FX fields and pay only its explicit GBP fee. The successor must exist with consistent units and active status at delivery and booking. Its quantity is added to any compatible existing holding; different quote units or currency do not change the share count. New holdings open at delivery time, not the older terminal cutoff. No FX or notional cash is invented for the share transfer.

Economic successor actions between entitlement and booking, or successor ledger movements at/after a late delivery, require explicit reconstruction and are refused. This protects against omitted splits/dividends and retroactively reinterpreted trades. Fractional results are exact decimal quantities; rounding, cash in lieu and stock withholding are not silently approximated.

Refused captures/settlements leave positions, cash, fees, claim status and event identifiers unchanged. Successful cash and stock settlements preserve contributions and retain all source terms. Booked action bindings remain checked even after closing the old shares: missing or economically revised evidence refuses further use. A later identical vintage is allowed without rewriting the original receipt. Newly disclosed unbooked economic events since ownership began, including a hidden earlier split or a later distribution on the retired listing, refuse further use even after settlement; retirement cannot hide contradictory or additional consideration.

## Authored validation

Sixty new cases bring the full suite to 895 .NET tests. The original zero/cash cases first failed on the unsupported-action boundary; a separate regression reproduced post-terminal purchases through a stale active listing before the new guard. Cases cover exact cash/stock/zero accounting, pending funding and valuation refusal, fees/withholding, pence and cross-currency terms, delivery into existing holdings, payment FX timing, evidence/identity/revision guards, overdraft atomicity, post-cutoff trading and successor history.

Seven exported C# scenarios independently match Python Decimal cash, equity, fees and quantities:

| Authored scenario | Final GBP cash | Final GBP equity |
| --- | ---: | ---: |
| Zero outcome after two USD-share purchases | 140 | 140 |
| USD cash, withholding and payment FX | 289.5 | 289.5 |
| Fully withheld cash, actual fee only | 139.5 | 139.5 |
| Three USD-quoted successor shares | 139.5 | 319.5 |
| Delivery into one already held successor share | 102 | 342 |
| Three successor shares quoted in GBP pence | 139.5 | 379.5 |
| GBP cash payout after pence-quoted purchases | 24.9 | 24.9 |

The USD examples start with 300 GBP and two shares bought at 100 USD using 0.8 GBP/USD. Cash consideration is 220 USD gross less 20 USD withholding, converted at 0.75 with a 0.5 GBP fee. Stock consideration is 1.5 successor shares per original share, marked at 80 USD and 0.75 GBP/USD. These are authored arithmetic checks, not historical returns. Private proof is under `/mnt/apps/developer/jellman86/artifacts/twenty/raw-terminal-20261022`.

Python tests, Ruff, web checks/build, sensitive-path guards, research-boundary probes and full-history/files secret scans also pass. No vendor/broker request, genuine holdout, model trial or accelerator is used. Verified terminal/settlement imports, pending terminal valuation, event scheduling, durable replay, registered raw backtests and combined initial-v1 acceptance remain open.
