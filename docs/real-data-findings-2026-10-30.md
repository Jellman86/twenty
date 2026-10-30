# Real-data findings · 30 October 2026

This document summarises what the project's real-data runs have shown so far. It is written for the owner's question "how far along are we?" It does not provide a profitability claim, and it does not authorise trading.

## 1. Industry rotation and the app's models did not beat the market (18 October runs)

The production register holds nine development trials on the `French industry portfolios — research only` panel (`363ff590…`). The panel contains ten value-weighted French industry portfolios plus the French market, in GBP via ECB reference rates, from January 1999 to August 2026. These series are CRSP-based and survivorship-free at portfolio level.

The trials put £20 a month into the top four industries chosen by each strategy, with contribution-only holdings. Development ran from 2005-08 to 2024-07 (£4,540 contributed). The last 24 months were left unevaluated.

| Strategy (4 holdings) | Base costs | Stress costs |
|---|---|---|
| **Market, bought monthly (benchmark)** | **£21,267** | **£21,213** |
| Breakout | £18,103 | £18,059 |
| Equal weight, all ten industries | £16,463 | £16,393 |
| Momentum (12-1) | £13,698 | £13,665 |
| Ridge relative-return model | £13,428 | £13,395 |
| Tree relative-return model (2005-03 → 2024-08, £4,660) | £14,982 | — |

The two `upside-tail` trials failed and remain in the register. No strategy reached the plain market. The closest, breakout, ended about 15% lower. The app's own trained models finished last.

**Limits:**

- These portfolios are research proxies, not ETFs.
- The US market from 2005 to 2024 was unusually concentrated in a few large technology stocks, which favours the benchmark over equal-weight and industry tilts.
- These are nine development trials, not a registered final test.
- Even so, the result is consistent with the literature the project relies on ([evidence review](literature-review.md)): after costs, simple selection rules rarely beat a broad index.

**Practical reading:** on current evidence, after both the industry runs and the trend rules below, the strategy for a £20/month ISA with the most support is regular purchases of a broad index fund. Any bot strategy should be compared with that baseline first.

## 2. Trend rule on market vs T-bills

Declared and pushed before running, in `760ae53`: [Family C trend-rule declaration](family-c-trend-declaration-2026-10-30.md). The eight trials ran on 30 October in a private local workspace. They are not in the production register, for the reason given in the declaration's disclosure section.

**Inputs:**

- Panel `cb17dd1f…` (`french-market-tbill-gbp-v1`), built from a fresh French factors capture and ECB history.
- Development window 2005-08-31 → 2024-07-31, £4,540 contributed. The last 24 months were not evaluated.
- Cross-check: the market benchmark ends at £21,267.13 here against £21,266.72 in production's industry runs, which used an older French vintage.

| Trial | Rule | Base | Stress | Worst fall | Months in T-bills | Switches |
|---|---|---|---|---|---|---|
| — | **Market, bought monthly** | **£21,267** | **£21,214** | −29.3% | 0 | 0 |
| C1 | 12-1 momentum, switch everything | £13,770 | £12,438 | −18.8% | 50 of 227 | 26 |
| C2 | 12-month-high breakout, switch everything | £6,261 | £5,411 | −25.3% | see note | 22 |
| C3 | 12-1 momentum, new money only | £20,814 | £20,761 | −24.6% | 50 of 227 | 26 |
| C4 | 12-month-high breakout, new money only | £20,642 | £20,589 | −21.2% | see note | 22 |
| — | 50/50 market and T-bills | £11,440 | £11,359 | — | — | — |

**Decision (declared rule):** all four are **rejected**. The worst-case margins against the best comparator, which was the market each time, are:

- C1: −£8,777
- C2: −£15,803
- C3: −£453
- C4: −£624

Nothing nominates for forward testing.

**Reading the results:**

- **C1, full switching.** The rule did what trend rules promise: it reduced the worst fall from −29% to −19%. It paid for that with a final value about 35% lower. The switches cost about £800 in base costs, and the rule stayed in T-bills through rebounds such as 2009 and 2020.
- **C3 and C4, new money only.** These came within about 2–3% of the market, because nothing is sold and most deposits still go to the market.
- **Disclosed mismatch for C2 and C4.** The engine's `breakout` buys an asset only when it trades **above** its prior 12-month high. Otherwise that month's money stays in zero-interest cash. The declaration described C2 as holding "whichever is closer to its prior high", which missed the idle-cash case. In GBP terms the T-bill series moves with the exchange rate, so both assets were often below their highs. C2 therefore held only cash in 112 of 227 months. No modified variant was run after seeing this; one would be a new, disclosed trial.

**Limits:**

- The returns are from a research index, not an ETF.
- Costs are flat scenario assumptions, and the stress setting doubles FX on every switch.
- The market's 2005–2024 path was already known from the industry runs' benchmark, so this was not an unseen test.
- One period cannot rule out a regime where trend rules help, such as a long bear market. The rules did not help in this one.

Reports, hashes and requests are kept outside Git under `artifacts/twenty/real-data-goal-20261030/`.

## What remains unknown

- Whether any rule survives a fresh, unseen period. The reserved 24 months are still unspent, and forward paper testing on current prices has not started.
- Individual-stock strategies (growth plan Family A). Free data cannot provide a survivorship-free stock history before about 2015 ([data selection](market-data-selection.md)).
- Real ETF costs, tracking error and Trading 212 execution.
