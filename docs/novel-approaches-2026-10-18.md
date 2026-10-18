# Novel approaches — tested 18 October 2026

Configurations were declared before any size/value results were seen: the
same TopK 4, maximum weight 0.25, contribution-only, base/stress costs as the
earlier industry-panel family, development window 2005-03-31 to 2024-08-31
(233 × £20 = £4,660), reserved holdout untouched. Trials: 8 on the new
size/value panel plus 2 core-satellite runs on the industry panel. Raw
output: `data/real-research/novel-approaches-2026-10-18.json` (private).

## New data: French 25 size × book-to-market portfolios

`french-size-value25-gbp-v1` (panel `05606cba…ac6a`) converts French's 25
value-weighted size/BEME portfolios to GBP exactly like the industry panel.
Symbols are `FF25_ME{size 1..5}_BM{1..5}`: ME1 smallest, ME5 biggest;
BM1 growth, BM5 value. Research-only, same restrictions as the industry
panel. Capture: `capture-public --sources french-size-value25-monthly`;
build: `french-panel … --portfolios size-value25`.

## N1. "Buy small growth" — **rejected decisively**

Single-portfolio census, £20/month into one portfolio, base costs.
Annualised time-weighted return and ending equity:

| Portfolio | Return/yr | Ending | Max drawdown |
|---|---|---|---|
| Big growth (ME5 BM1) | **15.27%** | £32,650 | −18.8% |
| Market | 12.63% | £22,135 | −29.3% |
| Small value (ME1 BM5) | 11.91% | £20,888 | −53.5% |
| Big value (ME5 BM5) | 10.90% | £17,467 | −35.7% |
| Small growth (ME1 BM1) | **4.25%** | £7,202 | −57.4% |

Small growth finished **last of all 25**. This is the textbook result, not
bad luck: small, expensive, fast-growing firms combine lottery demand,
aggressive investment and low profitability — the three characteristics the
Fama–French five-factor work and the asset-growth literature associate with
the lowest average returns. Whatever Twenty does next, it must not select
for "small and growing fast" on valuation-blind growth traits. If small
companies are ever used, the candidate traits from the literature are the
opposite corner: profitable and conservatively invested.

## N2. "Buy big growth / high cap" — **won this window; not a method**

Big growth was the best portfolio of the 25, and growth (BM1/BM2) beat value
at every large size. Only 5 of 25 portfolios beat the market at all. But
2005–2024 is the mega-cap technology and falling-rates regime; before 2005
the same table mostly ran the other way, which is why value became famous.
Choosing ME5 BM1 after reading this census is performance chasing with a
20-year lookback. Two further facts close the door:

- The cap-weighted market already captures big growth automatically: when a
  company compounds into a giant, its market weight rises with it. Owning
  the market *is* the high-cap growth capture mechanism, with no selection
  decision to get wrong.
- Momentum was supposed to find the winning corner mechanically. On this
  panel it returned 9.84%/yr — the worst strategy tested. Monthly ranking
  churned instead of holding the winner.

A static large-growth tilt may only enter Twenty as a preregistered
hypothesis on data not used to suggest it. It has not earned anything else.

## N3. Core-satellite candidate — **kept as chassis, creates no return**

New `core-satellite` hypothesis: half of each contribution buys every asset
equally (the core cannot miss a future winner), half follows the momentum
top-k (the satellite), per-name cap enforced. Results (candidate,
annualised; benchmark in brackets):

| Panel | Base | Stress | vs equal weight | vs momentum |
|---|---|---|---|---|
| size/value 25 | 10.89% (12.63%) | 10.82% (12.56%) | below 11.66% | above 9.84% |
| industry 10 | 12.61% (12.63%) | 12.53% (12.56%) | ≈ 12.60% | above 10.11% |

It behaved exactly as designed: between its two ingredients on both panels,
near-benchmark where momentum was merely weak, dragged where momentum was
poor. That makes it the right *shape* for any future satellite that proves
itself — damage is bounded at half weight — and no evidence of an edge by
itself. At £20/month across 25 names, most core buys start below the £1
minimum and only become investable as NAV grows; the satellite is
disproportionately what actually trades early. Recorded so this is not
rediscovered later as a surprise.

## N4. Trained ridge on 25 portfolios — **risk profile noted, still behind**

Ridge returned 11.60%/yr vs the benchmark's 12.63%, but with volatility
11.8% vs 13.9% and maximum drawdown −18.7% vs −29.3% (return/volatility
0.98 vs 0.91). On the industry panel the same model trailed on every
measure. One panel's risk profile is a hypothesis for a registered study on
tradable data, not a finding to trade on. Breakout (9.85%) and momentum
(9.84%) confirmed their industry-panel weakness is not panel-specific.

## What "outside the box" survives contact with the data

1. **Own everything, concentrate nothing by default.** Every selection rule
   tested — trained, momentum, breakout, blended — lost to the market in
   both engines, on both panels, at both cost levels: 18 completed
   candidate trials in total across this and the assumption tests. The market portfolio's automatic drift into
   winners is the thing to beat, and it is genuinely hard to beat.
2. **The remaining honest accelerations are boring:** start earlier,
   contribute more when possible, keep turnover (and therefore costs) near
   zero, never interrupt compounding for a strategy switch that has not
   passed a registered study. £20/month at the market's panel rate of
   12.6% for 40 years is ~£230k, not £1m; at £90/month it is ~£1.03m.
   Contribution size dominates every strategy effect measured here by an
   order of magnitude.
3. **Genuinely novel directions that remain testable** (in the growth plan's
   order): point-in-time small-cap census with delistings (#5) to test
   small *profitable/conservative* — the anti-small-growth corner;
   filing-change signals (#8), where the information is not already a price;
   and forward paper tracking, which no backtest can substitute for.
   Trend-following overlays (Faber-style) stay listed but weak: the
   published rule carries data-mining criticism and our own momentum
   results give it no local support.

Reproduce the panel: capture `french-size-value25-monthly`, then
`french-panel --from 1999-01-31 --to 2026-08-31 --portfolios size-value25`.
The strategies are ordinary workbench experiments with hypothesis
`core-satellite`, `ridge-relative-return`, `momentum` or `breakout`,
TopK 4, maximum weight 25%, contribution-only.
