# Research deployment

Application source: `Jellman86/twenty`, branch `dev`. Releases remain on `main` only after a deliberate release gate. CI tests .NET/Python/Svelte, scans full history and files for secrets, builds the image, tests its HTTP contract in a constrained container, then publishes `ghcr.io/jellman86/twenty:dev` and a commit tag.

Compose source: `Jellman86/docker-configs`, branch `main`, `stacks/twenty/compose.yaml`. The deployed stack has a bounded, explicitly requested CPU research worker and a private `/data` volume for imported datasets, experiment artifacts, paper accounts and execution journals. Preserve that volume across recreation. The default deployment has no broker credentials or private execution configuration; the optional automatic broker worker remains off. Web deployment does not activate trading or schedule training. See [initial application](initial-application.md) for resource bounds and [broker execution](broker-execution.md) for private configuration and authenticated acceptance requirements.

On Quark, resolve the Git stack in Dockhand, verify CI and the registry manifest, POST `/api/git/stacks/{id}/sync`, check the synced commit/status, then POST `/api/git/stacks/{id}/deploy`. Keep `repullImages` and `forceRedeploy` enabled. Verify the running image and readiness through Dockhand. Do not use Docker CLI for lifecycle changes.

Private route: `twenty.pownet.uk` resolves through UniFi DNS to Quark. Nginx Proxy Manager terminates HTTPS and proxies to `twenty-api:8080` over the existing `general_brg`. LAN/Tailscale source restrictions apply. No public DNS/tunnel is created. A public source repository does not make the service public.

## Sign-in

Since 16 October 2026 every API route except `/health/*` and `/api/auth/*` needs the single operator account; the static page shell loads anonymously and shows the sign-in screen. Keep the route private anyway: the login is a second layer, not a reason to publish the service.

- **First run:** with no account in `/data/auth/account.json`, the app logs a one-time setup code at startup (a new one on every restart until setup completes). Read it from the container log in Dockhand, open the site and create the account. To avoid the log, set `TWENTY_SETUP_CODE` in Dockhand's encrypted environment instead; it is never logged.
- **Storage:** only a PBKDF2-SHA256 hash (600,000 iterations, random salt) is stored. Session-cookie keys live in `/data/auth/keys`, so redeploying keeps you signed in. Both are on the private volume; preserve it.
- **Sessions:** HttpOnly, `SameSite=Strict`, `Secure` cookies lasting seven days with sliding renewal. Changing the password signs out every other device. Five wrong passwords lock sign-in for 15 minutes, and sign-in requests are limited to ten a minute per address.
- **Lost password:** stop the stack, delete `/data/auth/account.json` from the volume, start it again and use the new setup code. Research, ledger and journal data are untouched.
- **Switching sign-in off:** set `TWENTY_AUTH=off` (Dockhand stack variable) to remove the sign-in requirement, for example while only a demo key is connected. The app logs a warning and Settings says sign-in is off. It refuses to start with sign-in off while a **live** Trading 212 key is configured. The account and session keys stay on the volume, so removing the variable restores sign-in as it was.
- **Plain HTTP:** cookies are `Secure` by default, which Nginx Proxy Manager's HTTPS satisfies. For a local run over plain HTTP in a browser that rejects such cookies, set `TWENTY_INSECURE_COOKIES=true`; never set it on Quark.

Rollback: revert the app on `dev`, wait for successful CI/publication and repeat Dockhand sync/deploy. If Compose changes caused the problem, revert that change in docker-configs first and wait for Compose CI. Secrets must stay in Dockhand's encrypted store or local private credential files; never put them in Compose or Git.


## Quark checkpoint, 20 October 2026

Dockhand deployed application `47d25c8` after passing CI and confirming the rolling image matched its commit image. Compose source remained byte-identical to the previously deployed Git-backed stack. Readiness is healthy in research mode; automatic purchases remain disabled. The 61 run records, three datasets, seven paper accounts, signals, universe register and completed engineering study summary were preserved. All 58 successful report artifacts remain readable under their original stored hashes, with financial values unchanged; HTTP reports now include an empty `stockTransfers` collection on ordinary steps from the successor-settlement schema addition. The existing study's exact original plan bytes were preserved. Private before/after evidence is retained outside Git. This deploy did not create a model trial or open a final holdout.
