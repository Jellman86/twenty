# Filing-change baseline (deterministic text similarity)

Implemented in `Twenty.Core.Research.FilingChange` (`filing-change-v1`), with an offline CLI. This is the deterministic baseline for the first distinctive experiment in the [research roadmap](roadmap.md): Cohen, Malloy & Nguyen's *Lazy Prices* (P07 in the [literature review](literature-review.md)) finds that changes in corporate report text carry information about later operations and returns, using document similarity measures. Their published portfolio is long-short; any long-only ISA use here is a new experiment, not their result. This slice implements only the similarity *feature* — no classifier, no return test, no feature enters any model yet.

## Method (fixed, versioned)

`FilingChange.Compare(prior, current)` normalizes both documents identically:

1. Strip markup with a deterministic scanner: tags, comments, and the *contents* of `script`/`style` elements are removed.
2. Decode a small explicit entity set (`amp/lt/gt/quot/apos/nbsp` plus numeric character references); unknown entities are left as text.
3. Invariant lowercase; tokens are runs of `[a-z0-9]` containing at least one letter. Pure numbers are excluded so financial-statement churn (amounts, dates) does not dominate the prose signal.

Scores: cosine similarity over raw token counts, Jaccard similarity over vocabularies, length ratio, and token/vocabulary counts. Empty-after-normalization and oversized (>16M characters) documents fail closed. The published literature compares *sections* (e.g. MD&A, risk factors) year over year; whole-document comparison is the deliberately crude v1 baseline a section-aware version must beat.

```sh
dotnet run --project src/Twenty.Data -c Release -- filing-change <prior-document> <current-document>
```

The CLI prints the score plus both documents' SHA-256, so any quoted number binds exact input bytes.

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

- Whole-document, single-issuer smoke only. No section extraction, no boilerplate/template suppression, no comparison against a numeric baseline, and no return evaluation has been done or should be inferred.
- Filing *availability* discipline is unchanged: any future feature may only use documents known by the decision time (the SEC collector's observed-availability records), never later vintages.
- A real experiment needs many issuers' consecutive same-form filings with point-in-time prices/universe from [#5](https://github.com/Jellman86/twenty/issues/5), a declared comparison plan registered before results, and the change score tested incrementally over numeric baselines under [#8](https://github.com/Jellman86/twenty/issues/8).
