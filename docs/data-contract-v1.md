# Dataset contract v1 and historical selection

Implemented in `Twenty.Core.Data`; offline CLI in `Twenty.Data`. This is the storage/provenance contract and selection guard for issue #3. It does not collect data, calculate model features, reconstruct investment returns, or validate a provider's legal rights/coverage. All committed example observations are authored **synthetic fixtures**, not historical stock data.

## Use it

```sh
dotnet run --project src/Twenty.Data -- validate fixtures/data/point-in-time.v1.json
dotnet run --project src/Twenty.Data -- validate fixtures/data/point-in-time.v1.json --archive data/archive
```

Exit 0 means the storage/row contract passed; exit 1 means invalid data, JSON, or archive failure; exit 2 means invalid command usage. Read structured `issues` for contract failures. Parse errors expose numeric positions, not raw input/property names. Inputs are bounded to 8 MiB and depth 32. Import is explicit and local; it never calls a broker or downloads anything. Generated archives belong under ignored `data/` or outside Git.

## Manifest and row types

`DatasetFile` is a JSON manifest plus an immutable array of typed observations, discriminated by `kind`. The supported storage version is `twenty.dataset.v1`. Constructor fields must be present, including explicit nulls for nullable fields. Unknown fields, unknown enum names, integer enum substitutes, unzoned timestamps and missing non-null fields are rejected rather than given defaults.

The manifest pins dataset ID, creation cutoff, feature schema version/definitions, source snapshots, exchange-calendar references and universe definitions. Each source has an original-content SHA-256, retrieval and optional first-seen times, parser version, licence reference and revision policy. Source URLs must omit credentials, queries and fragments; record non-sensitive request configuration in a future collector's private metadata. Calendar IDs reference explicit timezone/version/hash metadata. Their actual holiday/session contents still need a verified calendar provider; a timezone is not a full exchange schedule.

Source hashes and licence references are required metadata, **not automatic proof** of authentic originals, permissible usage or completeness. The synthetic fixture's original-source and mini-calendar hashes are independently checked in tests. Real originals will be captured and verified by ingestion in #4/#5.

| Observation kind | Identity/context | Values retained |
|---|---|---|
| `listing` | Stable listing/security/company IDs, venue, stock/ETF class, source-namespaced provider mappings, calendar | Symbol, denomination, status and effective interval |
| `membership` | Declared research/Stocks ISA universe and listing | Eligible flag and effective interval, with source evidence |
| `priceBar` | Listing, exchange-local session date, close instant, adjustment basis | OHLC, volume and explicit price denomination |
| `corporateAction` | Listing, action ID and effective instant | Split ratio (new shares/old shares; for a stock terminal outcome, successor shares per original share), dividend/cash currency, new symbol, successor listing, terminal-outcome status |
| `fundamentalFact` | Company, concept, unit, period and filing accession | As-reported decimal value and retained revision ancestry |
| `filing` | Company, accession, form, reporting period and original hash | Acceptance/publication/availability and amendment accession |
| `fxRate` | Base/quote currencies, observation instant and purpose | Positive quote-units-per-base decimal rate |

A listing ID cannot change security, issuer, venue or stock/ETF class; such a change needs a new verified listing identity. Symbols/provider IDs can change through versioned intervals. A provider ID/ISIN that maps to multiple known listings is ambiguous; selection requires venue/identity evidence. A Stocks ISA universe is an explicit dataset scope, not proof that a particular broker account or every listed asset is ISA-eligible. Eligibility assertions need a reliable historical authority.

V1 supports GBP, USD, EUR, CAD, AUD, CHF, JPY, SEK, NOK, DKK, HKD, SGD, NZD and ZAR. Add more through an explicit reviewed contract change. `GBX` is represented as currency `GBP` with quote unit `pence`; it is never treated as a separate FX currency. Cash distributions and FX amounts use major currency units. Prices require a caller-selected adjustment basis; raw and adjusted series are never silently substituted. A fully adjusted bar means all OHLC fields share that basis; an EOD vendor's adjusted close alongside raw OHLC must not be copied into one fully adjusted bar without a justified transformation.

Feature definitions and their availability rules are versioned metadata inside the hashed dataset. Actual feature calculation remains a separate train-only pipeline. Do not reuse a feature schema version for a different definition; the later experiment registry will bind dataset and feature hashes to each trial.

## Two clocks and revision semantics

Every observation carries publication time, available-at time, availability evidence, timing precision and an optional policy reference. Availability is either archived/observed publication evidence or an explicit conservative policy. Unknown availability fails validation. Date-precision and conservative availability require a policy reference. Observed availability also requires nondefault first-seen evidence and `firstSeenAt <= availableAt`; importing a later observation cannot backdate it into earlier decisions. Conservative policy labels do not claim an actual public dissemination timestamp.

A market decision can use only records with `availableAt <= decisionAt`. Decision time cannot exceed the archived snapshot's creation cutoff. Retrospective retrieval may be later than a historical decision if original publication/availability evidence is preserved; requiring every historical fact to have been downloaded by Twenty before its historical date would make legitimate backfill impossible. Online collection should retain actual first-seen evidence rather than fabricate it for old records.

First select the latest available revision **of each logical observation**. Then evaluate business-effective intervals, using `[from, until)` boundaries. Listing/membership series include their effective start in their key. End-date corrections therefore revise the earlier assertion, while the following effective interval is a separate assertion. Close the earlier open-ended interval explicitly; overlapping known intervals cause a selection error rather than a silent guess.

Each logical observation has a complete linear revision chain: root first, then strictly later availability and `revisionOf` equal to the preceding record ID. Missing ancestors, forks/ties, cross-series references and cycles are rejected. A restated fact has the same company/concept/unit/period identity and retains the new accession; a filing amendment is also a separate accession linked by `amendsAccession`.

`PointInTimeDataset.AsOf` returns a frozen `DataSlice`. Its universe lookup selects contemporaneous eligibility and active listings. Price lookup checks denomination and exchange-local session date against the known effective listing. Missing/contradictory mappings and unknown universes fail closed. A split announced today but effective next week is not applied early. Missing delisting proceeds are represented as `missing` and cannot become zero loss, zero return or a guessed cash distribution.

FX selection requires explicit direction, purpose and maximum age. It never inverts a rate automatically or uses a research reference rate as an executable rate. Decimal conversion preserves precision; ledger rounding/fees belong to #6. Complete exchange-session/holiday checks, future dividend adjustment reconstruction and return calculations are not implemented by these selectors.

## Immutable archive

`DatasetArchive.Store` validates the contract before writing, computes SHA-256 of the exact input bytes and stores `<root>/datasets/twenty.dataset.v1/<hash>.json`. It flushes a temporary file, moves it without replacement and treats a byte-identical repeated import as idempotent. Existing different/corrupt content at the same address is refused and left intact. `DatasetArchive.Load` recomputes the requested content address before returning a validated snapshot; even structurally valid tampering is refused. This preserves exact input bytes, not a canonical semantic JSON hash. Snapshot bytes include the manifest and feature definitions. Source-original object storage and SQLite experiment metadata remain later work. The 8 MiB bound is for initial import batches; whole-market storage will need immutable shards/columnar artifacts rather than raising this limit without profiling.

## Upstream boundary and validation depth

Trading 212 instrument and EOD price DTOs retain provider optionality separately from domain records. A missing vendor close remains null. SEC recent-filing DTOs require complete equal-length parallel arrays and essential identity/timing entries; malformed arrays return structured rejection issues rather than being zipped/truncated. Rejected records cannot enter a validated snapshot. The separate [SEC collector](sec-collector.md) now implements bounded HTTP collection and persistent raw-payload quarantine, tested offline; it is not a complete SEC/XBRL parser. The separate [ECB FX audit/parser](ecb-fx.md) now validates real eurofxref XML samples; conversion into timestamped domain observations still requires a reviewed availability policy.

Structural validation is distinct from point-in-time query validation: unresolved interval overlap, price/listing unit mismatch and unknown terminal proceeds also fail when the affected selection is requested. Neither validation certifies investment performance. No historical vendor data or broker account has been tested by this milestone.

Tests use the fixture's future membership, announced split, later adjusted-price revision, ticker change, later restatement and late delisting information. They assert expected values at explicit cutoffs and change future records to confirm earlier selections stay unchanged. Other tests cover units, stale/inverted FX, exchange-local dates, missing metadata, unsafe source locations, unknown schemas/fields/enums, corrupt revision ancestry, malformed SEC arrays and immutable archive behavior.
