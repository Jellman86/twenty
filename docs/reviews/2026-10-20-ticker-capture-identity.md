# Ticker capture identity correction

20 October 2026. A delayed independent review of the historical `9144ede..d11aef5` range identified dot/hyphen capture aliasing. Its adjustment-tolerance, vintage-retention, cash-settlement and action-documentation findings were already addressed by subsequent commits. The aliasing remained present at `b2dbe7b` and is corrected here. This checkpoint's implementation/downstream review is a self-review, not an independent sign-off of current code.

## Reproduced failures and correction

Two .NET regressions failed before implementation: two requests for `AAA.B`/`AAA-B` produced one bucket, and conversion emitted both tickers' captures as revisions of each listing. An independent Python negative control also failed because the wire auditor accepted hyphenated history for a dotted listing. These were authored responses, with no vendor requests or research trials.

`TiingoHistoryIdentity` now validates request-URL ticker/source association before selection. New punctuation IDs are injective; plain-symbol IDs remain unchanged. Both legacy and canonical buckets remain readable, with genuine other-ticker captures filtered before latest-vintage/conflict/coverage checks. Invalid URL/source association fails closed. The price converter, listing audit, corporate-action audit and terminal audit all use the shared reader. Direct action-audit calls require the same binding. Python independently implements the URL/source check and preserves ticker punctuation in its map.

## Downstream effects

- A ticker cannot inherit another ticker's prices, dividends, splits, latest capture time or apparent historical coverage. A missing target remains missing; the converter records its exclusion. This does not justify selectively omitting failed names from a registered sample.
- Old logs, dataset artifacts, trial reports and study plans stay immutable. Reads do not migrate legacy IDs. Ambiguous old metadata is refused rather than reconstructed. Punctuation is distinct within the existing identity contract; historical ticker reuse still needs interval evidence.
- Escape characters, maximal ticker length and a canonical bucket coinciding with a different ticker's legacy bucket are covered. A source filed under the wrong directory is refused. Plain IDs are read once; old/new captures retain the existing duplicate/conflicting-vintage rules.
- Conversion advances to `listing-prices-v4`; output manifests and hashes therefore change even when financial observations do not. The Python audit is `listing-prices-wire-v3`. No Core/Research policy changes are introduced; conversion provenance and content-addressed datasets distinguish new inputs. Existing frozen studies are not reopened or reinterpreted.
- URL binding validates recorded metadata, not cryptographic source authenticity. Terminal request dates retain their stricter existing contract/error codes. No accounting convention, price adjustment, session calendar, availability timestamp, quota, retry policy, execution permission or deployment setting changes.
- The independent Python audit and .NET ingestion both rejected the regression. Shared vendor inputs remain same-producer comparison, not independent market verification.

## Validation and bounded genuine replay

573 .NET tests and 39 Python tests pass locally. Added cases cover separate writer buckets, legacy price conversion, missing-target refusal, old/new reads, same-ID namespace collisions, missing/wrong-host/user-info/fragment/port/request identity, directory/source mismatch, maximal encoding and direct action-audit substitution. Existing revision, calendar, study, reservation, accounting and restart tests remain green. Financial regression inputs are authored; no accelerator work is involved.

Offline copies of previously captured genuine inputs were used, with zero new vendor requests. AAPL's 5,930 bars, 48 vendor actions and two listing observations remain exactly equal to the v3 output, including capture availability and session closes; the new converter version changes its manifest/hash. The independent Python wire check passes. The primary-reference AAPL action report and six-symbol ABST-follow-up terminal report are byte-identical to their retained originals. The latter remains invalid with the previously documented post-trading/unsupported-evidence findings; the identity correction does not accept those data. [Compact hashes and counts](../ticker-identity-smoke.json) accompany private full evidence in `artifacts/twenty/ticker-identity-20261020` outside Git.

Svelte check/build reports zero warnings/errors, Ruff and the scientific-boundary probe pass. Full-history/files secret scanning and its deliberate synthetic leak control are required before publication. Two initial scanner findings were false positives on identical authored invalid-port URLs; equivalent constant concatenation preserves those URL tests without adding scanner exclusions. CI/image publication and Dockhand state-preserving deployment are recorded after execution. Initial-v1 acceptance gates remain partial/open.

## Publication and deployment

[CI 39506714745](https://github.com/Jellman86/twenty/actions/runs/39506714745) passed all test/security gates, image smoke and publication for `c522dda6da0376348c3b9880df4545f3eebadc17`. The rolling `dev` manifest matched its immutable SHA tag before deployment. Dockhand synced the Git-backed Compose source, verified its file hash and performed one successful API-owned deploy. Quark readiness and the single Twenty container are healthy. The original six collections, 58 successful report responses, study plans and four diagnostic responses are preserved exactly; all 61 prior trials remain disclosed, with zero universe trials and automatic purchases disabled. Full private proof is in `artifacts/twenty/deploy-ticker-identity-20261020`. This deployment did not create a study, research trial, final request or vendor call.
