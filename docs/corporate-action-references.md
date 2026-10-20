# Independent corporate-action references

`CorporateActionAudit` and the offline `corporate-action-audit` command compare declared issuer/exchange assertions against the latest captured Tiingo history for one symbol. This supplements the [listing audit](listing-audit.md), whose adjustment-step check compares the vendor with its own action fields. It does not alter prices, mint actions, backdate availability or certify an investment dataset.

```sh
dotnet run --project src/Twenty.Data -c Release -- corporate-action-audit \
  --archive <private-offline-capture-copy> \
  --reference docs/aapl-action-references.json \
  --sources <private-source-document-directory>
```

Original issuer documents are private files named `<contentSha256>.bin`; each must match the hash in the reference bundle. Use a copy of the capture archive, not the running application's writer store. The audit binds the history payload hash, capture id/time, reference bytes hash and reviewed source-document hashes. Capture source id, byte length and payload checksum must agree. Conflicting latest captures fail; identical retransmissions select the ordinal first id. The original URL is recorded, but its authenticity and accurate transcription still require source review.

The bounded `action-reference.v1` schema declares symbol, inclusive history window, sources and non-overlapping assertions for each action kind. Each assertion declares kind, positive value, source id and one of:

- `exact-ex-date`: one stated trading ex-date, compared exactly along with the split ratio or dividend amount.
- `declaration-record-window`: a dividend amount compared against exactly one vendor dividend within the issuer's declaration-to-record-date bounds (at most 45 days). This does **not** independently verify the vendor ex-date. Record, payment and ex-dates are distinct.

Within the declared history window, missing, extra, ambiguous or differently valued events make the report `valid: false` and the CLI exit 1. Unreadable inputs, invalid/overlapping assertions, unsupported date semantics, missing history, insufficient endpoint coverage, corrupt sources and capture conflicts also fail. A successful report means the declared event comparison passed; it is not general data certification. Endpoints bound the comparison but do not establish a gap-free session history. The listing calendar audit remains separate.

## Declared AAPL sample, 20 October 2026

The sample is the same already captured 2015-01-02 to 2026-10-16 history used by the genuine price-conversion/calendar smokes: all 47 vendor dividends and its one split, without symbol or event substitution. [Reference assertions](aapl-action-references.json) contain original normalized facts from reviewed primary sources; [audit metadata](aapl-action-audit.json) records exact hashes and outcomes. Raw pages, price payloads and full generated reports remain private.

[Apple's dividend-history table](https://investor.apple.com/dividend-history/default.aspx) explicitly reports amounts without split adjustment. Its declaration and record dates provide 47 disjoint windows. Every captured dividend amount matches exactly within its corresponding window, including August 2020's 0.82 and August 2026's 0.27, the two residuals in the earlier SEC-duration comparison. This corroborates those amounts, not their exact vendor ex-dates or payment timing.

[Apple's 30 July 2020 announcement](https://www.apple.com/newsroom/2020/07/apple-reports-third-quarter-results/) states a four-for-one exchange and first split-adjusted trading on 31 August 2020. The capture records ratio 4 on that date. The issuer's 28 August split-effect date, 24 August record date and 31 August first adjusted trading date must not be substituted for one another. Changing the reference ex-date to 28 August is a genuine-data negative control: the audit exits 1 with a missing referenced action and an unreferenced vendor action.

All 48 events match; no event is missing, extra, ambiguous or differently valued. Repeating the same audit is byte-identical. Unit tests use authored financial inputs and cover amount tampering, action removal, date-window movement, duplicate candidates, overlap refusal, extra actions, document corruption, capture identity/hash/size mismatches, missing/conflicting captures and unsupported semantics. This is single-symbol evidence. It does not validate vendor price adjustment formulas, other issuers, terminal shareholder proceeds, historical availability or achievable returns; no model trial, final holdout or broker request was involved.
