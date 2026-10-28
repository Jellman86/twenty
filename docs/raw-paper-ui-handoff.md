# Raw paper UI handoff

Owner direction, 28 October 2026: another agent will own the UI work; this checkpoint focuses on the backend. The existing prepare/apply workbench is deployed at `d4968ae`. The new pure-split backend must not be deployed with the old UI's blanket claim that all corporate actions are unsupported. No new UI implementation or browser acceptance is claimed here. Mac testing is out of scope.

## Stable backend contract to consume

- `GET /api/research/raw-paper-accounts`: `authoredOnly: true`, `preparationAvailable: true`, `corporateActions: true`, `pureSplitsAvailable: true`, `actionSupport: {pureSplits:true, dividends:false, terminals:false, pendingClaims:false}`, `asynchronousFills:false`, and `accounts`. The broad corporate-action boolean now means some actions are supported; the matrix defines exactly which. Do not advertise all action types from that boolean.
- Creation retains the existing request fields. An explicit `actionMode: "pure-splits-v1"` opt-in freezes the new mode. Omit it for the legacy no-action account. Null/omitted values keep historical serialized bytes unchanged. Unknown strings refuse. There is still one account per source study/cost scenario, including stopped/failed accounts; an old account cannot be upgraded/reset to gain a different mode.
- A pure-split account has `version: "raw-paper-split-batches-v1"` and `policy.actionMode: "pure-splits-v1"`. Legacy accounts keep `raw-paper-batches-v1` and omit `actionMode`. Show this frozen distinction on inspection. Treat unknown modes as inspectable but unsupported for new work.
- Preparation is unchanged: `POST .../{id}/prepare` with the required explicit `expectedReceiptSha256` (null first, exact head later). It returns a projection and immutable cumulative `replaySha256`, never accepted capital. Separate `POST .../{id}/advance` retains UUID/head semantics. New split accounts additionally require the exact canonical next batch regenerated from their frozen inputs; semantically similar manually invented IDs/events are not accepted. Legacy manual submission remains supported.
- Inspecting accounts/operations does not load receipts or source/final reports automatically. Receipts remain explicit downloads. Historical records and UUID retries are readable even when the scientific build changed; continuation is read-only in that case.

## Errors and truthful scope

Show safe explanations for `raw-paper-actions-unsupported`, `raw-paper-split-vintage`, `raw-paper-split-ambiguous`, `raw-paper-action-during-batch`, `raw-paper-action-binding`, `raw-paper-split-price`, `raw-price-share-units`, `raw-paper-action-history`, `raw-ledger-action-history` and `raw-paper-arithmetic`, alongside existing busy/quota/head/input/status codes. A preparation refusal admits no attempt. An admitted apply failure retains an immutable failed attempt and stops future batches, preserving accepted capital.

The mode accepts only authored pure positive-ratio splits with a single original action vintage. Multiple equal effective or booking instants refuse. A split during the complete fill batch refuses. Valuation/target quotes must use post-split share units; a raw close before the known effective split refuses rather than creating a false gain. Dividends, terminal proceeds, pending claims, partial/asynchronous fills, genuine forward evaluation and broker execution remain unsupported.

The subsequent [shared quote-unit guard](raw-price-share-units.md) changes Core identity as well as the earlier Research change. Use the final committed backend revision and generate fresh authored studies/accounts with that exact running build; do not silently resume a fixture frozen under the prior backend. Old bytes/retries are deliberately read-only. Neutral 1:1 splits do not change quote units. The API fields above remain unchanged by the shared guard.

## UI acceptance cases

1. Optional, explicit creation opt-in with clear permanent scope; default creation still omits the mode, including against an older server without `pureSplitsAvailable`.
2. Separate preparation and application; preview is labelled projected, accepted balances remain unchanged until success, stale/delayed responses cannot populate another inspected account, and old heads cannot apply twice.
3. Display legacy versus split mode, partial action support and old scientific identity without implying investable results or upgrade authority. Preserve exact decimal strings in original receipts; browser display values must not be sent back as accounting inputs.
4. Check error, stop/completion, receipt and unknown-mode handling at desktop/390px and with keyboard controls. Include actual responses from an authored private backend, rather than only intercepted successful examples.
5. Keep production research registers unchanged. No genuine holdout, vendor/broker call, live trading or production-authored experiment is authorised as incidental UI testing.

The original no-action hand calculation remains £19.75 / £39.50 equity after one/two £20 deposits and £0.25 per-fill fees. With one authored 2:1 split and raw price USD 100 → USD 50, the same equities correspond to 0.49375 / 0.9875 shares. These are accounting fixtures, not returns evidence. Backend source-bound tests and actual API receipts are recorded under `paper-splits-20261028` outside Git. A draft UI patch is retained there only as a reference, not as accepted UI work.

## Operational UI, 28 October 2026

The web app now lands on Portfolio and has a top navigation: Portfolio, Activity, Orders, Paper accounts, Research and Settings. Old `#workbench`, `#roadmap`, `#evidence` and `#execution` links still resolve. Paper accounts (`#paper`) consume the contract above: split opt-in only when `pureSplitsAvailable`, `actionMode` omitted otherwise, projected preparation shown separately from accepted balances, unknown modes and older builds read-only, receipts as explicit downloads, and identity hashes inside a collapsed details panel.

`scripts/ui_workflow_check.py` drives the rendered app with Playwright against a private loopback fixture copy. Broker, journal and simulated-ledger responses in it are synthetic intercepted fixtures; raw paper checks use the real backend.
