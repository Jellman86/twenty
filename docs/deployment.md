# Research deployment

Application source: `Jellman86/twenty`, branch `dev`. Releases remain on `main` only after a deliberate release gate. CI tests .NET/Python/Svelte, scans full history and files for secrets, builds the image, tests its HTTP contract in a constrained container, then publishes `ghcr.io/jellman86/twenty:dev` and a commit tag.

Compose source: `Jellman86/docker-configs`, branch `main`, `twenty/docker-compose.yml`. The deployed stack has a bounded, explicitly requested CPU research worker and a private `/data` volume for imported datasets, experiment artifacts, paper accounts and execution journals. Preserve that volume across recreation. The default deployment has no broker credentials or private execution configuration; the optional automatic broker worker remains off. Web deployment does not activate trading or schedule training. See [initial application](initial-application.md) for resource bounds and [broker execution](broker-execution.md) for private configuration and authenticated acceptance requirements.

On Quark, resolve the Git stack in Dockhand, verify CI and the registry manifest, POST `/api/git/stacks/{id}/sync`, check the synced commit/status, then POST `/api/git/stacks/{id}/deploy`. Keep `repullImages` and `forceRedeploy` enabled. Verify the running image and readiness through Dockhand. Do not use Docker CLI for lifecycle changes.

Private route: `twenty.pownet.uk` resolves through UniFi DNS to Quark. Nginx Proxy Manager terminates HTTPS and proxies to `twenty-api:8080` over the existing `general_brg`. LAN/Tailscale source restrictions apply. No public DNS/tunnel is created. The app has no user authentication, so keep this route private. A public source repository does not make the service public.

Rollback: revert the app on `dev`, wait for successful CI/publication and repeat Dockhand sync/deploy. If Compose changes caused the problem, revert that change in docker-configs first and wait for Compose CI. Secrets must stay in Dockhand's encrypted store or local private credential files; never put them in Compose or Git.
