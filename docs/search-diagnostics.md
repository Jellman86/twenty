# Candidate-family uncertainty diagnostics

Implemented on `dev`, 15 October 2026. This is a retrospective analysis of recorded development returns. It does not nominate a champion, open the reserved holdout or authorise execution. The current bundled data remain synthetic.

## Research basis

Politis & Romano (1994), **The Stationary Bootstrap**, motivates resampling consecutive observations with geometrically distributed block lengths. The implementation starts at a uniformly chosen month, then either continues to the next month with circular wraparound or restarts uniformly with probability `1 / expectedBlockMonths`. A shared index path is applied to the entire multivariate return vector. Full algorithm/proposition passages on pages 1303–1305 were inspected in the [published-paper scan](https://users.ssc.wisc.edu/~behansen/718/Politis%20Romano.pdf); [publisher record](https://doi.org/10.1080/01621459.1994.10476870). Stationarity and sufficient history remain assumptions, not properties established by this software.

Bailey, Borwein, López de Prado & Zhu's **Probability of Backtest Overfitting** motivates retaining complete candidate return streams and examining selection across alternatives. Its CSCV statistic is not implemented by this diagnostic. [Author manuscript](https://www.davidhbailey.com/dhbpapers/backtest-prob.pdf). Bailey & López de Prado's **Deflated Sharpe Ratio** additionally requires return moments and assumptions about independent trials; the raw registry count is not an effective independent-trial count. This implementation does not compute DSR. [Author manuscript, including Appendix 3](https://www.davidhbailey.com/dhbpapers/deflated-sharpe.pdf).

The method below is an explicit Twenty diagnostic using stationary resampling and a joint maximum-deviation statistic. It is not claimed as a replication of PBO, DSR or White's Reality Check. It cannot correct trials that are missing from the recorded family.

## Inputs and family

Select a completed development run in the Growth lab and request **Analyse the recorded candidate family**. The server snapshots all locally registered attempts, including cancelled/failed ones. The caller cannot supply a shortlist. All completed runs with the anchor's exact dataset, core assembly hash, monthly contribution, cost schedule, allocation policy and reserved-window length enter the family. Other attempts remain listed with exclusion reasons and hashes; they have not disappeared from the research search. Different model hypotheses, training windows, refit settings, concentrations and regularisation values can enter the same family.

Every included report is read from its immutable hashed artifact and checked against its run contract. The analysis uses the intersection of their recorded development calendar windows, requires at least 24 consecutive months, and never pads missing returns. Each account retains its original pre-intersection holdings/cash history; this is not a new reset-account experiment. There is no reserved-price read or refit. Comparing runs that previously used different holdout lengths does not restore unseen data: they are excluded from this family and remain visible.

Imported provenance declarations and exact hashes establish integrity, not trustworthy point-in-time coverage. A changed metadata file creates another dataset identity; searches across such identities, other engine versions, output roots or manual data inspection are outside these bands. No entire-programme multiplicity claim is made.

## Output

For every candidate versus benchmark, momentum, equal targets and zero-interest cash, compute `12 × mean(candidate monthly net return − comparator monthly net return)`. These are annualised arithmetic mean differences in percentage points. They are not compound growth rates, risk-free-adjusted Sharpe ratios, prediction scores or probabilities of reaching a wealth target.

For each fixed expected block length of 3, 6 and 12 months, generate 2,000 joint resamples with seed 212. The pointwise range is the 2.5th–97.5th percentile of each resampled mean. To account for comparing the included family, each replicate also records the maximum absolute centred mean deviation across **all candidates and all four comparators**. Its 95th percentile supplies a common radius around each observed mean. This is an approximate simultaneous band within that block setting, not simultaneous coverage across the three sensitivities. Quantiles use linear interpolation; the recorded method version, worker commit and diagnostic assembly hash identify the implementation/runtime build.

Repeated identical return streams do not become independent observations: they use the same resample indices and do not enlarge the maximum. Incomplete trials cannot supply return paths; their presence is disclosed and prevents treating the scoped family as the whole programme. A range wholly above zero is not an automatic promotion criterion.

Short histories can contain very few expected blocks. Weak dependence, stable distributions, finite moments and the bootstrap approximation may all be inappropriate for concentrated speculative returns. These ranges hold fitted models and the selected universe fixed; they omit model-estimation, provider, survivorship, delisting, execution and structural-regime uncertainty. Use all three sensitivities, cost stress and new prospective evidence; do not pick the most flattering band.

The reported expected block starts are `1 + (months − 1) / expectedBlockMonths`: the initial block plus expected restarts. This is not an effective independent sample size.

## Persistence and bounds

`POST /api/research/diagnostics` takes `{ "requestId": "UUID", "anchorRunId": "completed-run-id" }`. Requests require the existing same-origin header. A repeated request returns the same attempt; conflicting anchor reuse fails. `GET /api/research/diagnostics` lists the latest 20 summaries and total count; `GET /api/research/diagnostics/{id}` returns the full snapshot/result. No credentials or private provider payloads are accepted by this API.

The separate SQLite journal persists admitted attempts before analysis, including failures, cancellations and interrupted requests. Restart marks a running analysis interrupted; it is not silently rerun. Successful payloads include dataset/synthetic status, register-snapshot time, all local trial/config/report/engine hashes, analysis code identity and every sensitivity. Stored payload hashes detect corruption. A repeated analysis is another visible attempt; it is not a new independent market history.

Bounds: one diagnostic at a time, two-minute deadline including report loading, at most 64 compatible candidates, 200 attempts, 1 MiB per journal payload and 32 MiB total journal payloads. Excess candidates fail rather than silently trimming the family. Reports are extracted one at a time. Analysis and an explicitly requested training job can share the container's single CPU; neither runs periodically or requires a GPU.

The separate [registered study workflow](registered-studies.md) now declares a bounded cost-stress family, freezes a choice and spends one final pair. Genuine data acceptance, whole-programme search correction and prospective promotion remain open in [issue 7](https://github.com/Jellman86/twenty/issues/7). The standalone retrospective diagnostic does not itself perform those actions; existing broker requirements remain unchanged.
