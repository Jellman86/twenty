# Verified offline replay archive

`RawReplayArchive` adds a versioned file contract around the [explicit event replay](raw-claim-replay.md). It binds exact request and dataset bytes, reconstructs as-of slices, executes a new ledger and stores a request/result receipt. Recovery re-executes the request and requires byte-identical output. Saved balances never initialize a ledger. This is an offline accounting tool, not a registered strategy or a background worker that resumes half-completed work.

## Bound inputs and execution identity

A `RawReplayDocument` declares archive version, engine version, `RawReplayBuildIdentity`, maximum raw-price and FX ages in ticks, explicit FX purpose and ordered `RawReplayEventSpec` rows. Each event gives its identifier, contiguous sequence, booking/as-of instant, dataset SHA-256 and typed operation. Market slices are reconstructed from the original validated dataset at that instant. The reader verifies exact dataset bytes before parsing; it does not accept serialized `DataSlice` state or a caller-supplied cash balance. Request bytes are copied before resolver callbacks and dataset bytes are copied before use.

The separate `raw-replay-core-runtime-v1` identity binds the actual Core DLL SHA-256, .NET framework description and runtime identifier. It is intentionally distinct from the Core+Research identity used by registered model/universe studies: this archive neither registers a study nor confers final-evaluation authority. A changed identity or engine/archive version refuses replay. The receipt does not support migration to a different scientific implementation. Runtime environment dependencies such as timezone data are not independently archived; recomputing the exact result detects their effect on these outputs but does not certify every platform.

JSON rejects unknown members, duplicate members at any nesting level, unknown event kinds, integer enum values, missing required constructor fields and offset-free timestamps. Calendar serialization includes the full table and its digest; reading reconstructs and verifies it. Derived display-only fill-policy/fill fields are excluded rather than accepting and ignoring supplied values. Modelled fills still rerun their declared planner and retain all raw replay timing, price, FX, funding and action-history checks.

## Bounded artifacts and recovery

| Resource | Limit |
| --- | ---: |
| Request JSON | 2 MiB |
| Events | 10,000 |
| Distinct datasets | 16 |
| One dataset | 8 MiB |
| Aggregate resolved dataset bytes | 32 MiB |
| Observations across cached unique dataset/time slices | 500,000 |
| Sum of slice observations across every event, including cached repeats | 500,000 |
| Serialized result | 16 MiB |
| Receipt file | 32 MiB |

A receipt retains original request bytes and exact result bytes as base64, with both hashes and the archive version. `Verify` checks framing/hashes, resolves and validates input datasets, executes from an empty private ledger and compares the result bytes. Replacing a balance and recomputing its checksum still fails when the calculated result differs. These are unsigned local integrity checks; an attacker replacing the request, inputs and result together is not authenticated by local hashes. Dataset source hashes remain upstream assertions, not proof of vendor authenticity, legal entitlement or complete corporate-action coverage.

`Store` validates and executes before creating output. It writes a private temporary file, flushes its contents and renames without overwrite to `raw-replays/raw-replay-archive-v1/<request-sha256>.json`. Identical concurrent stores converge on one exact receipt. A conflicting existing file is preserved and refused. Own temporary files are cleaned after normal failure; orphaned interrupted stages are ignored and are not promoted or deleted by another replay. A new receipt file is mode 0600 and newly created Unix directories use mode 0700. File flush plus rename protects against exposing partial writes to readers; power-loss durability of directory metadata and backup coverage are separate operational requirements.

`Load` checks the requested address and runs full verification again. Recovery is safe re-execution of a bounded complete document, with no broker/network side effects and no duplicate deposits/fills in a reused ledger. It requires all referenced dataset files; this receipt is not a self-contained backup of those inputs. Workspace-wide quota management, automatic orphan cleanup, incremental checkpoints and durable job scheduling are not provided.

## Offline command line

The identity for the currently built executable is available without data access:

```sh
dotnet run --project src/Twenty.Data -c Release -- raw-replay identity
```

Author a request using that exact identity and the `RawReplayDocument`/event records, serialized with `RawReplayJson.Options`. Dataset hashes refer to files imported with the existing dataset archive, under `<dataset-root>/datasets/twenty.dataset.v1/<sha256>.json` (use the current `DatasetValidator.SchemaVersion` value rather than assuming a directory name). Input artifacts and receipts belong in private storage outside Git.

```sh
dotnet run --project src/Twenty.Data -c Release -- raw-replay run \
  --request /private/request.json --datasets /private/dataset-root --archive /private/replay-root

dotnet run --project src/Twenty.Data -c Release -- raw-replay verify \
  --request-sha <sha256> --datasets /private/dataset-root --archive /private/replay-root
```

The run command stores and reopens the receipt before reporting success. Verify uses fresh process state and re-executes the ledger. CLI output names the request/result hashes and explicitly labels the tool offline and unregistered; errors report bounded codes rather than raw input contents. Exact request bytes define the address: whitespace changes create another request artifact, not a research-trial identity or an alternative final evaluation.

## Authored validation and remaining acceptance

Forty-six added cases bring the .NET suite to 1,009 tests. Cases cover exact-byte round trips, dataset caching, request snapshotting, all event families including modelled fills, pending dividend/terminal estimates, stock delivery followed by a split, version/code/runtime refusal, strict JSON/calendar checks, byte/dataset/slice and repeated-work bounds, interrupted staging, concurrent writes, address swaps, conflicting receipts and forged results. Disabling result comparison makes the forged-balance regression fail; the restored implementation passes. All financial inputs are authored.

This closes the explicit offline archive/re-execution dependency. Automatic corporate-action scheduling, registered raw changing-universe experiments, verified genuine settlement/FX imports, combined capture-to-study acceptance and final full-volume/authenticated/active-work operational recovery remain open. Existing model/universe artifact formats and live broker authority are unchanged.

Local validation also passes 39 Python tests, Ruff, Svelte check/build, sensitive-path checks, research-boundary probes and full-history/files secret scanning. A fresh-process CLI rehearsal independently checks 140 GBP cash and 300 GBP equity with Python Decimal; create/reopen/retry preserve exact receipt bytes and timestamp, while changed dataset bytes, a forged rehashed result, an existing conflicting file and a different build are refused. Private final receipts are under `/mnt/apps/developer/jellman86/artifacts/twenty/raw-durable-replay-final-20261023`. No vendor/broker request, training, accelerator or genuine final evaluation is used.
