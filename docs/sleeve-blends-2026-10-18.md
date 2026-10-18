# Strategy sleeves — declared blend tests, 18 October 2026

Twenty can now split every contribution across strategy sleeves:
hypothesis `sleeve-blend` with a spec such as
`benchmark:0.7,core-satellite:0.3`. Sleeves: `benchmark` (the market),
`equal_weight`, `cash`, `momentum`, `breakout`, `core-satellite` and
`ridge-relative-return` (the trained model). Fractions must be positive,
named once, and sum to 1. `upside-tail` is excluded because its six-month
label horizon needs its own training contract. Selection assets stay
capped at the purchase target; the market sleeve is capped only by its own
declared share. Sleeve-blend is backtest-only for now: forward signals
reject it with `signal-hypothesis` until a blended forward-scoring
contract is designed.

Blends were declared before results: five blends × base/stress costs on
both real panels, TopK 4, maximum weight 25%, contribution-only,
development windows only. Raw output:
`data/real-research/sleeve-blends-2026-10-18.json` (private).

## Results (base costs, annualised return / ending equity on £4,660)

| Blend | Industry 10 | Size/value 25 |
|---|---|---|
| Market benchmark (comparator) | 12.63% / £22,134 | 12.63% / £22,134 |
| Sensible 70% market + 30% core-satellite | 12.55% / £21,686 | 12.10% / £20,150 |
| Barbell 80% market + 20% breakout | 12.71% / £22,045 | 12.46% / £21,370 |
| Model-heavy 50% market + 50% ridge | 12.92% / £22,074 | 11.58% / £18,296 |
| Kitchen sink 40/20/20/20 | 12.59% / £21,213 | 11.34% / £17,893 |
| All-crazy control (no market) | 12.48% / £20,647 | 11.00% / £16,979 |

Stress costs change nothing in the ordering (each blend loses ~0.07
points, the same as the benchmark).

## Verdicts

- **Blending works mechanically and creation is not magic.** Every blend
  landed between the market and its active sleeves, exactly as weighted
  arithmetic says. A sleeve allocation chooses how much benchmark return
  to exchange for satellite behaviour; it cannot manufacture the missing
  edge.
- **No blend beat the market on both panels.** Model-heavy and barbell
  edged ahead on industries (+0.29 and +0.08 points — inside any honest
  noise band, and model-heavy still ended with less money because
  time-weighted return ignores contribution timing), then lost by 1.05 and
  0.17 points on size/value. Cross-panel disagreement at this size is
  what "no robust effect" looks like.
- **The sensible 70/30 is the least-regret active default.** It cost
  0.1–0.5 points per year against pure market in exchange for keeping a
  researched satellite alive at bounded weight. If an active sleeve is
  wanted for engagement or future evidence, that is the honest price
  measured here — pay it knowingly or hold 100% market.
- **The all-crazy control is the warning label.** Removing the market
  sleeve cost 1.6 points per year on size/value. Crazy is a seasoning
  with a measured price, not a base.
- Drawdowns did not improve by blending on size/value (blends −30% to
  −37% vs market −29%); on industries they were marginally shallower.
  Do not sell sleeves as crash protection on this evidence.

## Use in the workbench

Pick "Split each contribution across strategy sleeves", enter the spec,
and run as any other experiment. The trial register counts every blend
tried. With these ten declared configurations now observed on both
panels, further blend tuning on the same panels is search, not evidence:
any winner picked from here needs unseen or forward data before it means
anything.
