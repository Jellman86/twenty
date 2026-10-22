# Form 25 coverage denominator (`sec-form-index-v1`)

Added 22 October 2026, advancing [historical-data acceptance #5](https://github.com/Jellman86/twenty/issues/5). The declared terminal sample is six stocks, but six out of how many? `Twenty.Ingestion.Sec.SecFormIndex` reads EDGAR's quarterly full index by form type and counts delisting-form filings per year, so a declared sample can be compared with the population it is drawn from. It is a counting tool only.

```sh
dotnet run --project src/Twenty.Data -c Release -- sec-form-index \
  --from 2022 --to 2023 --archive <private-directory> [--max-requests 25-100]
```

Each quarter fetches `https://www.sec.gov/Archives/edgar/full-index/<year>/QTR<n>/form.gz` through the existing bounded [SEC client](sec-collector.md): declared user agent, 500 ms shared pacing, no redirects, response cap, conditional requests and originals archived with receipt hashes. The compressed form is requested because it is 4–5 MB where the plain `form.idx` is 39–50 MB, and the response cap applies to the wire bytes. A quarter that has not started is neither fetched nor flagged.

## What is counted

Rows, exactly as the index writes the form type. `25`, `25/A`, `25-NSE` and `25-NSE/A` are counted separately and never merged or normalized.

- **These are filing counts, not delisting counts.** One exit can carry both an issuer's Form 25 and an exchange's Form 25-NSE, and an amendment does not add an exit. Nothing here infers which rows describe one event.
- A filing date is the date the form was filed, not the date trading stopped or the date proceeds were received.
- The report is a population count over the quarters actually fetched. It does not make any declared sample representative of it.

## Fail-closed reading

The index's header column labels do not align with its data columns, and company names hold spaces, so a row is anchored on its unambiguous tail (CIK, `yyyy-mm-dd`, an `edgar/` path) rather than on column offsets. A file that is not an index (`index-shape`), a row that will not parse (`index-row`), a row dated outside its own quarter (`index-date-range`) and an archive that inflates past the 128 MB bound (`index-size-limit`) are all refusals rather than skipped rows: silently dropping rows would misstate the denominator, which is the whole point of the measurement. `SecFormIndex.Inflate` exposes the bounded decompression so the archive-bomb guard is directly testable.

Validation: 11 authored cases cover the family counted per filing year, form types containing spaces (`SC 13G/A`), company names carrying date- and form-like text, out-of-quarter rows, unparseable rows, missing header/rule/rows, empty and non-archive inputs, the inflation bound, and quarter aggregation with the request budget. All are authored fixtures; 751 .NET tests total. No model trial, holdout or broker request is involved.

## Genuine status

The index format was verified against one genuine quarterly index (2013 Q1): 303,618 rows parsed with zero failures, and the delisting family in that quarter is 19 `25`, 282 `25-NSE` and 6 `25-NSE/A` — which already shows why the forms must not be summed into one delisting count. **The denominator itself is not yet measured**: the owner-designated contact that SEC requires in the user agent is set privately in `TWENTY_SEC_CONTACT` and is not present in the deployment or in this environment, so no collection run has been made. The measurement is 2022–2023 first, which is the window the declared terminal sample's events fall in (ACC 2022-08, ABMD 2022-12, AAWW 2023-03, ABST 2023-07, AAIC 2023-12), then the full history back to 1994.
