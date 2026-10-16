# Growth and catalyst research plan

Drafted 16 October 2026. Status: **proposal only; nothing here is implemented.** It translates the extreme-winner, biotechnology and timing evidence in [the literature review](literature-review.md) (P25–P39) into experiments Twenty can falsify. It does not establish that any of them will work.

## Questions

1. Can point-in-time data rank stocks by the probability of a 10x gain over five to ten years, **without** selecting a pool whose wipeouts outweigh its winners?
2. Can public clinical-trial and regulatory data estimate trial or approval outcomes better than the market price already implies?
3. Do stop-loss or trend rules improve a contribution portfolio out of sample after costs, compared with staying invested?

Each question is a separate hypothesis family with its own registered study and broad-exposure benchmark. They are not combined until each has evidence on its own.

## What the evidence rules out

- **Learning from winners alone.** Studying stocks that became multibaggers (P31) cannot give the probability that a similar stock does. Every model starts from all eligible stocks at each formation date, including later delistings and bankruptcies.
- **Upside traits as buy signals.** Recent extreme daily gains (P26), high expected skewness (P27), retail attention (P28) and recent equity issuance (P29) each predict *low* average returns. They are required features and falsification checks, with the sign learned from data and not assumed.
- **Predicting crashes and rallies.** Most published market-timing predictors fail out of sample (P36). Stop-losses lose expected return unless returns are persistent (P37). Timing rules are therefore tested as exposure rules under measured persistence, not as forecasts of the next move.
- **Treating a forecast as an edge.** An accurate approval model (P33) is only useful if it disagrees with the price in the right direction.

## Family A — multibagger and wipeout scoring

- **Labels:** at each annual formation date, whether the total return over the next 5 and 10 years reaches ≥10x, and separately whether it loses ≥90% or delists. A pure 100x label is too rare for useful estimation in a few decades of data; report it descriptively only.
- **Candidate features (timestamped):** size, book-to-market, gross profitability (P06), free-cash-flow yield, investment/asset growth, GSCORE components (P30), momentum (P05), MAX (P26), idiosyncratic volatility, listing age and recent issuance (P29), sector.
- **Census first:** before any model, count every ≥10x outcome by sector, starting size, listing age and formation year, *with the denominator* of all eligible stocks in each group. This answers where multibaggers came from without selecting on the outcome, and shows how much depends on starting near a market trough.
- **Models:** base rate; logistic regression; a gradient-boosted tree. Nothing larger until these are measured.
- **Metrics:** precision and wipeout rate in the top decile versus the base rate; calibration; the realised return of a top-k portfolio net of costs compared with broad exposure and same-universe equal weight; how often selections compound below cash (P25).
- **Evidence limit:** five- or ten-year labels from roughly 25–30 years of data give only a handful of non-overlapping windows. Report the effective sample, use overlapping windows only with dependence-aware intervals, and expect wide uncertainty.

## Family B — clinical-trial catalysts

- **Sources:** ClinicalTrials.gov (API v2), dated database snapshots such as CTTI's AACT where licensing permits, openFDA Drugs@FDA, FDA advisory-committee calendars and SEC 8-K filings via the existing collector. Verify whether each source exposes record history; a field edited after an outcome would leak it.
- **Steps:** (1) recompute phase-transition base rates from these sources and compare them with P32; (2) train an outcome classifier with imputation (P33) and compare it with HINT/TrialBench baselines (P34) on dates Twenty can reproduce; (3) only then, an event study of announcement returns by sponsor type (P35) and a test of whether model probability minus price-implied expectation predicts the announcement-window return.
- **Risk controls:** single readouts are binary. Record exposure per catalyst date, cap any one position, and report the loss distribution, not just the mean.
- **Instrument scope:** many small biotechs trade on venues or at sizes the Trading 212 ISA may not offer. Before modelling, record which candidates were actually tradable in the account; untradable winners do not count.

## Family C — exposure rules

- **Candidates:** a long-only, unleveraged trend filter on a broad index (P39); per-position stops inside the momentum strategy (P38, P12); volatility scaling, already under review (P10, P11).
- **Comparison:** the same contributions held continuously. Report time out of the market, number of switches, re-entry slippage, worst drawdown and final value.
- **Registration:** thresholds and lookbacks are declared before testing; every variant counts in the search (P20–P22).

## Data that must exist first

This plan increases, rather than reduces, the importance of issue #5. Multibagger candidates are mostly small, young companies, which are where delisting returns, splits and missing histories concentrate. Without a survivorship-free universe, Family A will report fictitious winners. Scraped data with no history (forums, trending lists) can only be collected forward and evaluated as forward paper predictions; it cannot be backtested. Follow [the scraping policy](data-sources.md#scraping-policy-and-failure-handling).

## Order

1. Survivorship-free price and fundamentals history (#5) and contribution-accounting parity (#6).
2. Family C on broad-index data: cheapest, needs only prices.
3. Family B base rates and classifier, which need no price history beyond sponsors' shares.
4. Family A once point-in-time fundamentals and delistings are verified.
5. Forward paper evaluation of any surviving candidate before any change to execution.

## Not in scope

No real-money execution, leverage, options or short selling. No claim of probabilities for reaching £1 million. Automatic buying and selling stays behind the existing broker gates in [the execution contract](broker-execution.md).
