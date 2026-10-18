# Assumption tests — 18 October 2026

Declared before the Python runs below were executed. The C# family was already
declared in `data/real-research/comparison-plan.json` (created before its
results) and is reused here, not extended. No reserved holdout was evaluated.
Every attempted trial is listed, including failures.

## Data and limits

- Panel `363ff590…fce6`: Kenneth French 10 value-weighted industry portfolios
  plus market, monthly, 1999-01 to 2026-08, converted to GBP with ECB
  reference rates. Development window for the C# runs: 2005-08-31 to
  2024-07-31 (227 contributions of £20 = £4,540). The Python harness evaluates
  233 contributions (£4,660) because its training window starts earlier.
- This panel is `researchOnly`: non-executable industry portfolios from a
  retrospective revised vintage, not point-in-time stocks. It can falsify a
  strategy assumption cheaply. It cannot promote anything to a broker signal
  (see [Registered studies](registered-studies.md) and issue #5).
- Costs: base 15 bps FX + 5 bps half-spread + 5 bps slippage per side, £1
  minimum. Stress doubles the variable rates. Trading 212's published ISA FX
  fee is 0.15%, so the FX component is realistic for USD names; GBP names
  avoid it and some UK shares add purchase tax, which this flat schedule does
  not model.

## Assumptions and verdicts

### A1. £20/month can reach £1,000,000 in a tolerable time at plausible returns — **rejected as stated**

Month-end deposits, annual effective compounding (the `project` convention):

| Sustained return | Years to £1m at £20/month |
|---|---|
| 7% | 83.5 |
| 10% | 62.9 |
| 12.5% (this panel's market rate) | 52.8 |
| 15% | 45.7 |
| 20% | 36.5 |

Reaching £1m in 20 years needs ~44% per year, every year. In 30 years, ~26%.
No evidence in the [literature review](literature-review.md) supports
expecting such rates from an unleveraged long-only ISA. The honest levers
are time and contribution size: at 10%, £1m in 30 years needs ~£485/month;
in 40 years ~£180/month. Return-chasing concentration is the remaining
lever, and A5 below shows its median outcome is worse, not faster. The £1m
figure stays a scenario-tool output (see [Roadmap](roadmap.md)), never a
training target.

### A2. The trained/active strategies beat the market and equal weight after costs — **rejected on this panel**

C# contribution-only runs on Quark (8 declared trials; annualised
time-weighted return, ending equity on £4,540 contributed):

| Hypothesis | Base | Stress |
|---|---|---|
| ridge-relative-return | 9.50% / £13,428 | 9.43% / £13,395 |
| momentum | 9.50% / £13,698 | 9.43% / £13,665 |
| breakout | 11.43% / £18,103 | 11.37% / £18,059 |
| upside-tail | failed: `tail-insufficient-examples` | failed: `tail-insufficient-examples` |
| equal weight (comparator) | 11.07% / £16,463 | 10.98% / £16,393 |
| benchmark (market) | **12.39% / £21,267** | **12.31% / £21,213** |

Independent Python replication (full-rebalance, `20261018T103503Z-43cdabec`
base, `20261018T103507Z-62de69e0` stress; £4,660 contributed):

| Strategy | Base | Stress |
|---|---|---|
| trained ridge | 8.04% / £12,652 (cost £2,369) | 4.83% / £8,898 (cost £3,707) |
| momentum | 12.50% / £20,532 | 11.39% / £18,128 |
| equal weight | 12.43% / £20,208 | 12.29% / £19,997 |
| benchmark (market) | **12.63% / £22,135** | **12.56% / £22,080** |

Two engines, two cost levels, six completed candidate trials: nothing beat
simply buying the market with every contribution. Breakout came closest in
C# and still lagged by ~1 point per year. Because these trials are now
observed, this panel must not be used to tune a variant and re-test it as if
fresh: with this many trials a winning variant would need deflated-Sharpe /
multiple-testing scrutiny (Bailey & López de Prado), not a victory lap.

### A3. Momentum adds return — **not here; it reduced drawdown only**

Python momentum matched the benchmark on return (12.50% vs 12.63%) with a
materially smaller maximum drawdown (−18.5% vs −29.3%). That is a risk
profile worth a future registered test on tradable data, not a return edge.

### A4. Trading costs are negligible at £20/month — **rejected for high turnover**

The Python full-rebalance ridge paid £2,369 in costs on £4,660 of
contributions at base rates, and doubling rates cut its return from 8.04%
to 4.83% per year. The C# contribution-only design paid ~£9 total because it
almost never sells. Cost control at this account size is a turnover
decision, not a fee-negotiation decision. This validates keeping
contribution-only as the only execution-eligible allocation policy.

### A5. Concentrating on ex-post winning sectors accelerates the goal — **rejected as a method**

Buy-and-hold over the development window: HiTec returned 16.8%/yr and Shops
14.3%/yr; the other eight industries lagged the market's 12.5%/yr, Energy by
4.3 points. Picking HiTec in 2005 would have won; the method "pick a sector"
had a 2-in-10 hit rate before costs. This is the industry-level shadow of
Bessembinder's stock-level result (most stocks underperform Treasury bills
over their lives; a small minority accounts for net wealth creation): the
median concentrated bet compounds slower, which lengthens the expected time
to £1m even though the winning branch shortens it.

## Product consequences

1. The benchmark contribution strategy — buy broad market exposure monthly,
   sell nothing — is the incumbent every candidate must beat in a registered
   study on tradable point-in-time data. Nothing currently does.
2. Live execution now requires a registered-study final run
   (`execution-study-required`); ordinary runs remain demo-only. The weight
   cap is a purchase cap: drifted holdings are never added to, and new
   contributions buy underweights (cash-flow rebalancing) instead of idling.
3. Research priority stays issue #5 (survivorship-free point-in-time data).
   Without it, stock-selection claims are fiction (A5). Family C exposure
   rules from [the growth plan](growth-research.md) are the cheapest next
   registered test because they need only a broad index series.
4. Do not tune ridge/momentum/breakout further on this French panel. It has
   now answered the questions it was declared for.

Reproduce: `uv run twenty backtest --data data/real-research/imports/363ff59017999fd4b20839b7b4218f8c1759d849fb8a57dbf41f677b2fe5fce6.csv --top-k 4`
(and with `--fx-bps 30 --half-spread-bps 10 --slippage-bps 10` for stress).
Runs are recorded in `runs/experiments.jsonl`; the reserved final 24 months
remain unspent.
