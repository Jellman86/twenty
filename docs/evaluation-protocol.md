# Model and evaluation protocol

Status: proposed research protocol, reviewed 15 October 2026. Only the features and checks explicitly marked implemented in the README exist today. This document specifies the next experiments; it does not certify an investment strategy.

The native [registered first study](registered-studies.md) now implements a finite upfront hypothesis/concentration/cost family, common development dates, a one-month selection gap and a one-time final pair. Its fixed wealth-margin screen is a declared engineering/research convention, not a universal approval threshold. Point-in-time data certification, wider model families and prospective promotion remain outstanding.

## Research question

Does a locally trained model improve the net outcome of a small GBP, long-only portfolio beyond simple strategies, and does disclosure-change information add value beyond numerical features? The £1 million aspiration is a separate scenario calculation, not a training target or an instruction to maximise risk.

Track two different evaluations: a standardised return experiment for scientific comparison, and an account simulation with £20 monthly deposits, real minimums and costs. Identical deposits within a comparison are mandatory. Do not confuse higher ending wealth from deposits or timing with superior time-weighted investment returns.

## Hypotheses and model budget

| ID | Hypothesis | Fixed initial comparator | Status |
|---|---|---|---|
| H0 | Numeric models add value beyond simple allocation | Same-universe equal weighting, lagged momentum, broad benchmark | Ridge/small MLP and comparators implemented on synthetic fixtures |
| H1 | Publicly available profitability adds value to price signals | Same model and universe without profitability | Planned; requires point-in-time fundamentals |
| H2 | Filing changes add value beyond numeric features | Numeric model; numeric plus deterministic section-difference score | Planned; requires archived comparable filings |
| H3 | Learned text extraction improves the filing signal | Deterministic differences with the same timestamps/features | Planned; extraction and economic-value tests are separate |
| H4 | Exposure scaling changes risk/utility usefully | Identical unscaled portfolio; simple momentum/volatility alternative | Planned; not assumed to improve returns |

Start with ridge, one CPU gradient-boosted tree family and the existing small MLP. The tree challenger is not implemented yet. Predefine three regularisation/complexity settings per family at most for the first formal study; count all seeds, lookbacks, feature sets and allocation variants. This is a project search budget, not an academically validated universal optimum. Larger models may be tested later when these controls are credible.

Freeze labels, feature definitions, sample, universe and costs before looking at model rankings. A one-month relative-return label is the initial supervised target. Test longer horizons only as explicitly registered new trials with overlapping labels purged at every boundary.

## Time and splits

1. Build the universe and features as known at each historical date; preserve inactive securities and delisting outcomes.
2. Fit imputation, scaling, feature selection and all calibration using training observations only. Missingness patterns cannot be filled from later releases.
3. Use chronological expanding-window evaluation, plus a rolling-window sensitivity check to assess regime dependence. Keep all assets from a given decision date in the same temporal split.
4. Train only on labels whose complete outcome and public data were available before the evaluation decision. The prototype uses a strict gap and features lagged by a whole monthly observation; a later faster execution rule needs its own timing proof.
5. Tune within training/inner chronological validation. Outer evaluation is not an unlimited development set.
6. Reserve the final 24 monthly intervals for a single declared candidate evaluation. The length is an initial convention and may provide inadequate statistical power; it is not proof that two years establishes a durable edge.
7. Record every holdout access. After inspection, that interval cannot be reused as unseen evidence for a revised candidate. Prospective observations provide new evaluation data.

A modern pretrained language model creates another timing problem: its weights may contain later information. A historical point-in-time feature file does not remove that knowledge. Prefer frozen snapshots and truly subsequent tests for return forecasting; maintain a separately justified contamination assessment for any historical text extraction/embedding experiment.

## Portfolio and benchmarks

Always report the broad benchmark, same-universe equal weighting, simple momentum and the model portfolio. Add a no-trade/cash reference when realistic cash remuneration is available. The external benchmark tests the user's alternative; equal weighting tests value beyond holding the selected research universe. Neither alone is sufficient.

Use long-only unleveraged positions initially. Do not import a paper's short leg, leverage or gross alpha into expected ISA performance. Respect instrument eligibility, quantity precision, £1 minimums, currency and stale-price controls. Rebalance with contributions where possible; test a predefined no-trade band against full target rebalancing. Report actual exposure and concentration, including whether risk-adjusted comparisons are driven by holding more cash.

The present fractional-notional simulator and flat cost schedule are approximations. A production-quality execution model needs security-level fees/taxes, executable prices, dividends/withholding, corporate actions, order rejection/partial fills and cash settlement rules. Its results cannot yet certify a tradable strategy.

## Metrics and uncertainty

Report ending GBP value, contributions, net profit, time-weighted compound returns, maximum drawdown, recovery time, realised exposure, turnover, cost drag and active returns against each benchmark. Report rank information coefficients and prediction errors as diagnostics; neither is the investment objective. Include information ratio and risk-free-adjusted Sharpe only with matching return-frequency conventions and a documented cash/risk-free series.

Use date-block resampling or other justified dependence-aware inference for active returns; individual stocks on the same date are not independent market histories. Report intervals and power limitations rather than selecting a significant-looking point estimate. Stress observed crisis/rebound periods and leave-sector/region tests where the data support them; label added windows as new research choices.

Maintain complete candidate return streams and trial metadata. The [candidate-family diagnostic](search-diagnostics.md) now supplies paired stationary-bootstrap mean-active-return ranges and approximate simultaneous bands within a disclosed compatible family. It retains excluded/failed attempts and fixed block-length sensitivities; it is retrospective and does not correct unrecorded or excluded searches. Plan deflated Sharpe and probability-of-overfitting diagnostics with explicit assumptions about dependent trials and non-normal returns. Those named statistics remain unimplemented. A single adjusted statistic must not approve the strategy automatically or be treated as a repair for survivorship/future-information leakage.

## Falsification and cost checks

- Change prices or disclosures after a decision and confirm earlier predictions do not change.
- Use date-aware label/predictor placebos, negative-control features and deliberately absent relationships; do not claim a simple global shuffle preserves time dependence.
- Compare a complex model with simple momentum/volatility substitutes before attributing performance to learning.
- Test signal/feature ablations, publication-period sensitivity and realistic processing delays.
- Show actual per-instrument base costs and a stress scenario with doubled variable execution costs. The doubling is a predeclared sensitivity, not a universal worst case.
- Include the research programme's data, compute, hosting and electricity budget; distinguish expenses funded externally from account expenses.

## Promotion gates

Historical research may nominate a candidate only after passing data/timing audits, fair comparisons, cost sensitivity, search-aware uncertainty analysis and the declared final evaluation. There is no universal Sharpe or p-value that substitutes for those checks.

Forward paper trading should record predictions, feature cutoffs, model hashes, decisions and proposed orders before outcomes. Six to twelve months can test operations and expose discrepancies but usually cannot establish persistent alpha. Paper fills are not real fills. Retraining creates a challenger; it must not silently replace the champion because it fits recent data better.

A subsequent live capability requires its own reviewed engineering stage: read-only account integration, instrument validation, reconciliation, deduplication after timeouts, hard cash/exposure limits and a kill switch. No live broker connection or scheduled Quark job is created by this protocol.
