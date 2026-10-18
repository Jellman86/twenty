# Boosted-tree challenger — implementation and declared comparison, 18 October 2026

Advances [#8](https://github.com/Jellman86/twenty/issues/8). The literature
review P01 (Gu, Kelly & Xiu 2020) says Twenty should compare ridge, boosted
trees and a small neural model on identical data, allocations and costs.
Hypothesis `tree-relative-return` is that tree challenger: the same
one-month relative-return labels, expanding training windows, refit cadence,
ranking and caps as `ridge-relative-return`; only the scoring model differs.

## Model contract

`BoostedTreeModel` is deterministic gradient-boosted regression trees with
fixed constants declared before any run: at most 60 trees, depth 3, at least
20 rows per leaf, learning rate 0.1, exhaustive squared-error split search in
feature order with the first best split winning ties. There is no randomness,
subsampling, feature scaling or per-trial tuning parameter; the constants
are part of the hypothesis definition, not config fields, so a trial cannot
silently retune them. Boosting stops early only when residuals vanish or no
split improves the fit.

- Each training fold now also stores a flat, self-validating tree snapshot
  (`BoostedTree` on `TrainingFold`, absent/`null` for other hypotheses).
  Children always follow parents in the node array, so a restored snapshot
  cannot contain a cycle; corrupt snapshots fail with `tree-snapshot`.
  Reports stored before this field existed still load unchanged.
- Forward signals score from the stored tree snapshot and bind it into the
  signal model hash. A tree report whose snapshot is missing fails with
  `signal-model-snapshot`.
- The tree is also a `sleeve-blend` sleeve, fitted once per fold and shared
  with the standalone hypothesis.
- It is **not** in the frozen `contribution-study-v1` family. Adding it
  there would rewrite that study's declared identity; a future study
  version must declare it before any promotion path can use it. Live
  execution therefore remains unreachable for tree reports.
- Python parity: none claimed. Like `core-satellite` and `sleeve-blend`,
  this is a C#-workbench hypothesis; the Python harness keeps its ridge/MLP
  reference role. Twelve new .NET tests cover determinism, an interaction
  target a linear model cannot represent, constant-input behaviour,
  snapshot corruption, chronology, future-perturbation invariance, report
  round-trip/legacy JSON, the tree sleeve and forward-signal scoring.

## Declared comparison

Declared before the runs (plan JSON with `createdBeforeResults`, private
scratch): ridge vs tree, identical config (£20/month, top 4, 25% cap,
60-month training, 12-month refits, contribution-only), base and
doubled-variable stress costs, development windows only, on the synthetic
demo panel, the synthetic winners/crashes stress panel and the French
industry-10 GBP research panel (`363ff590…fce6`, 1999–2026, £4,660
contributed; development 2005-03 to 2024-08). Twelve trials, no selection,
failures retained. The French size/value-25 panel was not available locally
and was not rebuilt; the reserved holdout was not evaluated. The benchmark
row reproduces [sleeve-blend tests](sleeve-blends-2026-10-18.md) exactly
(12.63% / £22,134), confirming the runner matches that methodology.

## Results (annualised time-weighted return / ending equity, base costs)

### French industry 10 — the only real panel (£4,660 contributed)

| Candidate | Return | Ending | Max DD |
|---|---|---|---|
| Benchmark (comparator) | 12.63% | £22,134 | — |
| Equal weight (comparator) | 12.60% | £20,728 | — |
| ridge-relative-return | 11.03% | £17,318 | −29.6% |
| tree-relative-return | 10.75% | £14,982 | −25.0% |

Stress costs change nothing in the ordering (ridge 10.96%, tree 10.68%).

### Synthetic panels (£1,620 contributed, 2006–2012)

| Panel | Ridge | Tree | Benchmark | Equal weight |
|---|---|---|---|---|
| Demo | 0.57% / £1,269 | 3.77% / £1,350 | −0.96% / £1,240 | 2.36% / £1,324 |
| Winners/crashes | −3.60% / £1,235 | 11.46% / £3,841 | −0.95% / £1,721 | 6.20% / £2,089 |

## Verdicts

- **The challenger is genuinely different machinery.** On authored data
  containing nonlinear structure it finds returns the linear model cannot
  represent (winners/crashes panel, and the pure-interaction unit test
  where the tree scores 100% sign accuracy against ridge at chance). That
  proves software behaviour on synthetic inputs only; it is not market
  evidence, per the evaluation protocol.
- **On the only real panel, nonlinearity bought nothing.** The tree
  finished 0.28 points per year behind the ridge time-weighted, and
  £2,336 behind in money because its picks also did worse as contributions
  grew. Both models lost clearly to the benchmark and to equal weight,
  extending the [assumption-test](assumption-tests-2026-10-18.md) A2
  verdict to the nonlinear case on this panel. One research-only industry
  panel with five price-derived features cannot settle P01 either way; it
  can and does reject "just add a tree" as an upgrade here.
- **Costs cannot distinguish the models under contribution-only.** Traded
  notional is the monthly contribution whichever names are picked, so both
  paid identical costs (£11.62 base on the French panel). Every difference
  above is selection, not turnover.
- **Cost of compute is bounded and recorded.** Tree runs took ~10× the
  ridge wall time (1.8s vs 0.1s on the French panel, local CPU); every fold
  used all 60 trees (~620–670 nodes). No accelerator was involved.
- With these twelve declared trials observed, further tree tuning on the
  same panels would be search, not evidence. Any future claim needs
  unseen or forward data, and a declared study version that includes the
  hypothesis before results exist.

## Use in the workbench

Pick "Train a boosted-tree relative-return model" and run as any other
experiment, or name `tree-relative-return` inside a sleeve spec. The trial
register counts every attempt. No broker order, demo or live, was placed by
this work.
