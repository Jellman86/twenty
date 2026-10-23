# Independent raw-accounting arithmetic parity

23 October 2026. This checkpoint strengthens V1-03; it does not close that gate or register a raw-price strategy. The application accounting remains C#/.NET. Python is an offline independent arithmetic check, with no broker, market-data or execution authority.

[The shared authored fixture](../fixtures/research/raw-accounting-parity.v1.json) declares six scenarios with decimal strings and expected checkpoints. [The C# test](../tests/Twenty.Tests/RawAccountingParityTests.cs) constructs validated point-in-time inputs and calls the real `RawGbpLedger` methods. [The Python reference](../src/twenty/raw_accounting_reference.py) separately calculates share counts, contributions, fees, cash and pending claims directly from fixture terms, without calling .NET or reading the expected answers. Both compare all 39 checkpoints against the same original answers. Neither generates the other's expected results.

| Authored scenario | Closing cash GBP | Closing equity GBP | Distinguishing checks |
| --- | ---: | ---: | --- |
| Foreign dividend, cash exit | 290.55 | 290.55 | Dividend survives removal of the original shares; terminal tax and dividend tax are deducted separately |
| Foreign dividend, stock exit | 140.55 | 320.55 | Three delivered successor shares, separately valued at USD 80 × GBP 0.75/USD; dividend paid after delivery |
| Foreign dividend, confirmed zero | 141.05 | 141.05 | Original shares removed without creating terminal cash; pending dividend still paid |
| Pence, fractional split, dividend and partial sale | 17.55 | 27.55 | 2.5 shares become 6.25; only raw prices receive pence scaling; five shares remain after sale |
| Fully withheld GBP claims | 9.25 | 9.25 | No net dividend or terminal cash; explicit GBP fees still reduce cash |
| Contributions and fractional foreign sale | 102 | 121.25 | GBP 120 contributions retained separately; remaining 0.25 shares marked at a different FX rate |

The checkpoints retain pending dividend cash, pending terminal cash and pending successor quantities separately. A pending claim is never delivered cash or a holding. Final marks in these six scenarios are fully settled. Pending-claim *estimates* remain covered by the separate C# receivable tests, not by this independent reference.

## TDD and fault detection

The first Python regression failed because the reference module did not exist. After implementation, all shared scenarios passed independently. Tests additionally establish that expected answers do not drive the Python calculation, inputs are not mutated, a changed final FX quote changes the exposed foreign holding's final mark without changing earlier checkpoints, and a caller's low decimal precision does not round the reference results. Invalid funding, unsupported operations, duplicate payments, excess withholding, mismatched successor delivery and premature settled marks refuse.

Three temporary negative controls were then applied to the actual sources, tested and restored:

- Removing `Denomination.ToMajor` from C# fill accounting was detected by the pence scenario.
- Adding withholding instead of deducting it in C# terminal settlement was detected by the shared terminal scenarios.
- Adding withholding instead of deducting it in the independent Python calculation was detected by the authored answers.

These are fault-injection checks, not defects asserted to exist in the final application. The C# accounting source is unchanged by this checkpoint. Private logs are under `/mnt/apps/developer/jellman86/artifacts/twenty/raw-accounting-parity-20261023`; generated logs and run artifacts are excluded from Git.

## Scope and second-order effects

This is bounded arithmetic evidence over authored synthetic inputs. It establishes agreement for these declared examples, not general numerical equivalence, independently corroborated settlement, legal entitlement, corporate-action discovery, executable FX availability, a registered strategy or achievable market returns. One original listing, one successor, one dividend and one terminal action are supported by the Python reference; it is not an application ledger, dataset validator or archive verifier. Its fixtures stay away from .NET decimal precision/overflow boundaries. It uses a private 80-digit decimal context and introduces no currency rounding policy.

The C# adapter uses authored daily booking points and prices, without a venue-calendar or modelled-fill assertion. Active listing metadata may remain present after the explicit terminal action; the ledger's terminal action removes the economic position. These tests do not certify historical listing cessation or genuine price coverage. They do not cover mixed/CVR consideration, partial settlement, successor-lot merging, late revisions, pending-claim estimates or source timing; the existing focused C# tests retain their own scope.

No API, UI, research-store schema, scientific identity implementation, archive format, deployed workload, broker setting or study reservation changes. No genuine final evaluation, vendor query, GPU/NPU or training run is consumed. Existing reports and studies remain untouched. Publication status is tracked on [issue 6](https://github.com/Jellman86/twenty/issues/6); deployment of an unchanged application is unnecessary.

Registered changing-universe execution/FX integration, source-wide action discovery, genuine settlement/FX import and the combined capture-to-study/report acceptance remain open in [the initial acceptance contract](initial-v1-acceptance.md).

## Local validation

The full solution passed **1,067 .NET tests**; the Python suite passed **45 tests**, without the optional neural dependency. Ruff, sensitive-path checks, the isolated scientific-boundary probe and secret scans (including the deliberate leak control) passed. `npm ci`, Svelte checking (zero errors/warnings) and the production web build passed. The six parity cases passed again after removing unused terminal-fixture fields. No actual or mocked accelerator was involved; all financial inputs here were authored synthetic examples.
