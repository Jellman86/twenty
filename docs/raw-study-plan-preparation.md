# Raw study plan preparation

`POST /api/research/raw-study-plans` prepares a content-addressed declaration with the server's current scientific identity and accounting/runtime identity. The workbench exposes this through **Prepare a raw study plan**. It fills the gap between an offline declaration and prepared-plan admission; it does not author or certify market inputs.

Preparation reads no dataset, prices, calendar tables or action catalog, and does not consult their existence. The supplied hashes, universe id and provenance remain assertions. It creates no study, trial, reservation, selection, final claim, paper account or broker instruction. Separate registration through `POST /api/research/raw-studies` validates the exact originals and records all eight trials before a separate start request. Final spending remains a later explicit operation.

## Frozen inputs

The request supplies every top-level field and every execution field; omission is an HTTP 400 rather than adoption of a cost default. The form's initial values are labelled editable examples and require review. This example illustrates the wire shape, with placeholder hashes that must be replaced:

```json
{
  "datasetSha256": "<64 lowercase hex characters>",
  "universeId": "reviewed-universe",
  "synthetic": true,
  "purpose": "engineering",
  "firstMonth": "2010-01-01",
  "decisionCount": 73,
  "decisionTimeUtc": "23:00:00",
  "monthlyGbp": 20,
  "execution": {
    "maximumPriceAgeDays": 45,
    "maximumFxAgeDays": 2,
    "cashReserveGbp": 5,
    "feePerFillGbp": 1,
    "halfSpreadBps": 5,
    "slippageBps": 5,
    "minimumOrderValue": 1,
    "maximumParticipation": 0.05,
    "quantityStep": 0
  },
  "calendars": [{"calendarId": "reviewed-id", "venue": "XNAS", "sha256": "<canonical calendar hash>"}],
  "actionCatalogSha256": "<exact action catalog hash>"
}
```

`firstMonth` must be the first day of a calendar month. Decisions are the last calendar day of each consecutive month, at the explicit UTC time. Leap years are handled by the Gregorian calendar; no venue time zone, holiday or executable session is inferred. Custom monthly instants can still be declared through the offline protocol. The existing chronological protocol validates the warm-up, minimum development window, unused interval and 24 final contribution periods; short grids refuse. At most 600 decisions, 16 distinct calendar pins, £1–£1,000 monthly contributions and the existing execution-policy bounds are accepted. Imported inputs require `synthetic: false` and `purpose: "research"`; authored inputs require `true`/`"engineering"`. This is not provenance certification.

The fixed family remains 1, 2, 4 and 8 target names, each under base/stress costs. Stress doubles spread and slippage only. No new strategy, search range, execution convention, FX markup or fee assumption is introduced. The returned plan includes both full policies, stage dates, original-input pins, identity and limitations for review.

## Persistence and retry

The response is `{planSha256, planBase64}`. Base64 decodes to the exact canonical `RawUniverseStudyProtocol.Encode` bytes; their SHA-256 is the returned hash. The same bytes are atomically written to `imports/<sha>.raw-study.json`. No timestamp or random request id enters the plan. Equivalent inputs on the same build/runtime therefore produce the same bytes, including after restart. A changed scientific identity intentionally produces a different declaration. An old unspent study cannot be silently resumed under the new identity.

New preparation is bounded by 100 existing `*.raw-study.json` files in that inbox and the shared 256 MiB artifact quota, including held final-publication reservations. A byte-identical retry needs no new space and remains available at either limit. A conflicting existing file refuses `artifact-integrity` and is not overwritten. There is no pruning API; copying a hash into the registration form does not delete the original. Independently supplied inbox files count toward the bound.

The endpoint uses the existing authenticated research host, same-origin/header policy and 16 KiB body limit. It accepts no path or file upload. Core encoding retains the 256 KiB plan bound. Changing form inputs clears its earlier preview; **Use this plan for registration** copies the frozen hash into a separate form without automatically submitting admission or starting work.

## Validation boundary

Authored tests prepare with all original-input directories unavailable, verify exact canonical bytes/hash and no trial rows, retry after restart, then restore originals and admit the same bytes separately. Other cases cover leap/non-leap month ends, unchanged finite candidates and cost stress, invalid dates/pins/hashes/provenance/costs, count/quota limits, corrupt retained bytes, missing financial fields and cross-site/header refusal. These are engineering tests, not historical source or strategy-performance evidence.

[Validation notes](reviews/2026-10-27-raw-plan-preparation.md) record the local 1,386-case suite, actual native HTTP/cold-restart rehearsal, compatible older artifacts and desktop/390px browser checks. The [exact-source CI/image/Dockhand receipt](raw-plan-deployment-2026-10-27.md) now records deployment and state preservation. Raw paper-account integration, independent original-input preparation, genuine coverage and final combined initial-v1 acceptance remain open.
