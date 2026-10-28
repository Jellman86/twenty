# Recovery bundle acceptance — 28 October 2026

This checkpoint follows the alignment-first process at baseline `e88f6f0b7b965f35a2b322cd36b5ed4ad087a636`. Initial preflight recorded the clean `dev` checkout, Linux/x86_64 and actual tool versions before edits. The goal runner still held the earlier usage-limited roadmap goal and refused a replacement; useful work continued without changing or falsely completing that goal.

## Contracts and failure cases

Only a new offline operational manifest and Python helper/test surface change. C# accounting, research identity, report/replay versions, application API/UI, persisted studies, paper accounts and deployment configuration stay unchanged. Existing artifacts are compared as bytes; no research calculation is rerun. The manifest records its own explicit version, caller-supplied captured application commit, timestamp and relative file/size/hash rows. Its pin must be retained separately from the copy.

The initial test import failed because the helper did not exist. Twelve tests now exercise a real SQLite fixture and independent filesystem copies. The cross-consumer case invokes the actual CLI on a damaged transferred report and requires nonzero exit without printing the authored contents. Other cases refuse missing/extra/modified files, wrong/changed pins, corrupt SQLite, WAL/SHM/rollback sidecars, symlinks/hardlinks, invalid manifest paths/duplicates, unsafe permissions, in-place manifests, repository paths, case collisions and non-ASCII normalization hazards. Early guard ordering produced the wrong refusal codes for two fixtures; those were corrected before acceptance.

## Full-store native workflow

The deployed app reported `e88f6f0`. Its registers were inactive and no SQLite sidecars existed. A read-only Docker copy captured 211 files / 65,431,118 bytes. All source hashes matched before/after copying and the copy matched them exactly. The private owner-only sealed store passed manifest pin, complete file inventory and immutable/read-only SQLite checks.

A separate native loopback working copy preserved 12 collections, all 58 report responses, four diagnostic responses/history and original study-plan bytes, both initially and after cold restart. Seven existing model paper accounts were retained; the raw paper register was empty. Readiness was approximately 1.2 and 1.1 seconds. Collection was disabled, broker credentials were absent from the restored process and automatic purchases were disabled. No production write, new research trial or new final evaluation occurred.

A separately damaged report was refused by the actual bundle verifier before application startup. On another working copy the API refused the same class of corruption before and after restart, leaving the bytes damaged rather than repairing them. The sealed original was reverified unchanged afterwards.

Authentication was exercised only on a separate full clone with a new authored account. Anonymous access refused; authenticated reads preserved original reports; the session survived cold restart; a foreign-Origin write refused; logout removed access. Captured original-account material stayed untouched in the sealed store. This does not establish login with the original operator password.

Private hashes, baselines, sanitized proofs and process logs are under `/mnt/apps/developer/jellman86/artifacts/twenty/recovery-bundle-20261028`; initial preflight/red/unit evidence is under `raw-paper-next-20261028`. Large/private originals remain outside Git.

## Remaining gates

The Mac reported offline in Tailscale and bounded SSH attempts timed out, so no cross-host restore is claimed. This package makes that check repeatable when the Mac returns. A source-only Mac accounting check from 27 October is separate evidence. Independent durable retention, a mounted-container restore, original-account login, storage pressure, paper action/claim integration and combined initial-v1 acceptance remain open. No peer review or fresh deployment is claimed for this operational helper. Developer SSD artifacts remain outside the ordinary application backup.
