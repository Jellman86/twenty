# Alignment-first testing process

Adopted 27 October 2026 at the owner's request, after repeated misalignment found late: a test filter that matched no tests but exited successfully, sudo declared blocked before the documented host credentials were checked, report/API versions reconciled after implementation, and handoffs that carried conclusions without the evidence behind them. Passing unit tests did not catch these, because each came from an assumption *between* components, not inside one.

The rule: **check alignment before editing, and prove that tests actually ran.** `scripts/checkpoint.py` automates the mechanical parts. The judgement steps are a checklist, recorded in each checkpoint note.

## Before editing (preflight)

1. **Pin the baseline.** Record the exact commit and branch on every host involved (Quark, the Mac, CI, the deployed image). Different machines often hold different revisions.

   ```sh
   python3 scripts/checkpoint.py preflight --expected-head "$(git rev-parse HEAD)" \
     --output-dir /mnt/apps/developer/jellman86/artifacts/twenty/<checkpoint>/preflight
   ```

   Add `--api-base https://twenty.pownet.uk --expected-deployed <sha>` to fail when the deployed image is not the expected commit. Each `--expect-api 'capabilities.<field>=<json>'` asserts one live contract value. Type matters: `true` does not equal `1`.
2. **Inventory the contracts the change touches.** List report/engine versions, API and UI fields, stored artifact formats and frozen studies before writing code. Decide up front which old artifacts must still reproduce byte-for-byte and which identities change.
3. **Check access before declaring something blocked.** Read `~/.agents/ACCESS.md` and the project docs for configured credentials, contacts and keys. "Needs the owner" is a conclusion that requires evidence.
4. **Confirm the test selection.** Read the actual test class and method names before relying on a filter; file names are not test names. Set `--min-passed` to the number of tests you expect the filter to select, so a filter that silently matches fewer fails.

## While implementing

5. **Write the cross-component failure first.** Besides the unit regression, write a failing case at the boundary the change crosses: the next consumer of the output, the persisted format, the API/UI contract or the older artifact. Use authored inputs that do not just repeat the implementation's own assumptions; vary the input independently where possible.
6. **Keep old evidence reproducible.** Rerun at least one previously recorded artifact and compare hashes when an algorithm or format it depends on changes.

## Before calling it done

7. **Non-vacuous test evidence.** Run tests through the checker, which refuses zero-match runs, failures, skips counted as passes, malformed or inconsistent results, and source changes during the run:

   ```sh
   python3 scripts/checkpoint.py dotnet --project tests/Twenty.Tests/Twenty.Tests.csproj \
     --filter 'FullyQualifiedName~<Area>' --min-passed <expected count> --output-dir <new evidence dir>
   python3 scripts/checkpoint.py verify --preflight <preflight.json> --tests <tests.json> --output-dir <new evidence dir>
   ```

   `verify` fails if any visible source file changed after the evidence was produced, or if a result file was altered. Evidence directories must be new and must sit outside the repository.
8. **Exercise the real workflow.** Unit tests are not completion. Run the actual CLI, API or UI path the change affects (deployed, or a private clone) and record what was checked.
9. **State the gaps.** The checkpoint note says what was *not* verified: hosts not tested, mocked devices, synthetic versus historical inputs, review not performed.

When dependency manifests or locks change, inspect the repository's open dependency advisories and the upstream patched-version information. Compare the entire resolved package delta, then test the core environment and each affected optional extra separately. Record installed versions and skipped cases: base-only CI does not exercise an optional runtime. Retain justified resolver floors, keep numerical dependencies fixed where possible, and check remote alert closure after indexing. Record secret scanning and dependency-advisory review as separate checks.

## Handoffs

A handoff passes on evidence paths and receipts, not only conclusions: the preflight and verification JSON, the commits on each host and any open assumptions. The receiving session reruns `preflight` before editing, rather than trusting the summary.

## What the checker does not do

It binds source identity, runtime versions, deployed commit and test execution counts. It cannot judge whether a test is meaningful, whether fixtures are independent of the implementation, or whether a workflow check was thorough. Those remain review responsibilities under steps 2–6 and 8–9. It makes no application writes, deployments, credential reads or host changes; `preflight --api-base` makes two read-only GET requests.

CI runs the full .NET suite through the same checker, with `--min-passed 1502`, and uploads the results as the `dotnet-test-evidence` artifact. The floor includes the 1,443-test raw-price/share-unit baseline, 20 cash-dividend paper-account cases, one exact-decimal HTTP/storage consumer case and 38 pending-dividend cases added on 29 October. CI also runs `npm test --prefix web` for the browser request serializer before Svelte checking/build. Raise the floor when adding tests; never lower it to make a run pass without recording an intentional selection change.
