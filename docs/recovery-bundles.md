# Offline recovery bundles

`scripts/recovery_bundle.py` seals and verifies a private offline store copy before application startup. It is a transport/integrity check, separate from the application's report bindings, deployment and research authority. It never captures a running application, changes its data, repairs a bundle or starts a service.

## Capture and seal

Establish a quiet source first: no active trials/studies/paper operations and no SQLite WAL, SHM or rollback-journal files. Use the documented read-only capture procedure, checking the source file hashes before and after copying and comparing the copy. This checker does not make a live filesystem copy atomic. A changing store needs a separately designed consistent backup procedure.

Keep the entire copy and its parent evidence directory private (owner-only directories/files) outside Git. It contains original authentication material and financial artifacts. The script refuses publicly/group-readable files, links, special files and repository paths. Keep the sealed original unopened; application startup and migrations belong on a separate working copy.

```sh
python3 scripts/recovery_bundle.py seal \
  --snapshot /mnt/apps/developer/jellman86/artifacts/twenty/<attempt>/sealed-store \
  --manifest /mnt/apps/developer/jellman86/artifacts/twenty/<attempt>/manifest.json \
  --source-commit <captured-application-commit>
```

Record the returned manifest SHA-256 independently of the transferred bundle. The source commit is the caller's assertion about the captured application, not an authenticated signature or a claim that every historical report used that build. Existing manifests cannot be overwritten.

## Transfer, verify and open a working copy

Transfer through an owner-authorized private channel, retaining restrictive permissions. On the receiving host, verify using the independently retained manifest pin before starting the application:

```sh
python3 scripts/recovery_bundle.py verify \
  --snapshot <private-received-store> --manifest <received-manifest.json> \
  --expected-sha256 <original-manifest-sha256>
```

Every relative filename, byte count and SHA-256 must match, with no additional or missing files. SQLite `quick_check` uses immutable read-only connections and creates no journal sidecars. Changed files during hashing/checking refuse. The first contract accepts ASCII paths only and rejects case collisions, avoiding silent losses or filename-normalization changes on a Mac. There are at most 10,000 entries and 512 MiB of file content. Non-ASCII names and larger stores need an explicitly reviewed contract extension.

Only a passing verified copy should be duplicated into a working restore directory. Start an isolated loopback host with collection disabled and broker environment absent. Compare collections, exact reports, diagnostics and stored account state against the captured HTTP baseline, then cold-start it and compare again. Exercise authentication with an authored account on another clone; preserve the original operator account untouched. Reverify the sealed master afterwards. Never use startup to "repair" the only retained copy.

## Evidence and limits

[28 October acceptance](recovery-bundle-acceptance-2026-10-28.md) exercises a full-store copy, the actual CLI and native API, corrupted-copy refusal and authored authentication. Unit tests additionally pin missing/extra/modified files, wrong pins, invalid manifests, traversal, duplicate names, SQLite sidecars/corruption, links, permissions and Mac filename hazards.

This proves bounded byte integrity and SQLite readability. It does not authenticate a coherently replaced source/manifest, certify source data, prove original-account login, establish a backup schedule/retention policy, make developer state part of the ordinary application backup, or establish independent durable or mounted-container recovery. Those require separate evidence. No external USB operation, application deployment, broker request or genuine final evaluation is part of this tool.
