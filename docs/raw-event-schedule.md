# Evidence-directed offline action scheduling

`RawEventSchedule` compiles a declared catalog of splits, dividend entitlements/payments and pure terminal claims/settlements into the [verified raw replay archive](raw-replay-archive.md). Booking follows source availability and explicit prerequisite order. The original plan remains bound inside the replay request and is recompiled during execution/recovery. This is an offline compiler for declared portfolio exposures, not a source-wide action discovery service, registered strategy engine or broker scheduler.

## Explicit plan and source selection

A `RawSchedulePlan` declares scheduler/engine versions, the actual Core/runtime identity, price/FX age limits, FX purpose, a timeline and an action catalog. Timeline entries contain only deposits, assumed/modelled fills and complete marks. Action entries pin listing ID, action ID, observation ID and the exact capture dataset hash. Each generated event has an operator-supplied identifier, dataset hash and non-negative tie order. Dividend and terminal entries explicitly provide cutoff evidence and either a payment/settlement instruction or null. There is no inferred zero tax, fee, payout or payment date.

Input files must retain complete validated dataset records, including original/revision chains. Serializing only an already selected `DataSlice` can discard a revision's original observation and is not a dataset import procedure. The compiler verifies bytes, resolves the pinned observation and requires it to remain the selected vintage at the derived capture time. Even economically identical newer vintages require explicit repinning before capture; already booked ledger bindings retain their separate identical-vintage rule. The compiler never substitutes another action or treats a final trading session as legal cutoff evidence.

| Event | Derived booking instant |
| --- | --- |
| Split application | Latest of effective time and action availability |
| Dividend/terminal entitlement capture | Latest of effective time, action availability and explicit cutoff-proof availability |
| Dividend payment | Latest of capture booking, actual payment and payment-proof availability |
| Terminal cash/stock settlement | Latest of capture booking, actual settlement and settlement-proof availability |

Actual entitlement/payment/settlement instants remain in their evidence/receipts. Earlier scheduled payment availability than actual payment is refused. Missing times and mismatched cutoff assertions are refused; confirmation of zero has no separate settlement. A late legal-cutoff proof may delay both capture and settlement, but cannot create earlier cash or rewrite a past decision.

## Deterministic order and accounting refusal

Timeline and catalog array order do not decide economic priority. The compiler sorts by UTC instant and explicit tie order, assigns contiguous replay sequences and refuses duplicate priorities at the same instant. Capture must precede its payment when booked simultaneously. A final complete mark must be last; an action disclosed beyond it is refused rather than silently removed from the plan. IDs are unique across generated and timeline events.

Tie order is not proof of ownership. A purchase at the entitlement cutoff does not satisfy the ledger's strict pre-cutoff ownership requirement. Simultaneous conflicting actions, late quantity changes, missing pending-income assumptions, unsupported consideration and intervening successor actions retain all existing refusals. A mark between a known terminal event and a delayed capture cannot pretend the old shares remain an unaffected holding. Modelled execution still cannot cross a previously recorded event/mark after its actual opening time.

Compilation establishes source/time/order binding; executing the result through `RawReplayArchive` establishes accounting acceptance. A declared action with no owned position is refused, not silently skipped. Unlisted effective actions affecting a held position still fail the ledger guard. The [registered raw worker](raw-study-development.md#pure-split-integration-26-october-2026) now supplies dynamic pure-split application and verified no-holding dispositions separately; this standalone compiler still refuses declared actions without an owned position. Source-wide discovery and dividend/terminal study scheduling remain separate work. Hashes prove local byte integrity, not legal entitlement, vendor authenticity or source completeness. Genuine settlement imports remain an evidence gate.

## Durable binding and bounded work

A compiled request contains `RawReplayScheduleBinding`: scheduler version, exact original plan SHA-256 and base64 plan bytes. Execution/recovery recompiles that plan using its hash-verified datasets and requires the canonical derived document to match. Changing derived quantities, times, configuration or embedded plan terms without the corresponding legitimate compilation is refused. Existing manually authored replay documents remain supported with no schedule binding and make no scheduling-provenance claim.

Plan JSON is capped at 512 KiB, with at most 1,000 catalog actions and 10,000 generated/timeline events. The compiler shares the archive's 16-dataset, 8 MiB-per-dataset and 32 MiB aggregate dataset limits. Repeated source-action lookup work is capped at 500,000 observation visits. The compiled request must fit the existing 2 MiB limit; execution retains all archive slice/work/result/receipt limits. Strict JSON rejects unknown kinds/members, duplicates, absent required proof/payment declarations and offset-free instants. Generated receipts remain private and excluded from Git by both ignore rules and the tracked-path guard.

## Offline command

Use `raw-replay identity` and author a plan with that exact identity and `RawReplayJson.Options`. Dataset hashes address the ordinary private dataset archive. Then:

```sh
dotnet run --project src/Twenty.Data -c Release -- raw-replay schedule \
  --plan /private/plan.json --datasets /private/dataset-root --archive /private/replay-root

dotnet run --project src/Twenty.Data -c Release -- raw-replay verify \
  --request-sha <compiled-request-sha256> --datasets /private/dataset-root --archive /private/replay-root
```

Schedule compiles, executes, stores and reopens the receipt before success. Verify re-executes in fresh state and rechecks the embedded plan. Output remains explicitly offline and unregistered. No HTTP workflow, trial registration, broker call, model training or genuine final evaluation is introduced.

## Payment FX timing fix and authored validation

Review reproduced a pre-existing dividend-settlement gap: a rate observed before payment but first available after it could be accepted at later booking. `PayDividend` now requires the pinned executable rate to be both observed and available by actual payment, matching terminal settlement. The regression first failed on the old behaviour and now checks atomic refusal. This conservative executable-quote contract is not an importer for every broker statement format; a later statement of an actual conversion needs separately accepted evidence/mapping.

Fifty-two added cases bring the full .NET suite to 1,061 tests. Coverage includes every supported event family, pending claims, late capture/payment evidence, explicit ties, cutoff ownership, pinned revisions, future-FX perturbation, omitted/unowned actions, strict wire format, work/size limits, original-plan snapshotting and derived-instruction tampering. Bypassing schedule recompilation makes all six binding regressions fail; the restored implementation passes.

Fresh-process CLI create/reopen/retry checks retain exact plan/receipt bytes and timestamps. Python Decimal independently confirms the authored examples: from 300 GBP, buy two 100 USD shares at 0.8, leaving 140 GBP. A four-USD dividend less one USD withholding, converted at 0.75 and charged 0.2 GBP, adds 2.05 GBP. Cash terminal consideration of 220 USD less 20 USD withholding at 0.75 and a 0.5 GBP fee gives final cash/equity 291.55 GBP. Stock consideration delivers three successor shares marked at 80 USD and 0.75, giving cash 141.55 GBP and equity 321.55 GBP. Confirmed zero leaves 142.05 GBP after the dividend. Changed derived quantities are refused before publication. Private proof is under `/mnt/apps/developer/jellman86/artifacts/twenty/raw-event-schedule-20261023`.

Python tests, Ruff, web and research/security validation are separate gates. All financial inputs above are authored; no historical return, executable strategy, source-wide coverage or initial-v1 completion is established.

Local validation passes 39 Python tests, Ruff, Svelte check/build, sensitive-path checks, research-boundary probes and full-history/files secret scanning. No GPU/NPU check, optional neural training, broker action, vendor collection or genuine final evaluation is used. Existing report schemas are unchanged; scientific identity changes and reserved-study compatibility require separate verification before deployment.

## Publication and deployment acceptance (23 October 2026)

Reviewed source `59214cab2d66f898d973c19b5d33eec1e6eb5a7f` passed [CI 39999384006](https://github.com/Jellman86/twenty/actions/runs/39999384006), including all 1,061 .NET tests, Python/web checks, research/security boundary checks, full-history/files secret scanning, image smoke and registry publication. Failing controls reproduce the previously accepted late-published dividend FX and reject bypassed schedule verification; the restored implementation passes. Three fresh-process CLI scenarios match independent Python Decimal calculations and preserve original plan/receipt bytes and retry timestamps.

A fresh private compatibility clone under the changed scientific identity preserved the completed authored study, exact plan and ten request/report pairs. The original final request replayed its same result; a replacement remained refused, with ten trials unchanged. Proof is under `/mnt/apps/developer/jellman86/artifacts/twenty/universe-study-cross-version-schedule-20261023`. No production trial or genuine final evaluation was opened.

Fresh pre-deployment checks found no active production runs/studies and unchanged registers. The commit-specific registry digest matched `dev`; Git-backed Dockhand sync/deploy installed that tested image. Live metadata reports the exact source and one running healthy container. Eight stored collections, 58 byte-identical report responses, four diagnostic responses and their register, completed study plans and seven paper accounts were preserved. Trial counts remain 61 model and zero universe trials. Broker connection and automatic purchases remain disabled.

Read-only shared Playwright checks passed at desktop and 390px widths without page errors, research/execution writes or mobile overflow. Native preview was attempted first and explicitly reported missing host browser libraries. Final deployment proof is under `/mnt/apps/developer/jellman86/artifacts/twenty/deploy-raw-event-schedule-20261023`. This accepts declared-exposure scheduling and state preservation, not source-wide action discovery, verified genuine settlement imports, registered raw backtests or initial-v1 completion.
