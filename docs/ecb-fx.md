# ECB reference FX: verified wire format and conversion boundary

15 October 2026. This is a bounded public-source sample audit and an offline parser, advancing issue #5. It does not select a stock-price vendor or authorise a data subscription. No market/ISA eligibility coverage or investment performance is certified.

## Source evidence

The ECB Data Portal documents date-bounded EXR queries and CSV output. We retrieved GBP/USD daily reference series for 3–7 June 2024 and 28 March–2 April 2024, plus the official eurofxref historical XML, with normal TLS verification. Requests returned HTTP 200. [API query specification](https://data.ecb.europa.eu/help/api/data), [official examples](https://data.ecb.europa.eu/help/api/data-examples), [reference-rate download page](https://www.ecb.europa.eu/stats/policy_and_exchange_rates/euro_reference_exchange_rates/html/index.en.html).

Raw payloads stay in ignored private research storage. [Audit metadata](ecb-source-audit.json) records exact URLs, retrieval timestamps, content types, byte counts, SHA-256 and validation results. The XML was 8,201,190 bytes and the C# parser accepted 221,383 rate rows across 7,115 dates (4 January 1999–15 October 2026). That establishes acceptance of this retrieved object, not completeness or historical vintages.

All 14 sampled API currency/date observations matched the XML values exactly. The sampled API metadata specified EUR as denominator, zero unit multiplier and observation status `A`; its publication columns were blank. Neither delivery format supplied an actual release timestamp for each sampled observation. Dates 29 March–1 April 2024 were absent from both sampled deliveries. We do not manufacture rates for those dates or claim this sample is a complete calendar.

These are **two formats from the same producer**, not independent verification of the underlying fixing. Current historical downloads are not automatically the originally published vintages. The API documents revision/history options, but their usable vintage/timestamp semantics were not audited here.

## Units and derived cross-rate

ECB publishes currency units per EUR. For aligned daily observations:

```text
GBP per USD = (GBP per EUR) / (USD per EUR)
```

On 3 June 2024, the sampled ECB values were GBP 0.85175 and USD 1.0842 per EUR. Twenty's derived rate is approximately **GBP 0.7856022874 per USD**. This ratio is a calculation from ECB data, explicitly a modification; it is not a broker conversion quote. EUR is the implicit unit base. GBX is a share-price unit and cannot be requested as an FX currency.

ECB describes rates as information-only, normally released around 16:00 CET on eligible working days, with a preceding reference-rate procedure. A typical schedule does not establish exact historical availability. These rates must not replace broker fills, spreads, FX fees or conversion timestamps. [ECB policy and schedule](https://www.ecb.europa.eu/stats/policy_and_exchange_rates/euro_reference_exchange_rates/html/index.en.html).

The ECB copyright notice permits free use subject to accurate attribution and disclosure of modifications, with additional conditions for sold documents and some authored publications. Twenty attributes the source and labels its derived ratios. This review is limited to the cited reference-rate material; it is not a blanket licence for other vendors or ECB-authored papers. [ECB copyright conditions](https://www.ecb.europa.eu/services/using-our-site/disclaimer/html/index.en.html).

## Implemented parser

`Twenty.Ingestion.Fx.EcbReferenceRates` parses the eurofxref XML envelope, namespaces, day cubes and currency/rate attributes into immutable typed daily observations. It rejects DTDs/external entities, oversized/empty input, malformed dates, duplicate dates/currencies, unexpected cube attributes, nonpositive/nondecimal rates and ambiguous EUR/GBX rows. Inputs are capped at 8 MiB and 300,000 rows; a growing historical file must fail rather than silently truncate. A future collector should use bounded windows/shards or the 90-day feed rather than raise limits without review.

Cross-rate lookup requires an exact date and explicit supported base/quote currency. It checks both legs exist on the same date, refuses missing-date fill or cross-date mixing, and returns `FxPurpose.Reference`. Decimal overflow/underflow fails. The supported application pair currencies match domain v1; other historical currency rows can be preserved but need a contract review before application conversion.

Offline use:

```sh
dotnet run --project src/Twenty.Data -- ecb-inspect fixtures/fx/ecb.synthetic.xml --date 2024-04-02 --base USD --quote GBP
```

That authored fixture returns 0.64 GBP per USD and is explicitly synthetic. The same command was separately run against the privately downloaded real XML and matched the 3 June audit ratio. No real vendor payload is committed. CI runs only the synthetic fixture and regression tests.

The parsed reference date remains a `DateOnly`: no observation, publication or availability instant is invented. There is no automatic `FxRate` dataset import. A later import must bind original hashes, first-seen evidence or a reviewed conservative policy, declared time precision and a decision cutoff. Broker execution FX remains a separate source/purpose.

## Independent-producer comparison (Bank of England, 19 October 2026)

The same-producer caveat above is now partly retired for the FX legs. `scripts/audit_fx_ecb_boe.py` compares caller-supplied ECB eurofxref history against the Bank of England's daily spot rates into sterling (IADB `XUDLUSS`, USD per GBP; `XUDLERS`, EUR per GBP) — a genuinely independent official producer. Neither payload is committed; retrieval hashes and full results are in [fx-independent-audit.json](fx-independent-audit.json).

For the pre-declared calendar-2024 window: 253 matched dates. USD-per-GBP relative differences (BoE vs ECB-derived EURUSD/EURGBP) have median −0.33 bp and mean −1.03 bp; EUR-per-GBP median +0.24 bp. Medians at zero across two independent publications confirm quote direction, scale and units on both sides — an inverted or mis-scaled series would differ by thousands of basis points (pinned by unit test). Calendar asymmetry also matches independently: ECB-only dates are UK bank holidays (6/27 May, 26 Aug) and the single BoE-only date is 1 May, when the ECB does not publish.

Dispersion is fixing-time noise, not error: p95 absolute difference is 45.5 bp (USD) / 27.3 bp (EUR), and the nine USD dates over 50 bp (max 105 bp, 10 Apr and 31 Oct 2024) coincide with major US data releases and the UK Budget aftermath. ECB rates are determined around 14:10 CET (≈13:10 London, before 13:30 London US releases) and published around 16:00 CET ([ECB framework](https://www.ecb.europa.eu/stats/pdf/exchange/Framework_for_the_euro_foreign_exchange_reference_rates.en.pdf)); neither payload carries intraday timestamps, so that explanation is interpretation, not proof. The practical consequence stands: a daily reference rate can differ from an achievable afternoon conversion by ~1% on event days. Reference FX remains unsuitable as an execution price, and this comparison certifies neither producer's vintage history.

## Remaining acceptance work

Issue #5 stays open: select an affordable licensed stock-price source; audit splits, distributions, inactive securities and terminal returns; establish historical universe/ISA coverage; verify exchange sessions/calendars; and define revision/publication policies. A current ECB history file alone cannot make a historical stock-selection backtest trustworthy.
