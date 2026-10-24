# Gross session participation and ordered replay (`execution-v4`)

24 October 2026. The offline execution reference now prevents order splitting from multiplying a listing/session's declared volume allowance. This is a prerequisite for raw-strategy integration, not a registered raw strategy or a certification of executable liquidity.

## Reproduced defects and resulting behavior

Previously, every order was independently compared with `MaximumParticipation × Volume`. For a 5,000-share bar and a 10% cap, two 300-share orders both passed despite totaling 600 shares against a 500-share allowance. Independently planned receipts could also be booked through the direct ledger without a cumulative volume check. Null orders were silently skipped.

The planner now snapshots at most 10,001 elements from its order stream and refuses null entries or more than 10,000 orders with `invalid-execution-orders`. This bounds lazy/infinite streams before market work; enumerators are disposed. Empty streams remain supported. This is a per-planner-call bound, not a new global execution-run resource guarantee.

Valid orders retain caller order. Accepted **executed, quantity-step-rounded share quantities** consume a budget keyed by exact listing ID and local session date. Buys and sells consume gross volume, rather than offsetting each other. Rejected orders consume nothing. A whole order exceeding the remaining allowance is returned as `VolumeParticipation`; it is not partially filled or silently removed. A later smaller order may still fit. Distinct listing/session keys have separate budgets. The caller must bind and disclose order ordering in any future registered strategy; this implementation does not optimize ordering after observing results.

Each modelled fill now retains `Participation = { maximumParticipation, sessionVolume }`. The ledger requires a positive fraction no larger than one and positive volume equal to its bound raw bar. Before changing any cash, fees, holdings, IDs or entries, it sums prior modelled quantities for that listing/session across all calls. The same cap and volume must be retained throughout that session. A changed policy, revised-volume capture, missing/forged proof or excess cumulative quantity refuses with `raw-ledger-execution-participation`; a rejected attempt consumes no capacity. Keys use the local session date, so a changed calendar's opening instant cannot reset the same day's budget. Source and calendar validity still have their separate checks.

Caller-supplied assumed fills retain their weaker contract and are not automatically treated as modelled participation evidence. This budget covers modelled fills in one ledger; it is not an account-wide broker/order-book limit or a claim about other investors' fills. The fraction is a declared modelling assumption. A direct ledger receipt cannot independently establish the provenance of that policy; replay additionally replans from the declared `FillPolicy` and requires exact receipt equality.

Daily volume is known retrospectively. As in [the timing contract](execution-timing.md), the screen uses an explicitly supplied later raw bar; it does not make its open or eventual daily volume a decision-time feature or establish opening-auction capacity. Genuine liquidity/FX and source acceptance remain open.

## Consecutive modelled fills and booking time

A separate regression found that explicit replay rejected even valid consecutive receipts at one open: it compared the second open with the first receipt's later booking time. Replay now compares consecutive modelled fills by their execution-open instants. This permits ordered same-open fills within the cumulative budget, and later opens remain chronologically ordered.

Marks and all other replay operations retain their booking-time barrier. A fill cannot jump over an intervening later mark, deposit, assumed fill or other booked event. The ledger independently checks existing entries by their actual modelled-open or non-modelled booking times. An earlier open cannot spend later cash or reverse an already booked execution. The pre-existing replay booking-time guard already rejected the over-budget two-receipt example before this change; that is **not** asserted to have been a previously successful replay exploit. The missing aggregate enforcement existed in planning and direct ledger calls. The new aggregate guard becomes essential when valid consecutive replay fills are admitted.

## TDD and validation

Six initial regressions first failed on the previous code: split buys, reversed direction, exact-boundary/rejection behavior, null-order refusal and direct-ledger calls that independently replanned the same session. The valid consecutive-replay regression also failed before its timing change.

Twenty-two added cases bring the solution to **1,089 .NET tests**. They cover quantity rounding, rejected attempts, gross buy/sell volume, exact boundaries, lazy stream disposal/bounds, later-session independence, immutable rejection, missing/invalid evidence, policy/volume changes, forged replay proof, retained mark/cash barriers and reversed execution times. The positive two-fill example passes explicit replay, schedule compilation, archive execution and fresh receipt verification with identical result bytes. Existing calendar, FX, action, accounting and archive tests pass.

Temporary controls bypassing batch accumulation, ledger accumulation and the replay timing barrier are tested separately, then restored. The guards must detect each bypass; generated logs and proof remain private under `/mnt/apps/developer/jellman86/artifacts/twenty/execution-participation-20261024`. All financial inputs are authored synthetic fixtures. No genuine holdout, vendor request, broker action, optional neural dependency or actual/mocked accelerator is used.

## Compatibility and remaining acceptance

Planner/run versions are now `execution-v4` and `raw-execution-v4`. Missing participation evidence in a supplied legacy modelled receipt refuses; no evidence is fabricated during deserialization. Existing raw archive/request/schedule version labels remain unchanged, but their exact Core/runtime build binding changes, so old raw receipts intentionally refuse under the new binary and remain immutable. Reproduction requires their original exact build and datasets; there is no silent migration or rehashing.

Core changes also change the registered scientific identity. Completed study artifacts must remain byte-identical, their exact final request must replay its original result, and a replacement final request must remain refused. Unfinished reserved studies cannot silently resume under changed science. These compatibility and deployment checks are separate from unit-test success. No research-store schema, notional-report units, strategy candidate family, UI workflow or broker setting changes here.

Source-wide corporate-action discovery, verified genuine settlement/FX imports, registered changing-universe raw execution and combined capture-to-study/report acceptance remain open in [the initial acceptance contract](initial-v1-acceptance.md).

## Local compatibility and fresh-process acceptance

The restored implementation passes all 1,089 .NET tests, 45 core Python tests, Ruff, Svelte check/build, sensitive-path and research-boundary probes, and full-history/files secret scanning with the deliberate leak control. A fresh private compatibility clone under the new scientific identity preserves the completed study, exact plan and ten original request/report pairs; its original final UUID replays and a replacement remains refused. Proof is under `/mnt/apps/developer/jellman86/artifacts/twenty/universe-study-cross-version-participation-20261024`.

Fresh CLI processes compile a two-fill schedule, create/reopen/verify its archive and retry without changing receipt bytes or file timestamp. From GBP 20,000, two 50-share purchases at GBP 100 leave GBP 10,000 cash and 100 shares; the final GBP 200 mark gives GBP 30,000 equity. Each receipt retains the 10% cap and 1,000-share daily volume. Two independently planned 60-share orders are refused without publishing a receipt. Python Decimal separately checks these arithmetic values. This is authored engineering acceptance, not historical performance. The exact plan, input dataset, CLI logs and `cli-verification.json` remain in the private checkpoint directory.
