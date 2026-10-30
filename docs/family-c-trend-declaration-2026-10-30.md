# Family C trend-rule declaration · 30 October 2026

Declared and committed **before** the market/T-bill panel was built or any of these trials ran. This is the [growth plan's](growth-research.md#family-c--exposure-rules) next step after the 18 October industry runs found no edge: every app strategy ended below plain monthly purchases of the market (see [real-data findings](real-data-findings-2026-10-30.md)).

## Question

For a UK investor adding £20 a month, does switching between the US stock market and US Treasury bills on a simple trend rule beat holding the market? A pass would justify forward paper testing only. It would not authorise trading.

The test can be carried out in an ISA. A US-market UCITS ETF and a 0–1-year US Treasury UCITS ETF are both available as GBP-account purchases. Their costs and tracking error are **not** modelled here beyond the flat scenario costs below.

## Data

- **Kenneth French** `F-F_Research_Data_Factors`, monthly section:
  - Market = `Mkt-RF + RF`. This is CRSP-based, with dividends reinvested, so survivorship-free at portfolio level.
  - T-bills = `RF`, the one-month Treasury bill return.
- **ECB reference USD→GBP**, using the same last-fixing-on-or-before-month-end rule (at most seven days old) as `french-industry-gbp-v1`. Both assets are unhedged USD exposures, so currency risk is present in both states.
- **Window:** 1999-01-31 (index baseline) to 2026-08-31. Captures are a fresh retrieved vintage, recorded by hash. The panel is `researchOnly` (non-executable research portfolios), as for the industry panel.
- **Symbols:** `FF_MARKET`, `FF_TBILL`. The benchmark is `FRENCH_MARKET` (the same series as `FF_MARKET`, held separately as the comparator).

## Trials (all eight count)

| ID | Hypothesis | Holdings | Allocation | Meaning |
|---|---|---|---|---|
| C1 | `momentum` | 1 | `full-rebalance` | Absolute momentum: hold whichever of market and T-bills had the higher 12-month return, skipping the latest month. Switch the whole portfolio when the ranking changes |
| C2 | `breakout` | 1 | `full-rebalance` | Hold whichever is closer to (or above) its own prior 12-month high. The market wins only near new highs |
| C3 | `momentum` | 1 | `contribution-only` | C1's ranking directs each new £20 only. Nothing is sold |
| C4 | `breakout` | 1 | `contribution-only` | C2's ranking directs each new £20 only |

Each candidate runs under two cost settings, in the same way as the 18 October runs:

- **Base:** FX 15 bps, half-spread 5 bps, slippage 5 bps, no purchase tax, £1 minimum order.
- **Stress:** double the base rates.

Shared settings: £20/month, `TopK 1`, `MaximumWeight 1`, `TrainMonths 60`, `RefitMonths 12`, `HoldoutMonths 24`. The development window is 2005-08-31 to 2024-07-31, identical to the 18 October industry runs. The last 24 months stay unevaluated.

Comparators are produced by the engine for every trial: `benchmark` (the market bought monthly), `equal_weight` (50/50 market/T-bills) and zero-interest `cash`.

## Decision rule (fixed now)

The primary rule is the registered-study screen. A candidate's score is the smaller of its base and stress margins, where each margin is the candidate's ending GBP minus the greatest ending GBP among benchmark, equal-weight and cash.

- **Positive score:** the candidate only *nominates* for forward paper testing on current prices.
- **Non-positive score:** rejection.

Every outcome is reported, with no reselection, re-tuned lookbacks or extra variants after the results are seen. Any later variant is a new, disclosed trial.

These measures are also reported, but they cannot override the primary rule: maximum drawdown, months in T-bills and switch count.

## Disclosure

These trials run in a **private local workspace** on Quark, not in the production register. Production's import inbox is root-owned, and placing files there would need host access that this session does not have. The workspace, captures, reports and hashes are retained under `artifacts/twenty/real-data-goal-20261030/`, and the results are reported in full in the findings document, including a failure if one occurs.

Earlier exposure must be disclosed. The French market series and this development window were already used as the *benchmark* in the 18 October runs, so the market's path over 2005–2024 is known. Market history from 1999 to 2024, including 2008 and 2020, is well known in general. This is not an unseen test, and its evidence is weaker for that reason.
