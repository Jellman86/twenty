# Current signals and broker execution coordinator

Implemented on `dev`, 15 October 2026. This extends the isolated historical rehearsal with a separate, account-bound execution journal. Default configuration is disconnected and disabled. All implementation tests use authored gateways/HTTP fixtures; no authenticated Trading 212 order has been submitted by the project.

## Signal contract

A completed **contribution-only** experiment supplies a frozen hypothesis, feature version, scaling/model snapshot, benchmark/universe and allocation policy. Full-rebalance reports remain available for research but cannot approve the buy-only coordinator. A separately imported latest monthly panel supplies current features. No future price row is fabricated and no model is refitted on reserved outcomes. Momentum and breakout retain their own scoring functions; they are not silently executed as ridge strategies.

The decision month is the UTC calendar month following the last input observation month. Stale/future panels, mismatched universes/features and observations registered after generation are rejected. Each signal persists input/model/report hashes, feature cutoff, local input registration time, generation time and ranked target weights before any outcome. Local registration proves only when Twenty confirmed those bytes, not the producer's publication time or historical vintage. Private data-quality evidence remains necessary.

## Private activation

Set `TWENTY_EXECUTION_CONFIG_FILE` to a read-only mounted JSON file. Credential files and `TWENTY_T212_ENVIRONMENT` remain separate, as in [API setup](trading212-setup.md). Missing configuration creates no broker calls. `enabled: false` prevents execution; `automatic: false` permits only an explicitly requested cycle after all gates pass. `automatic: true` runs a bounded cycle once a minute. A web deployment never sets either flag or provisions credentials.

The following deliberately incomplete example cannot activate execution. Replace the placeholders only with privately verified inputs; never commit the completed file.

```json
{
  "enabled": false,
  "automatic": false,
  "environment": "demo",
  "accountId": 123456789,
  "accountType": "demo-invest",
  "runId": "COMPLETED_RUN_ID",
  "reportSha256": "EXACT_REPORT_SHA256",
  "monthlyGbp": 20,
  "walletRule": {
    "netValueConvention": "FROM_VERIFIED_SAMPLES",
    "includesTaxes": true,
    "taxQuantityConvention": "FROM_VERIFIED_SAMPLES"
  },
  "accountEvidence": {"path": "/run/secrets/account-evidence.txt", "sha256": "EXACT_FILE_SHA256"},
  "researchEvidence": {"path": "/run/secrets/research-evidence.txt", "sha256": "EXACT_FILE_SHA256"},
  "marketDataEvidence": {"path": "/run/secrets/data-evidence.txt", "sha256": "EXACT_FILE_SHA256"},
  "walletEvidence": {"path": "/run/secrets/wallet-evidence.txt", "sha256": "EXACT_FILE_SHA256"},
  "quotesFile": "/run/secrets/current-execution-quotes.json",
  "datasetId": "EXPLICIT_CURRENT_PANEL_SHA256",
  "syntheticEvidence": true
}
```

Evidence files are bounded and checked against their SHA-256 values. File integrity does **not** establish that the contents are correct or constitute independent scientific/legal approval. The operator must actually inspect the account/ISA identity and key scopes, market data/availability/licensing, research evaluation, raw-price units and wallet samples. Do not create empty placeholder “approvals” to bypass those external checks.

Demo accepts `demo-invest` or `demo-isa`. Live requires `stocks-isa`, nonsynthetic inputs/evidence and a matching nonsynthetic demo receipt. Trading 212 summary independently checks the configured account ID and GBP primary currency; the schema supplies no account-type field. The linked terms restriction remains documented in the setup audit; continuing implementation does not claim broker consent.

## Quotes and instrument rules

The private quote file uses version `execution-quotes-v1` and an `items` array. Each item contains all of:

- `rule`: `symbol`, `brokerTicker`, `quoteCurrency` (GBP/GBX), raw `ask` and `limitPrice`, `quantityStep`, `minimumNotionalGbp`, `maximumFeeBps`, UTC `observedAt` and `eligible`.
- `securityId`: a verified issuer/stock grouping identity. Duplicate groups/listings are rejected, so aliases or multiple listings cannot evade concentration checks.
- `priceTick`, `source`, `ruleEvidenceSha256`, `orderCurrency` (the verified response currency) and explicit `synthetic`.

Quotes must be no more than five minutes old. Limits must align to the price tick and share step. The current contract initially supports only GBP/GBX buys; foreign-currency order budgeting is not guessed. Provide rules for the full frozen universe, including already held names. Rule evidence must be supported by the private data audit and authenticated/instrument-source samples; a hash string alone does not validate a trading minimum.

Exact normalized quote snapshots and broker request/response bytes are archived privately under `/data/broker-evidence`, with hashes and capture receipts. Authentication/cookies are excluded. The archive is bounded at 128 MiB and stops rather than discarding required evidence when full. It needs deliberate private retention/export before saturation, not a large automatic backup schedule.

## Cash journal and wallet interpretation

The public schema exposes individual fill IDs, quantities/prices and `walletImpact`, but does not establish the sign of `netValue` or whether it includes the tax components. These semantics require controlled captures matched to the broker's cash/statement evidence. [Official schema](https://docs.trading212.com/_bundle/api.json?download=).

Before enabling execution, the read-only capture action can archive summary, positions, pending orders and bounded history using configured credentials. It places no order. Inspect actual BUY/TRADE fills and statements to select a versioned profile:

- `netValueConvention`: `negative-debit` or `positive-debit`.
- `includesTaxes`: whether the verified net value already includes taxes; there is no inferred tax addition.
- `taxQuantityConvention`: `positive-charge` or `negative-charge`.

The journal accepts only supported BUY/TRADE evidence with a GBP wallet impact. It sums unique interpreted fill cash impacts, never repeated order-level cumulative quantities. It does not reconstruct cash from quantity × price × FX, infer a sign with absolute value or turn pending `filledQuantity` into a verified debit. Booked wallet impact is distinct from legal securities settlement.

Unexpected fill revisions, unsupported corporate-action types, contradictory terminal states and monetary inconsistencies create persistent integrity blocks. A later truncated/empty history scan cannot clear them. Verified actual debits exceeding the reserved estimate are recorded, including a negative virtual sleeve cash balance if necessary, and freeze further execution. They are not hidden to preserve a budget invariant. Repair requires independent reconciliation and a reviewed ledger/policy migration; there is no automatic “clear error and buy again” button.

## Budget, holdings and submission workflow

The £20 is a **virtual monthly allocation to the strategy sleeve**, not proof that a deposit occurred. The app does not transfer money. Broker availability must cover the sleeve allocation. Other ISA holdings/cash are recorded as an outside baseline, not appropriated as strategy capital. One account/environment can receive only one allocation per UTC month; changing a model cannot create another allowance. Carry-over cash can fund later purchases, so a monthly capital allowance is not a fixed gross-turnover limit.

The planner buys only and never automatically sells or replaces an order. The matching native backtest uses `allocationPolicy: "contribution-only"`; legacy/missing values mean `full-rebalance` and are rejected for execution. Research and broker both select available holding slots in rank order, then fund permitted targets in deterministic weight/symbol order. A purchase target is not a continuous weight ceiling: price appreciation can exceed it when selling is disallowed. Existing/pending names consume holding slots. If rankings rotate while slots are occupied, the policy buys eligible held names or retains cash rather than accumulating an unlimited number of stocks. Price/fee reserves and concentration are rechecked against a conservative NAV floor.

Intents and reservations commit before transport. A submitting marker commits before each non-idempotent POST. Restarted submitting attempts become unknown and are never replayed. Before **each** POST the coordinator refreshes broker positions/pending/history, reconciles known fills, checks account/funds, reloads policy/quotes, and rechecks the pinned batch rules, matching demo receipt, exposure/slots, pause and expiry. The upstream endpoints do not provide an atomic snapshot: use the selected listings exclusively for this application, and treat concurrent/manual activity as a reconciliation concern. These checks narrow the race; they cannot lock another broker client out.

Missing pending/history rows never prove cancellation. A successful cancellation request retains reserves until positive terminal evidence and complete accepted fill quantities arrive. A timeout stays unknown. An operator may bind an independently inspected broker order ID to an unknown intent; one matching price/time row alone is not proof of ownership. No automatic replacement is sent.

Demo receipts require reconciled fills plus matching position readback. They bind the strategy report, both Core **and API/coordinator code**, wallet/research/data contract and instrument-rule contract. Synthetic receipts cannot activate live, and changed rules/code require new compatible evidence. A private one-process workspace lease and one coordinator semaphore prevent concurrent workers sharing the ledger.

## Validation limits

Tests exercise frozen inference and all hypotheses, actual local observation time, idempotent funding, holding rotations, account/environment guards, before-send markers, restart uncertainty, ambiguous submissions, duplicate/reordered fills, cancellation/late fills, missing history, wallet sign/tax profiles, debit overruns, conflicting revisions, per-order external activity, changed rules, pagination, rate resets, exact private capture and response deadlines. The conditional live path is exercised with authored gateway objects in temporary workspaces only. That is software evidence, not a genuine demo receipt or confirmation of broker payload compatibility.

Still external: API key/secret and scopes, independently verified ISA binding, authenticated wallet samples, licensed/audited current and historical market inputs, executable quote/rule feed and a defensible chosen strategy. The installed web deployment remains disconnected. There is no profitable strategy or millionaire probability established by this implementation.
