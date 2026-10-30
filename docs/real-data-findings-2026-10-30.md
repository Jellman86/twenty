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

**Practical reading:** on current evidence, the strategy for a £20/month ISA with the most support is regular purchases of a broad index fund. Any bot strategy should be compared with that baseline first.

## 2. Trend rule on market vs T-bills

Declared before running: [Family C trend-rule declaration](family-c-trend-declaration-2026-10-30.md). Results are pending.

## What remains unknown

- Whether any rule survives a fresh, unseen period. The reserved 24 months are still unspent, and forward paper testing on current prices has not started.
- Individual-stock strategies (growth plan Family A). Free data cannot provide a survivorship-free stock history before about 2015 ([data selection](market-data-selection.md)).
- Real ETF costs, tracking error and Trading 212 execution.
