# Academic evidence review

Review date: **15 October 2026**. Scope: 24 studies relevant to a small, long-only GBP ISA, local model training, disclosure analysis and trustworthy backtesting. P25–P39 were added on **16 October 2026** for extreme-winner selection, biotechnology catalysts and timing rules. This is a targeted critical review, not a systematic review or an independent replication.

Entries distinguish the material actually inspected. **Sections** means relevant methods/results/discussion passages were inspected; it does not mean every appendix was reproduced. **Abstract** means conclusions are limited to an accessible publisher/author abstract or summary. Publication dates refer to journal issue years where applicable; working-paper versions can differ. No paper supplies a defensible probability that Twenty will turn £20 monthly into £1 million.

## Forecasting and model complexity

### P01 — Gu, Kelly & Xiu (2020)

**Empirical Asset Pricing via Machine Learning**, Review of Financial Studies. Review: author/NBER abstract and publication record; no independent replication.

Trees and neural models improve historical risk-premium forecasts over conventional regressions; momentum, liquidity and volatility are prominent predictors. This motivates nonlinear challengers, not replacing data validation with a large model. The study's asset-pricing setting and portfolio construction differ from a small ISA. Twenty should compare ridge, boosted trees and a small neural model on identical data, allocations and costs; distinguish forecast quality from net portfolio value. A five-feature prototype is not a replication of this study.

[Primary working-paper record](https://www.nber.org/papers/w25398); [published DOI](https://doi.org/10.1093/rfs/hhaa009).

### P02 — Kelly, Malamud & Zhou (2024)

**The Virtue of Complexity in Return Prediction**, Journal of Finance. Review: publisher abstract, theoretical framing and conclusion sections.

The authors develop an argument for richly parameterised return models with suitable shrinkage and provide market-timing evidence. They explicitly distinguish useful nonlinear approximations from arbitrary additions of predictors. The result is not a universal guarantee for every neural architecture or cross-sectional strategy. Twenty should include a bounded regularised nonlinear challenger, keep its search budget visible and measure economic value after costs. Read alongside P03 before interpreting model size as evidence of learning.

[Publisher](https://onlinelibrary.wiley.com/doi/10.1111/jofi.13298).

### P03 — Nagel (2025 working paper)

**Seemingly Virtuous Complexity in Return Prediction**, August 2025 BFI manuscript. Review: introduction, kernel/momentum interpretation, artificial-return/bootstrap tests and conclusion passages.

Nagel challenges the interpretation of short-window random-feature return predictions: persistent predictors can mechanically produce volatility-timed momentum, including when the method does not learn the relevant return relationship. This is a specific critique, not proof that all complex models fail. Twenty should compare complex models with simple economic substitutes, use predictor/label placebo tests and test changing return dynamics. A working paper's critique and a published paper's original findings should both remain visible rather than being presented as settled consensus.

[University-hosted manuscript](https://bfi.uchicago.edu/wp-content/uploads/2025/08/BFI_WP_2025-104.pdf).

### P04 — Chen, Pelger & Zhu (2024)

**Deep Learning in Asset Pricing**, Management Science. Review: publisher/author abstract and publication details.

The method estimates a conditional asset-pricing model with no-arbitrage restrictions, adversarial test assets and economic state extraction. Its target is broader than directly predicting a retail account's next trade. Institutional data, estimation objectives and portfolios create substantial translation work. Defer this architecture until the simpler pipeline works with genuine historical data; do not label Twenty's small supervised network an implementation of this paper. Its useful lesson is that economically motivated objectives can matter as much as network depth.

[Publisher](https://pubsonline.informs.org/doi/10.1287/mnsc.2023.4695); [author preprint](https://arxiv.org/abs/1904.00745).

## Candidate signals

### P05 — Jegadeesh & Titman (1993)

**Returns to Buying Winners and Selling Losers: Implications for Stock Market Efficiency**, Journal of Finance. Review: publisher abstract.

The study documents intermediate-horizon relative-return persistence and subsequent dissipation. The winning-minus-losing portfolio is not equivalent to buying only a few recent winners. Use momentum as a required comparator and a candidate feature; test a long-only implementation with liquidity, sector and turnover controls. Avoid retrospectively choosing the lookback with the best chart. The current lagged price feature is motivated by this literature but does not reproduce the paper's portfolio definitions.

[Publisher](https://onlinelibrary.wiley.com/doi/10.1111/j.1540-6261.1993.tb04702.x).

### P06 — Novy-Marx (2013)

**The Other Side of Value: The Gross Profitability Premium**, Journal of Financial Economics. Review: NBER author abstract and publication record.

Gross profits relative to assets help explain average-return differences and complement valuation information. Accounting definitions, sectors and public release dates matter; revised fundamentals cannot be used as if originally available. Add profitability only after building timestamped statements and sector-aware handling. Compare profitability/momentum combinations with each ingredient alone. A profitability label is not interchangeable with revenue growth, analyst enthusiasm or a high profit margin.

[Primary record](https://www.nber.org/papers/w15940); [published DOI](https://doi.org/10.1016/j.jfineco.2013.01.003).

### P07 — Cohen, Malloy & Nguyen (2020)

**Lazy Prices**, Journal of Finance. Review: publisher abstract and supporting-material listing.

Changes in corporate reports contain information about later operations and returns. The reported portfolio buys non-changers and shorts changers, so a long-only risk filter is a new experiment, not the published strategy. Compare deterministic section differences with learned change classifications using the same public timestamps. Separate routine reporting/template changes from substantive changes, retain supporting passages and evaluate whether an apparent benefit is simply sector, size or quality exposure. This is the first proposed distinctive text research direction.

[Publisher and replication-material links](https://onlinelibrary.wiley.com/doi/10.1111/jofi.12885).

### P08 — Cohen & Frazzini (2008)

**Economic Links and Predictable Returns**, Journal of Finance. Review: author-hosted abstract and introduction.

The paper studies delayed incorporation of information across disclosed customer–supplier links. Relationship discovery is a sensible use of document processing, but a current relationship graph must not be applied retroactively. Historical links, reporting thresholds and public availability must be reconstructed. Defer this extension until the filing pipeline works; compare any relationship signal with own-stock and industry momentum. Its original long-short results cannot be imported as a long-only ISA return expectation.

[Author-hosted paper](https://pages.stern.nyu.edu/~afrazzin/pdf/Economic%20Links%20and%20Predictable%20Returns%20-%20Cohen%20and%20Frazzini.pdf).

### P09 — Cohen, Malloy & Pomorski (2012)

**Decoding Inside Information**, Journal of Finance. Review: NBER author abstract and publication record.

Predictable routine insider transactions carry different information from the transactions classified as opportunistic. Public director purchases are therefore not automatically bullish signals. A future experiment must distinguish open-market purchases, compensation, options and reporting amendments, with signals activated after public disclosure. Historical behaviour used to classify a trader must be based only on their prior record. Treat this as a secondary candidate after statements and price data, not a reason to imitate every disclosed trade.

[Primary record](https://www.nber.org/papers/w16454); [published DOI](https://doi.org/10.1111/j.1540-6261.2012.01740.x).

## Exposure, risk and implementation

### P10 — Moreira & Muir (2017)

**Volatility-Managed Portfolios**, Journal of Finance. Review: author abstract and publisher publication details.

Historical factor portfolios improved risk-adjusted performance when exposure varied with volatility. These implementations can involve leverage and scaling choices unavailable or unsuitable in this ISA. Test a separately specified exposure reduction rule, capped between zero and one, against an unscaled portfolio. Do not promise to prevent losses or miss fewer crashes. P11 provides an important counterweight to treating volatility scaling as a universal improvement.

[Publisher](https://onlinelibrary.wiley.com/doi/10.1111/jofi.12513); [author working-paper record](https://www.nber.org/papers/w22208).

### P11 — Cederburg, O'Doherty, Wang & Yan (2020)

**On the Performance of Volatility-Managed Portfolios**, Journal of Financial Economics. Review: author-hosted published abstract and introduction.

Across 103 equity strategies, volatility management does not systematically dominate unmanaged portfolios; positive spanning-regression alphas need not translate into implementable real-time gains. Structural instability helps explain poor out-of-sample results. This does not invalidate every risk limit, but requires separating risk preference from return enhancement. Twenty should report the cost and missed-rebound effects of scaling and retain the unscaled strategy as a comparator. Avoid full-sample scaling constants that encode later information.

[Author-hosted published paper](https://www.lehigh.edu/~xuy219/research/COWY.pdf); [DOI](https://doi.org/10.1016/j.jfineco.2020.04.015).

### P12 — Daniel & Moskowitz (2016)

**Momentum Crashes**, Journal of Financial Economics. Review: publisher/author abstract.

Momentum can experience persistent losses around rebounds following stressed markets. The paper's dynamic strategy and long-short mechanics differ from an unleveraged retail portfolio. Evaluate stressed declines and subsequent rebounds, not only full-sample averages. Report drawdowns, exposure and recovery time. Do not tune a bespoke crash rule on a single episode or assume a stop order guarantees its trigger price.

[Publisher](https://www.sciencedirect.com/science/article/pii/S0304405X16301490).

### P13 — DeMiguel, Garlappi & Uppal (2009)

**Optimal Versus Naive Diversification: How Inefficient Is the 1/N Portfolio Strategy?**, Review of Financial Studies. Review: publisher abstract.

Across their evaluated datasets, portfolio optimisation does not consistently beat equal weighting once estimation error matters. This is not proof that optimisation never helps, but it makes a same-universe equal-weight comparator essential. Twenty now includes that comparator alongside momentum and the external benchmark. It tests whether model-based selection adds value over simply holding the research universe. Diversification across eight synthetic assets is only a software fixture, not adequate evidence for a production allocation.

[Publisher](https://doi.org/10.1093/rfs/hhm075).

### P14 — Bessembinder (2018)

**Do Stocks Outperform Treasury Bills?**, Journal of Financial Economics. Review: publisher data/method and conclusion snippets, author abstract.

Long-horizon stock returns are highly skewed; a minority of firms accounts for much of aggregate wealth creation. The result explains why concentrated stock picking can miss important winners; it does not reveal those winners beforehand. Twenty should report concentration and compare with broad exposure. A proposed millionaire target must not become a reason to maximise lottery-like payoffs without measuring their probability and potential loss.

[Publisher](https://www.sciencedirect.com/science/article/pii/S0304405X18301521); [author manuscript record](https://papers.ssrn.com/sol3/papers.cfm?abstract_id=2900447).

### P15 — Barber & Odean (2000)

**Trading Is Hazardous to Your Wealth**, Journal of Finance. Review: author-hosted published abstract and introductory methods/results.

Frequent individual trading in their brokerage sample underperformed after costs. Commission levels and market structure differ today, so the historic performance gap is not a forecast for Twenty. The relevant design consequence is to count every cost and compare low-turnover alternatives. Automation should improve consistency rather than create activity. Cash-flow return calculations must not disguise trading losses as growth from new deposits.

[Author-hosted published paper](https://faculty.haas.berkeley.edu/odean/papers/returns/Individual_Investor_Performance_Final.pdf).

## Replication and statistical reliability

### P16 — Hou, Xue & Zhang (2020)

**Replicating Anomalies**, Review of Financial Studies. Review: published results section and author-hosted published abstract.

The published version examines 452 anomalies; many fail common replication standards after mitigating microcaps and correcting multiple tests. Earlier working-paper counts differ and must not be mixed with final statistics. Twenty should test liquidity restrictions, economic magnitudes and net implementation, not just a significant original result. This is evidence for demanding robustness; P17/P18 show why it is not evidence that all return predictors are fictitious.

[Published manuscript](https://global-q.org/uploads/1/2/2/6/122679606/houxuezhang2019rfs.pdf); [DOI](https://doi.org/10.1093/rfs/hhy131).

### P17 — Jensen, Kelly & Pedersen (2023)

**Is There a Replication Crisis in Finance?**, Journal of Finance. Review: author/NBER abstract, publisher framing and publication details.

Using a different replication framework and broad international evidence, the authors reach more favourable conclusions about factor replication. Differences in definitions, weighting and statistical methods matter when comparing replication rates. Twenty should preserve signal-family hypotheses and examine robustness rather than quote a single literature-wide failure rate. International evidence motivates later external validation; it does not substitute for costs, availability and long-only testing in the intended account.

[Publisher](https://onlinelibrary.wiley.com/doi/10.1111/jofi.13249); [primary working-paper record](https://www.nber.org/papers/w28432).

### P18 — Chen & Zimmermann (2022)

**Open Source Cross-Sectional Asset Pricing**, Critical Finance Review. Review: author/Federal Reserve manuscript abstract and project data documentation.

The project supplies documented predictor implementations and portfolio series, with strong reproduction results under its methodology. These resources help specify signals and compare replications. Open predictor or factor portfolios do not supply the complete licensed stock-return, execution and ISA-eligibility dataset Twenty needs. Record the exact data release: the project documents a historical look-ahead bug fix, illustrating why source updates and hashes matter. Do not train on synthetic prices and describe downloaded factor evidence as validation of the same system.

[Federal Reserve manuscript](https://www.federalreserve.gov/econres/feds/files/2021-037pap.pdf); [publisher PDF](https://cfr.ivo-welch.info/published/papers/chen2021open.pdf); [author data documentation](https://www.openassetpricing.com/data/).

### P19 — McLean & Pontiff (2016)

**Does Academic Research Destroy Stock Return Predictability?**, Journal of Finance. Review: published abstract.

The published study of 97 predictors finds lower returns outside original samples and after publication. The decomposition is not a forecast that every strategy loses the same percentage of its return. Twenty should explicitly compare pre-/post-publication periods where feasible, keep recent results separate and avoid calibrating expected returns to original-paper alphas. Publication decay makes forward paper predictions particularly valuable.

[Publisher](https://onlinelibrary.wiley.com/doi/10.1111/jofi.12365).

### P20 — Harvey, Liu & Zhu (2016)

**… and the Cross-Section of Expected Returns**, Review of Financial Studies. Review: author-hosted published abstract and introduction.

Large-scale factor testing requires a higher evidential hurdle than an isolated conventional significance test. Their analysis does not create one universal trading approval threshold for every experiment. Twenty should predefine a small hypothesis family, disclose all attempted variations and correct comparisons for the actual search process. Technical indicators, lookbacks, model seeds and portfolio rules can all contribute to that search, not merely named algorithms.

[Author-hosted published paper](https://people.duke.edu/~charvey/Research/Published_Papers/P118_and_the_cross.PDF); [DOI](https://doi.org/10.1093/rfs/hhv059).

### P21 — Bailey, Borwein, López de Prado & Zhu (2017)

**The Probability of Backtest Overfitting**, Journal of Computational Finance. Review: author abstract/method framing and publisher issue record.

The framework estimates how choosing an in-sample winner can lead to disappointing relative out-of-sample performance. It motivates keeping all candidate return streams rather than only the best report. Combinatorial evaluation is not a substitute for chronological training or a repair for look-ahead data. Twenty should implement search-aware diagnostics alongside forward evaluation; its current append-only local register is helpful bookkeeping, not a computed probability of overfitting.

[Author manuscript](https://www.davidhbailey.com/dhbpapers/backtest-prob.pdf); [publication record](https://escholarship.org/uc/item/4w1110bb).

### P22 — Bailey & López de Prado (2014)

**The Deflated Sharpe Ratio: Correcting for Selection Bias, Backtest Overfitting and Non-Normality**, Journal of Portfolio Management. Review: author manuscript sections on selection, moments and dependent trials.

The statistic addresses selection across trials and non-normal return distributions. It requires assumptions about trial dependence, sample size and return moments; an opaque effective-trial count can make it misleading. Twenty should disclose both raw and adjusted statistics, retain candidate returns and use dependence-aware uncertainty estimates. It must not interpret a high deflated statistic as a guarantee or as a cure for leaked data. This diagnostic remains unimplemented.

[Author manuscript](https://www.davidhbailey.com/dhbpapers/deflated-sharpe.pdf); [publication metadata](https://doi.org/10.2139/ssrn.2460551).

## Language models and contamination

### P23 — Lopez-Lira & Tang (2023 preprint; 2026 journal version)

**Can ChatGPT Forecast Stock Price Movements? Return Predictability and Large Language Models**, Journal of Financial Economics. Review: author preprint abstract/history and indexed publisher abstract/section excerpts; full latest journal text was not accessible.

The research supports testing textual signals. The journal abstract distinguishes an immediate non-tradable reaction from later drift and reports declining returns with adoption. Do not convert a headline hit rate into execution accuracy or assume a licensed timestamped news feed is free. For Twenty, test disclosure/text signals as additions to an existing numeric model, account for processing delay and use genuinely future observations relative to a fixed model snapshot.

[Author preprint](https://arxiv.org/abs/2304.07619); [journal DOI](https://doi.org/10.1016/j.jfineco.2026.104335).

### P24 — Lopez-Lira, Tang & Zhu (2025 working paper)

**The Memorization Problem: Can We Trust LLMs' Economic Forecasts?** Review: author preprint abstract.

The authors document financial/economic memorization and show why historical forecasting before a model's training cutoff can be contaminated. Instructions to ignore later knowledge and masked company names do not reliably establish clean evaluation. Twenty should freeze model snapshots, distinguish extraction from return forecasting and prefer prospective tests for a modern foundation model. Supervised models trained only on authorised historical features avoid this particular foundation-model problem, but still require strict data timing and search controls.

[Author preprint](https://arxiv.org/abs/2504.14765).

## Extreme winners, skewness and lottery demand

Added 16 October 2026 to test the proposed search for 10x/100x stocks. These entries are mostly abstract-level reviews; none was replicated. Read them together: wealth creation is concentrated (P14, P25), but the observable traits most associated with lottery-like upside have low average subsequent returns (P26–P29).

### P25 — Bessembinder, Chen, Choi & Wei (2023)

**Long-Term Shareholder Returns: Evidence from 64,000 Global Stocks**, Financial Analysts Journal. Review: publisher abstract.

Across more than 64,000 global common stocks from 1990 to 2020, most US and non-US stocks compounded below one-month US Treasury bills, and roughly 2.4% of firms account for all net global wealth creation. This extends P14 outside the US. It shows why a concentrated portfolio usually misses the winners; it does not show they are identifiable beforehand. Twenty should measure every multibagger model against broad exposure that holds those winners automatically, and report how often its selections compound below cash.

[Publisher](https://rpc.cfainstitute.org/research/financial-analysts-journal/2023/long-term-shareholder-returns-evidence-from-64000-global-stocks); [RePEc record](https://ideas.repec.org/a/taf/ufajxx/v79y2023i3p33-63.html).

### P26 — Bali, Cakici & Whitelaw (2011)

**Maxing Out: Stocks as Lotteries and the Cross-Section of Expected Returns**, Journal of Financial Economics. Review: abstract and replication abstracts.

Stocks with the largest maximum daily return in the prior month subsequently underperform; the reported lowest-minus-highest decile difference exceeds 1% per month in 1962–2005 US data. Gorman, Akhtar, Durand & Gould (2022, Critical Finance Review) find the effect general but argue overreaction rather than lottery preference explains it. Either reading means a recent violent rise is a warning, not an entry signal. Include MAX as a required feature and a falsification check: a 10x classifier that mostly selects high-MAX stocks is likely selecting the losing pool.

[Summary of publisher abstract](https://academicnewsletter.sufe.edu.cn/info/356984); [Gorman et al. replication](https://nowpublishers.com/article/Details/CFR-0123).

### P27 — Boyer, Mitton & Vorkink (2010)

**Expected Idiosyncratic Skewness**, Review of Financial Studies. Review: publisher/SSRN abstract.

Lagged skewness is a weak forecast, so the authors model expected skewness from other firm traits. Stocks with high expected skewness have lower subsequent Fama-French alphas (about 1.00% per month between extreme quintiles), which also helps explain the low returns of high idiosyncratic-volatility stocks. A model that successfully predicts upside skew can therefore still select negative-alpha stocks. Twenty should score expected return and probability of a large gain separately and never treat the second as the first.

[SSRN](https://papers.ssrn.com/abstract=1541002); [RePEc record](https://ideas.repec.org/a/oup/rfinst/v23y2010i1p169-202.html).

### P28 — Barber, Huang, Odean & Schwarz (2022)

**Attention-Induced Trading and Returns: Evidence from Robinhood Users**, Journal of Finance. Review: publisher abstract.

Concentrated retail buying of high-attention stocks predicts negative abnormal returns: −4.7% over 20 days for the top stocks bought each day, and −19.6% in extreme herding events. Social-media, forum and trending-list data are therefore more plausibly contrary or risk indicators than buy signals. Any scraped attention feature must be collected with first-seen timestamps and tested with both signs; a positive-sign result would need unusually strong evidence.

[RePEc record](https://ideas.repec.org/a/bla/jfinan/v77y2022i6p3141-3190.html); [working paper](https://www.smallake.kr/wp-content/uploads/2020/12/SSRN-id3715077.pdf).

### P29 — Loughran & Ritter (1995)

**The New Issues Puzzle**, Journal of Finance. Review: publisher abstract; Ritter (1991) by citation only.

US firms issuing equity in 1970–1990 earned about 5% a year (IPOs) and 7% a year (seasoned offerings) over the following five years, well below matched non-issuers. Later work links much of the gap to idiosyncratic risk and lottery preference. Young, recently listed growth stories are where many apparent future multibaggers sit, so listing age and recent issuance must be features and evaluation strata. A universe that drops failed IPOs will overstate this segment badly.

[RePEc record](https://ideas.repec.org/a/bla/jfinan/v50y1995i1p23-51.html).

### P30 — Mohanram (2005)

**Separating Winners from Losers among Low Book-to-Market Stocks using Financial Statement Analysis**, Review of Accounting Studies. Review: publisher abstract.

GSCORE combines earnings and cash-flow quality with earnings/growth stability and R&D, capital-expenditure and advertising intensity to separate outcomes among growth (low book-to-market) stocks. Most of the long-short return comes from the short side: it identifies growth stocks to avoid better than stocks to buy. That makes it useful for a long-only account mainly as an exclusion screen. Implement it only with timestamped statements, and test whether it reduces wipeouts in a multibagger candidate pool.

[RePEc record](https://ideas.repec.org/a/spr/reaccs/v10y2005i2d10.1007_s11142-005-1526-4.html).

### P31 — Yartseva (2025 working paper)

**The Alchemy of Multibagger Stocks**, CAFE Working Paper 33, Birmingham City University, listed as submitted. Review: repository listing, seminar abstract and a secondary practitioner summary only; the paper itself was not inspected.

The study reportedly examines 464 US-listed stocks that rose at least tenfold from 2009 to 2024 and extends Fama-French five-factor variables with free-cash-flow yield, margins, interest-rate changes and momentum. Selecting firms by their eventual outcome cannot by itself give the probability that a firm with the same starting traits became a multibagger, or how many such firms failed. Treat its variables as candidate features only. Twenty's version must start from all eligible stocks at each formation date, including later delistings, and report precision against the base rate.

[Repository listing](https://www.open-access.bcu.ac.uk/view/creators/Yartseva=3AAnna=3A=3A.html); [secondary summary](https://www.quant-investing.com/blog/find-next-10-bagger-using-data-driven-screening/).

## Biotechnology and clinical-trial catalysts

### P32 — Wong, Siah & Lo (2019)

**Estimation of Clinical Trial Success Rates and Related Parameters**, Biostatistics. Review: publisher abstract.

Using 406,038 trial-data entries for more than 21,000 compounds from 2000 to 2015, the authors estimate phase-transition and approval probabilities by disease, phase, sponsor and biomarker use. Oncology's estimated probability of approval from phase 1 was 3.4%. These base rates are the prior any biotech model must beat; a pipeline narrative is not a probability. The estimates are historical and partly derived from commercial datasets, so recompute them from the data Twenty can actually obtain.

[PubMed](https://pubmed.ncbi.nlm.nih.gov/29394327/); [MIT repository](https://dspace.mit.edu/handle/1721.1/128480).

### P33 — Lo, Siah & Wong (2019)

**Machine Learning with Statistical Imputation for Predicting Drug Approvals**, Harvard Data Science Review. Review: author abstract.

Models trained on 2003–2015 drug-development and trial data reported AUCs of 0.74, 0.78 and 0.81 for phase 2→3, phase 2→approval and phase 3→approval. The strongest predictors were trial outcomes and status, accrual, duration, prior approval for another indication and sponsor track record. Statistical imputation outperformed complete-case analysis. A good approval classifier is not a trading signal: the market price already embeds an approval expectation. Twenty should compare model probabilities with market-implied expectations, not with zero.

[SSRN](https://papers.ssrn.com/abstract=2973611); [MIT repository](https://dspace.mit.edu/handle/1721.1/112049).

### P34 — Fu, Huang, Xiao, Glass & Sun (2022); Chen et al. (TrialBench)

**HINT: Hierarchical Interaction Network for Clinical Trial Outcome Prediction**, Patterns; **TrialBench: Multi-Modal AI-Ready Clinical Trial Datasets** (preprint, reported accepted by Scientific Data). Review: abstracts.

HINT combines drug-molecule, disease and eligibility-criteria embeddings and reports test F1 of 0.665, 0.620 and 0.847 for phases 1, 2 and 3. TrialBench packages 23 ClinicalTrials.gov-derived datasets across eight tasks, using DrugBank features and commercial TrialTrove approval labels. They provide ready baselines and code, but their labels depend on licensed sources, and their splits are not built around when information became public. Use them for model development only after re-deriving labels and dates that Twenty can reproduce.

[HINT preprint](https://arxiv.org/abs/2102.04252); [TrialBench preprint](https://arxiv.org/abs/2407.00631).

### P35 — Singh, Rocafort, Cai, Siah & Lo (2022); Hwang (2013)

**The Reaction of Sponsor Stock Prices to Clinical Trial Outcomes: An Event Study Analysis**, PLOS ONE; **Stock Market Returns and Clinical Trial Results of Investigational Compounds**, PLOS ONE. Review: abstracts.

Singh et al. examine 13,807 trials from 2000 to 2020. Sponsor type (early biotechnology versus big pharmaceutical) has the largest association with announcement abnormal returns, followed by disease, outcome, phase and accrual, but these properties explain little of the variation. Hwang's small large-company sample (24 compounds) finds median announcement-day abnormal returns of +0.8% for positive and −2.0% for negative results, with negative effects persisting. Trial results are genuine, dateable catalysts with asymmetric downside. Holding a single small biotech through a readout is a concentrated binary bet; position limits and outcome-date exposure must be explicit.

[Singh et al.](https://doi.org/10.1371/journal.pone.0272851); [Hwang](https://doi.org/10.1371/journal.pone.0071966).

## Timing, stop-losses and trend rules

### P36 — Goyal, Welch & Zafirov (2024); Welch & Goyal (2008)

**A Comprehensive 2022 Look at the Empirical Performance of Equity Premium Prediction**, Review of Financial Studies. Review: abstracts.

Re-testing 17 original and 29 later published market-return predictors with data to 2021, the authors find over a third lack in-sample significance and half of the remainder perform poorly out of sample; a few survive. The 2008 paper concluded the equity premium was not predictable for practical purposes. Predicting when to leave and re-enter the market is the weakest-supported part of the proposed system. Any timing rule must beat continuous investment out of sample after costs, and the record must include every rule tried.

[SFI publication record](https://www.sfi.ch/en/publications/a-comprehensive-2022-look-at-the-empirical-performance-of-equity-premium-prediction); [author page](https://ivo-welch.info/home/card-eqpredict.html).

### P37 — Kaminski & Lo (2014)

**When Do Stop-Loss Rules Stop Losses?**, Journal of Financial Markets. Review: author abstract and working-paper summary.

Under a random walk, a simple stop-loss always reduces expected return. Stop-losses add value only when returns are persistent (momentum), which the authors find for certain rules in long-horizon US equity data. The useful object is therefore a measured persistence condition, not a price threshold chosen from a chart. Twenty should test stop rules as pre-registered hypotheses against buy-and-hold, report time out of the market and re-entry costs, and remember that a stop order does not guarantee its trigger price.

[MIT DSpace](https://dspace.mit.edu/entities/publication/bb69ca4b-0cdc-487f-831d-63b2e84fafee).

### P38 — Han, Zhou & Zhu (2016 working paper)

**Taming Momentum Crashes: A Simple Stop-Loss Strategy**. Review: SSRN abstract and secondary summaries; reported figures differ between versions.

A 10% stop applied to each position of a monthly long-short momentum portfolio reportedly cuts the worst equal-weighted monthly loss from about −50% to about −11%. The results are gross of the extra trading, apply to a long-short portfolio, and rely on daily monitoring. They support testing per-position stops inside a momentum-style strategy (see P12), not a general claim that stops improve any portfolio.

[Practitioner review](https://alphaarchitect.com/2016/08/taming-the-momentum-roller-coaster-fact-or-fiction/).

### P39 — Moskowitz, Ooi & Pedersen (2012); Hurst, Ooi & Pedersen (2017)

**Time Series Momentum**, Journal of Financial Economics; **A Century of Evidence on Trend-Following Investing**, Journal of Portfolio Management. Review: abstracts and the AQR manuscript summary.

An asset's own past 1–12-month return predicts its near-term return across 58 liquid futures, with partial reversal later; the extended study reports positive trend-following returns in every decade across 67 markets since the 1880s. This is the strongest evidence for rule-based reduction of exposure, but it comes from diversified, leveraged futures portfolios, not single small-cap stocks in an ISA. A long-only, unleveraged trend filter on a broad index is a fair test; a trend rule on one volatile stock is a different, weaker hypothesis.

[AQR manuscript](https://fairmodel.econ.yale.edu/ec439/hurst.pdf); [summary](https://alphaarchitect.com/time-series-momentum-aka-trend-following-the-historical-evidence/).

## Synthesis and limits

The review supports investigating persistent signals and useful document processing. It does not establish that increased model complexity, faster search, volatility scaling or concentrated selection will improve this account. Those are experimentally separable propositions. Begin with good data and fair benchmarks, then require a learned model to demonstrate incremental value under the same execution restrictions.

The additions sharpen this. Extreme winners are real and dominate wealth creation, but the obvious traits of lottery-like upside predict low average returns, and outcome-selected multibagger studies cannot give a probability. Clinical-trial outcomes are genuinely predictable from public data, but a usable edge requires beating market-implied expectations. Market timing has weak out-of-sample support; stop and trend rules help only under measured persistence. See [the growth and catalyst research plan](growth-research.md).

No paid datasets were purchased, no source code from papers was executed and no published strategy was independently replicated for this review. Full methods/appendix review remains necessary for any candidate promoted to a faithful replication. Research priorities and concrete gates are specified in [the evaluation protocol](evaluation-protocol.md) and [roadmap](roadmap.md).
