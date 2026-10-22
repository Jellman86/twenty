# Bound session timing and pinned FX (`execution-v3`)

This offline checkpoint replaces the v2 planner's first-observed-session and booking-FX assumptions. It does not expose a registered raw backtest, submit orders, authenticate vendor claims or complete terminal/action scheduling.

## Calendar facts and bounds

`ExecutionCalendar` contains a contiguous bounded sequence of local dates, with explicit open/close instants or an explicit closed row for every date. Gaps, duplicates, half-sessions, invalid instants/local dates and more than 10,000 rows are refused. The venue and entire `CalendarReference` must match the listing and dataset. A SHA-256 over the normalized table, reference, venue and source reference binds the exact supplied session facts; equivalent timezone offsets normalize to the same hash. The complete table remains in `RawExecutionResult.Calendar`, including for all-refused runs.

`NasdaqSessions.ExecutionCalendar` adapts the existing reviewed XNAS 2015–2026 holiday/early-close table. It uses 09:30 New York local time for regular-session opens, with DST conversion, and preserves the reviewed 16:00/13:00 closes. [Nasdaq Equity 1](https://listingcenter.nasdaq.com/rulebook/nasdaq/rules/nasdaq-equity-1) defines regular market hours and notes that individual opening crosses occur sequentially. The regular-session boundary is therefore a declared modelling instant, not proof that a particular security traded at exactly that instant. Existing [calendar source review and exceptions](nasdaq-sessions.md) remain authoritative for the bounded table. No other venue or year is inferred from a matching timezone.

The generic calendar constructor validates structure and records caller assertions; it does not authenticate a source or prove that a supplied closed row is correct. Genuine use requires a reviewed source adapter and retained originals. Calendar facts are retrospective: they do not claim that an unscheduled closure was announced before the historical decision. The schedule is an execution input, not a strategy feature.

## Exact session selection

The planner finds the first calendar open **strictly after** the order's decision and no later than its deadline. It requires the raw bar for that exact session date and close. A missing, halted, mismatched or unavailable next session cannot be replaced by a later bar. Exhausted/unreviewed calendar coverage fails closed; it is not evidence that the exchange was closed. Decisions after an already-passed opening cannot buy at that earlier open.

Daily bars are retrospective execution assumptions. Their availability must satisfy the existing supplied-slice and order-deadline guards. Neither the decision callback nor this model is entitled to treat the later bar's open/volume as information available at the earlier decision. Daily volume participation remains an ex-post approximation, not a quote or opening-auction capacity model.

## Exact FX and booking

A foreign `PlannedOrder.FxObservationId` pins one execution observation. It must match the foreign→GBP direction and `Executable` purpose, be observed and available by the modelled session open, and satisfy the explicit maximum age measured at that open. Bounds are inclusive. Missing/wrong identifiers, reference/indicative rates, stale/future rates and rates first available later are `InvalidExecutionFx` refusals. No later observation, inverse or reference series is substituted. If the original pin has been superseded out of the supplied booking slice, it is refused rather than reconstructed from a newer vintage; retaining and resolving original execution archives remains an import/replay requirement. GBP needs no conversion and refuses a fictitious FX binding.

The pin is realized execution evidence, not a prediction or an invitation to choose among later rates after seeing performance. Registered integration must bind an upfront resolution policy and disclose the whole input/trial family. This offline callback/reference is not a strategy sandbox. An `Executable` label does not independently certify broker access, rate terms or source authenticity; verified imports remain required.

`RawExecutionRun` passes the plan's evidence into the ledger. `RawLedgerEntry.At` remains booking/knowledge time; `ModelledExecution.Timing.Session.OpenAt` records the distinct modelled execution instant. Receipts retain session and schedule hashes, the bar identifier and availability evidence, price/FX source snapshots and original content hashes, plus the exact FX observation/rate. The ledger rechecks these against the supplied dataset and calendar before changing cash. Legacy `Fill` calls without execution evidence retain their original explicit-price/booking-FX assumption contract; they are not automatically upgraded into execution evidence.

A newly opened holding uses the modelled execution instant. Retroactive fills cannot spend later deposits or pass later existing ledger events; same-open fills may be booked in the caller's order. An unresolved action between execution and booking refuses the late receipt, so a later booking cannot silently erase an intervening dividend or terminal event. Full event-time replay and action/payment scheduling remain future work. Funding/holdings failures still abort the run rather than creating a completed partial result.

## Validation and remaining work

The first regressions failed on calendar-gap acceptance, missed deadline semantics, skipped next-session bars and future FX. Further authored cases cover weekends/holidays, DST, early closes, mourning closures, range/ref binding, hash determinism, inclusive FX-age/availability boundaries, wrong direction/purpose/pins, later-rate perturbation, altered source/receipt evidence, cash atomicity, backdated funding and intervening actions. All money/market inputs are authored; the calendar adapter reuses the reviewed factual table.

Registered strategy selection, actual executable quote/import acceptance, durable order refusals, multi-calendar portfolio scheduling, registered terminal/action scheduling and combined end-to-end acceptance remain open. No old notional report is reinterpreted and no genuine holdout, broker order or accelerator is used by these checks.


## Publication and deployment acceptance (22 October 2026)

Source `db251fc106378c881ce7c80fde6f4f21dceb0394` passed [CI 39826520508](https://github.com/Jellman86/twenty/actions/runs/39826520508), including 835 .NET tests, Python/web checks, research-boundary and sensitive-path checks, full-history/files secret scans, image smoke and registry publication. Four exported authored scenarios independently match Python Decimal and zoneinfo, retaining original price/FX source hashes. Private accounting proof is under `/mnt/apps/developer/jellman86/artifacts/twenty/execution-timing-20261022`.

A fresh private compatibility clone under the new scientific identity preserved the completed authored universe study's exact result/plan and ten request/report pairs. Replaying the original final request retained the original pair; a replacement remained refused, with ten trials unchanged. This was not a production trial or genuine holdout use. Proof is under `/mnt/apps/developer/jellman86/artifacts/twenty/universe-study-cross-version-timing-20261022`.

After rechecking that no production runs or studies were active, the published commit-specific registry digest was verified against `dev`, then deployed through Git-backed Dockhand sync/deploy. Live metadata reports the exact source and one running healthy container. Eight stored collections, 58 byte-identical reports, four diagnostic responses and completed study plans were preserved. Trial counts remain 61 model and zero universe trials; seven paper accounts are unchanged. Broker connection and automatic purchases remain disabled.

Read-only shared Playwright checks passed at desktop and 390px widths with no page errors, horizontal overflow or research/execution writes. Native preview was attempted first and reported missing host browser libraries. Deployment receipts are under `/mnt/apps/developer/jellman86/artifacts/twenty/deploy-timing-20261022`. These are engineering acceptance checks for the offline timing contract, not actual market-fill or initial-v1 acceptance.


The subsequent [terminal ledger reference](raw-terminal-accounting.md) adds explicit retirement and settlement primitives. The execution run still requires event scheduling, pending-claim valuation and registered integration before it can consume those events automatically.
