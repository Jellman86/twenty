# Raw paper-account implementation review boundary

This checkpoint implements the first authored-only complete-batch scope of the [paper contract](../raw-paper-accounts.md). It does not complete action/claim paper integration, asynchronous execution, genuine forward evaluation or initial-v1 acceptance. Source/Core accounting code is unchanged; the Research identity changes and old unspent studies/accounts retain their historical restrictions.

## Tests and findings addressed

The initial positive contract and persistent-account tests failed against explicit refusing stubs before implementation. Early harness compilation mistakenly called Core-internal JSON helpers; it was corrected to use public serializer options. Another malformed-mark fixture tried serializing a default ImmutableArray before reaching the product; the corrected case changes the closing instant. Byte assertions replaced record equality for deserialized immutable-array account metadata. These are retained harness failures, not claimed product regressions.

The first account implementation froze a candidate but did not fully fix the history/closing instants. It now freezes the entire grid, including the exact 13-date signal window and each later decision/close. Ordinary replay recomputes allocation and fills; the paper contract additionally forbids choosing another permitted monthly cutoff. A prefix, deposit, signal-history, cost, assumed-fill, incomplete-batch, extra-period, study-binding or closing-mark change refuses.

The publication test injects a SQLite failure between a verified receipt and registry commit. New balances are staged in local values until the account/operation transaction commits; an interrupted attempt preserves the earlier accepted head and cannot be replaced. A later actual-SIGKILL rehearsal additionally exercises this boundary in a native process.

A further test removes a genuine-flagged original after import. The first implementation reported missing input because it opened that file before rejecting provenance. Explicit universe imports now record a bounded, hash-checked manifest provenance row. Paper admission rejects genuine or unattested inputs from this metadata before original reads. Legacy files are not scanned/backfilled at startup; explicit exact reimport is required. This index is an assertion from a validated manifest, not source authentication, and genuine paper exposure guards remain unimplemented.

## Accounting and operational evidence

The authored USD/GBP example gives independently calculated 0.246875 shares, £0 cash, £0.25 fees and £19.75 closing equity after one £20 deposit; the second gives 0.49375 shares, £40 contributions, £0.50 fees and £39.50 equity. The account is independent of the eight source development ledgers and spends no final claim. Current records, explicit receipts and UUID retries remain inspectable without originals.

Focused cases cover these values, immutable prefix/history, scope/policy refusals, UUID aliases/conflicts, expected-head compare-and-swap, one account per study/cost, no reset of failed attempts, shared-worker/quota refusal, stop/completion, cold restart, transaction faults and HTTP guards. All source/raw-final/admission checks and full-suite results are recorded separately after execution.

The first native API/browser rehearsal at `raw-paper-api-20261027` uses actual requests and inputs rather than intercepted replies. The repaired native T3 browser creates the stress account, applies a base batch, inspects delivered quantities/marks and stops future batches at desktop/390px. It opens no final or report/receipt automatically and writes only the private authored workspace. Cold retry without originals preserves both accounts and eight source reports/selection; no genuine claim or broker connection occurs. `raw-paper-kill-20261027` kills an owned native child after receipt creation while its success transaction is blocked; restart retains the prior base account and interrupts the uncommitted stress attempt with zero deposits. The orphan is not adopted and a replacement UUID refuses. These first proofs precede the final metadata provenance guard. The fresh `raw-paper-reviewed-20261027` and `raw-paper-reviewed-kill-20261027` proofs repeat the API/cold-retry and actual-SIGKILL checks against final source `4751481`. The browser harness hit its deadline during a context interruption after both accounts and two base batches were accepted; its failure log is retained. A new native host resumed the same immutable workspace without recalculating those batches, completed mobile inspection/stop, and verified cold retries with originals unavailable. No successful fresh-start proof is inferred from the timed-out harness.

## Limits and peer review

The Big Pickle design request `twenty-raw-paper-design-df20ba3-r1-20261027` failed at its free usage limit before inspection. There is no independent verdict for this implementation. No alternative billable model was selected.

The first scope is conservative: no corporate actions or pending claims in paper inputs, one calendar/booking instant per batch, no partial/asynchronous order recovery, no pending-publication recovery, and no genuine data. A failed admitted batch terminates that account while preserving accepted state. The whole cumulative receipt is replayed within finite byte/work/deadline limits. The shared store gate serialises mutations during calculation; it does not guarantee physical disk space against foreign writes. Original hashes do not authenticate fully coherent operator replacement of the datastore. Further paper/action integration and final operational/data acceptance remain open.

## Final validation and publication

Application source `47514813f372d04cc169dfeeb615fa78324a09e1` passed all 1,409 .NET tests locally (14m55s) and in CI, 45 Python tests, Svelte check/build, Ruff, scientific-boundary and full-history/file secret controls. The final focused paper/admission/raw-final run passed 76 cases. There are 23 new paper cases (11 contract and 12 persistent-store cases); the completed source includes the pre-read provenance regressions.

Four fresh `raw-paper-compat-{1..4}-20261027` clones preserve actual older completed raw-final, frozen-v3, raw-v1 and universe request/report/selection/plan bytes and UUID retries, while refusing new final spending under changed science. They are compatibility checks over authored fixtures, not new genuine research evidence.

[CI/image/Dockhand publication](../raw-paper-deployment-2026-10-27.md) preserves eight production collections, 58 exact reports, four diagnostics/history, original study plans and seven model paper accounts. Native T3 live desktop/390px checks are read-only; production has zero raw paper accounts. The source is deployed on `dev`, with no release merge/tag, broker connection or automatic purchase.
