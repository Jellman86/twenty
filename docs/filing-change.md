# Filing-change baseline (deterministic text similarity)

Implemented in `Twenty.Core.Research.FilingChange` (`filing-change-v1`), with an offline CLI. This is the deterministic baseline for the first distinctive experiment in the [research roadmap](roadmap.md): Cohen, Malloy & Nguyen's *Lazy Prices* (P07 in the [literature review](literature-review.md)) finds that changes in corporate report text carry information about later operations and returns, using document similarity measures. Their published portfolio is long-short; any long-only ISA use here is a new experiment, not their result. This slice implements only the similarity *feature* — no classifier, no return test, no feature enters any model yet.

## Method (fixed, versioned)

`FilingChange.Compare(prior, current)` normalizes both documents identically:

1. Strip markup with a deterministic scanner: tags, comments, and the *contents* of `script`/`style` elements are removed.
2. Decode a small explicit entity set (`amp/lt/gt/quot/apos/nbsp` plus numeric character references); unknown entities are left as text.
3. Invariant lowercase; tokens are runs of `[a-z0-9]` containing at least one letter. Pure numbers are excluded so financial-statement churn (amounts, dates) does not dominate the prose signal.

Scores: cosine similarity over raw token counts, Jaccard similarity over vocabularies, length ratio, and token/vocabulary counts. Empty-after-normalization and oversized (>16M characters) documents fail closed. The published literature compares *sections* (e.g. MD&A, risk factors) year over year; whole-document comparison is the deliberately crude baseline the section-level version below refines.

```sh
dotnet run --project src/Twenty.Data -c Release -- filing-change <prior-document> <current-document> [--sections]
```

The CLI prints the score plus both documents' SHA-256, so any quoted number binds exact input bytes.

## Section-level extraction (`FilingSections`, v2–v3 later the same day)

`FilingSections.Split` extracts Part/Item sections from periodic reports after the shared normalization (block tags become newlines, so heading structure survives). A part heading is a short line `Part <roman|digit>` followed by end-of-line or a dash/dot separator — comma cross-references ("Part II, Item 7 of this Form 10-K…") and colon exhibit sub-headings ("Part II: Demographic Background" inside Baidu's Item 6) are not headings. An item heading is `Item <n>[.letter|letter]` terminated by punctuation (em-dash titles glued without a space, "Item 1—Business", included; decimals like "Item 1.5" excluded) or by whitespace before a capitalized title ("ITEM 2 PROPERTIES"); lowercase continuations ("Item 7 of this…") are cross-references. Sections are keyed by (part, item) because 10-Q item numbers repeat across parts; Form 20-F dotted sub-items ("Item 3.D.") canonicalize to `3d`. Consecutive same-key segments under bare headings merge — Microsoft repeats "PART I / Item 1" running page headers throughout each section, which otherwise fragments it into page-sized pieces (v3; a titled heading always starts a new segment, so contents entries never merge into the body). Tables of contents repeat every heading, so among duplicate segments the winner is the one whose heading carries its title inline, then the most scored tokens (exact ties to the later segment) — the body, not the contents page. Short non-heading lines repeated at least eight times document-wide (running heads, "Table of Contents" furniture) are boilerplate and excluded from section text. Segments with no scored tokens are not sections. `FilingSections.Compare` joins two filings section-by-section and classifies each as `Compared`, `OnlyPrior` or `OnlyCurrent`; nothing is silently dropped.

The v1 selection rule was corrected against genuine data: choosing merely the longest segment let a bare contents stub ("Item 1B.", 3 tokens of neighbouring contents lines) beat Apple's genuine one-token body ("None."). Titled-heading preference fixes the inventory exactly: the September 2025 10-K yields all 23 body sections in document order, from Item 1 Business (2,366 tokens) and Item 1A Risk Factors (10,034) down to one-token Items 1B/9. v2 then generalized the heading rules and added boilerplate exclusion after genuine multi-style checks (next section); the `filing-change-v1` scorer and its tokenization are unchanged, so whole-document scores are stable across the bump.

Per-section genuine results (same archived Apple filings; whole-document cosines were 0.972 and 0.986):

| Section | Dec 2025 → Mar 2026 | Mar 2026 → Jun 2026 |
|---|---|---|
| Part I Item 1 Financial Statements | 0.939 (2,687→2,861) | 0.930 (2,861→2,884) |
| Part I Item 2 MD&A | 0.978 (2,505→3,081) | 0.977 (3,081→3,020) |
| Part II Item 1 Legal Proceedings | 0.999 (790→777) | 0.996 (777→837) |
| Part II Item 1A Risk Factors | 0.654 (62→3,242) | 0.979 (3,242→2,854) |

The December risk-factor section is the short fiscal-Q1 form ("no material changes" style, 62 tokens) against March's full text — a routine reporting-pattern difference, not necessarily substantive change, and exactly the confound a future experiment must model. Tiny sections (e.g. Mine Safety, 2–7 tokens) produce unstable scores and would need minimum-size treatment before any research use. The Mar→Jun MD&A score was recomputed with an independent Python implementation: identical token counts (3,081/3,020) and cosine 0.976546 in both at v1; under v2 boilerplate exclusion the same pair is 3,031/2,970 tokens at 0.985789, again agreed exactly by the independent implementation.

## Labelled extraction checks across issuer styles (19 October 2026)

Expected section inventories were labelled from each genuine document's own headings before running the extractor; documents were collected with the bounded [SEC collector](sec-collector.md) (private archives, hashes in the run output) after the genuine-shape fixes recorded there. Authored regression fixtures in `FilingSectionsTests` pin each style's rules for CI; the genuine documents themselves are not committed.

| Style (genuine sample) | Labelled expectation | Result |
|---|---|---|
| Apple modern iXBRL (10-K Sep 2025; 10-Qs 2025–26) | 23 sections (10-K), 11 (10-Q), document order | Exact. MD&A v2 corroborated independently (0.985789) |
| Costco modern iXBRL (10-Ks 2025/2026, em-dash titles) | 23 headed items; Item 6 body genuinely empty | 22 sections; Item 6 [Reserved] has no scored tokens (page number only) and is correctly absent. Year-on-year MD&A 0.996294, corroborated independently |
| Apple legacy SGML/ASCII (10-K Dec 2000; 10-Qs 2000–01) | 15 sections (10-K: Parts I–IV, Items 1–14, pre-1A era); cross-reference lines must not split | Exact, parts correct despite wrapped "Part II, Item 7 of…" lines. Cross-format 10-Q pair (.txt→.htm) whole-cosine 0.996; Part II Item 4 genuinely OnlyCurrent (annual-meeting quarter); Part II Items 2/3/5 genuinely absent in both |
| Microsoft running page headers (10-Ks 2025/2026) | 23 headed items; per-page "PART I / Item 1" repeats merged, not fragmented | 22 sections (Item 6 reserved/token-empty, as at Costco); section tokens cover 41,246 of 44,904 whole-document tokens. MD&A year-on-year 0.991954, corroborated independently with identical token counts |
| Baidu conventional 20-F (2024/2025 annuals) | Part I Items 1–12, Part II 13–16K, Part III 17–19; exhibit "Part II:" sub-headings inside Item 6 must not re-key Items 7–12 | 30 sections, parts exact after the v3 colon fix. Item 16 body genuinely empty (blank lines only). Item 3 corroborated spot-check tokens 58,699 in both implementations |
| ASML designed-report 20-F (2025) | Heading-text extraction expected to fail: body uses typeset headings without "Item n" typography | Negative result as labelled: only cross-reference-sheet fragments extracted (6 spurious sections, one swallowing 137k tail tokens). Do not quote ASML section scores; whole-document scores remain valid |
| Form 20-F dotted sub-items (authored fixture) | `Item 3.D.` → `3d`, `Item 4.B` → `4b` | Fixture-verified; Baidu's genuine 20-F uses undotted `ITEM 16A.` style, also covered |

The genuine 20-F checks became possible after the SEC collector's per-response cap was raised from a measured-evidence 8 MiB to 48 MiB (Microsoft 10-K 8.19 MiB; ASML 20-Fs 23.71/32.3 MiB); see the [collector doc](sec-collector.md).

Boilerplate evidence was genuine before it was implemented: Costco's document repeats "Table of Contents" 69 times, and the 2000 10-K repeats "NOTES TO CONSOLIDATED FINANCIAL STATEMENTS (CONTINUED)" 24 times; both are excluded from section text at the ≥8-occurrence threshold, while a 7-time repeated line is retained in the regression fixture.

## Genuine smoke (19 October 2026)

Run over the archived Apple filings from the live [SEC collector](sec-collector.md) smoke (private archive; hashes are the archive object names):

| Pair | Cosine | Jaccard | Length ratio | Tokens |
|---|---|---|---|---|
| 10-Q Dec 2025 → Mar 2026 | 0.9724 | 0.7236 | 1.491 | 7,984 → 11,903 |
| 10-Q Mar 2026 → Jun 2026 | 0.9857 | 0.8074 | 0.983 | 11,903 → 11,700 |
| 10-Q Jun 2025 → Jun 2026 | 0.9866 | 0.7259 | 1.399 | 8,366 → 11,700 |
| 10-K Sep 2025 → 10-Q Dec 2025 (cross-form) | 0.9455 | 0.4284 | 0.272 | 29,330 → 7,984 |

Consecutive-quarter similarity is high, cross-form vocabulary overlap much lower — the expected ordering. The Mar→Jun 2026 pair was recomputed with an independent Python implementation (different HTML parser): identical token counts (11,903/11,700), cosine 0.985676 and Jaccard 0.807412 in both. Note the December 10-Q is materially shorter (first fiscal quarter has no year-to-date comparative columns), which moves length ratio without necessarily signalling substantive change — exactly the routine-vs-substantive confound P07 warns about and a future experiment must handle.

## Limits and next steps

- Single-issuer smoke only. Section extraction is heading-heuristic (other filers' heading styles will differ), with no boilerplate/template suppression, no routine-vs-substantive classification, no comparison against a numeric baseline, and no return evaluation — none has been done or should be inferred.
- Filing *availability* discipline is unchanged: any future feature may only use documents known by the decision time (the SEC collector's observed-availability records), never later vintages.
- A real experiment needs many issuers' consecutive same-form filings with point-in-time prices/universe from [#5](https://github.com/Jellman86/twenty/issues/5), a declared comparison plan registered before results, and the change score tested incrementally over numeric baselines under [#8](https://github.com/Jellman86/twenty/issues/8).
