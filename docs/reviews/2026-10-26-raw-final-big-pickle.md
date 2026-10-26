# Permanent raw final review

Big Pickle independently reviewed `9cd86b9..fc0790a` read-only. It found the atomic claim/pair transition, budget neutrality, no-replacement behaviour, frozen development evidence and empty final accounts sound. Its review was static; the parent separately executed the tests and private HTTP rehearsal.

## Publication failure — retained conservative policy

The Medium finding identifies an operational limitation: a calculation can verify successfully but fail quota, file publication or bookkeeping, leaving its final attempt failed and its claim permanently spent. This implementation intentionally treats every exposed failed/interrupted attempt as spent, as declared by the original protocol. Automatically retrying calculation or substituting a later report would change that policy. A durable verified-result staging/republication protocol remains unimplemented. The final workflow now states that limitation explicitly, including missing final-action coverage and orphan-file quota pressure. A sparse authored quota blocker causes the completed calculation's publication to fail; the test checks the retained claim, `artifact-capacity`, `final-failed` audit event and refusal to rerun or replace it. This response documents and tests the conservative boundary; it does not claim publication recovery is solved.

## Accepted audit and integrity improvements

- New final requests retain `FinalRequestedCommit`; historical records without the optional field retain their original JSON. A label-only commit change is no longer described as proof of a scientific-code change.
- Claim validation reuses the existing trial-list connection instead of opening one per study.
- Final failure/interruption events use the `final-` prefix.
- Final report reads require `raw-study-final-v1`, in addition to original hashes and role bindings.
- Final UUID replay compares parsed identifiers; the shared claim lookup also rejects case aliases.
- The lifecycle documentation describes reserved slots becoming registered final attempts and the permanent claim.
- Admission refuses a backward clock before spending, rather than publishing inconsistent parent timestamps.

## Information windows and evidence

The review's objections 2/3 misread the inserted UTC window. Both claim dates and `universe_holdout_windows` represent the reserved outcomes; SQL inserts `ReservedStart` and `ReservedEnd`, not the wider information start. `RawFinalWindow` separately starts at `FinalDecisions[0]` minus price age, including twelve warm-up decisions before `ReservedStart`. Prior-exposure checks protect unseen reserved outcomes; active-reservation checks protect the full read footprint. Sharing already-known historical warm-up data is permitted, not another claim of unseen outcomes. The test now asserts every stored date/instant, synthetic flag and claim time explicitly.

Tests additionally pin slot neutrality, case-insensitive replay, refusal of a replacement after successful completion, and inability to cancel a completed result. The original private path cited in the review contains an intentionally preserved partial rehearsal after a driver assertion failed. The completed HTTP evidence is separately at `/mnt/apps/developer/jellman86/artifacts/twenty/raw-final-verified-20261026`: four families, 40 original trials, 120 account outcomes checked with independent Python Decimal arithmetic, four synthetic spent claims, exact cold-restart report/selection bytes and no genuine final access or broker request. Older v1 raw reports, a frozen v3 selection and completed notional-universe claims were checked in separate compatibility clones. These authored fixtures do not establish genuine data quality, historical profitability or full operational recovery.


## Second round and shared UUID follow-up

At `01c43ae`, the second read-only round found no new defect in the response delta, accepted the seven audit fixes and policy classifications, and retracted its two window objections. It independently inspected both completed rehearsal roots and confirmed the 40/120 counts. It correctly identified that the older model/universe paths still used ordinal UUID spelling and binary conflict lookups. The same parsed-GUID replay and case-insensitive shared-claim lookup now apply to all three protocols. A shared authored collision fixture fails on both older implementations before the correction; all three reject the case alias afterwards, and older same-study uppercase retries replay their original pair.

A separately labelled false-provenance fixture now exercises the non-synthetic guards from development through positive selection, a claim recorded with `synthetic=0`, and flat-final `research-rejected`. A new universe study over an aliased dataset then refuses known reserved exposure. Its values are entirely constructed; this tests the guarded branch and does not supply genuine historical final evidence. Residual limitations remain publication-stage recovery, mid-run/previously persisted clock regressions, and a killed-process partial-artifact recovery rehearsal.


The targeted third round at `559d762` confirmed all three final paths accept only D-format UUIDs, compare semantic IDs and reject case-alias conflicts before side effects. It found no release-blocking defect. A database expression-unique index is optional future defence in depth and would need an audit of legacy case aliases before migration. Malformed, coherently rewritten model/universe study metadata remains outside these guards' trust boundary; no repair is claimed. Exact-spelling replay remains covered by the existing failure/restart tests in both older protocols; the main workflow cases now additionally check the uppercase retry. The final-source rehearsal subsequently completed all four families and is recorded in the [deployment acceptance](../raw-study-final.md#authored-acceptance-and-reviewed-deployment-26-october-2026).
