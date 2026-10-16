# Data collectors

Implemented 16 October 2026. Twenty collects free public data in two places from the same image: **locally** inside the app on Quark, and **remotely** on a disposable VPS that ships its captures to Quark. Source choice and limits are in [the free-data plan](market-data-selection.md); the owner's scraping decision is in [data sources](data-sources.md#scraping-policy-and-failure-handling).

## What is collected

| Source | Every | Default host | Check before storing |
|---|---|---|---|
| `nasdaq-listed`, `nasdaq-other`: Nasdaq Trader symbol directories | day | any | exact column header and the `File Creation Time` trailer |
| `tiingo-tickers`: Tiingo's public `supported_tickers.zip` | day | any | zip entry and column header |
| `french-factors-monthly`, `french-factors-daily`: Fama–French factors | week | local | zip entry and column header after the notes |
| `shiller-ie-data`: Shiller's `ie_data.xls`, found from the link on shillerdata.com | week | local | a real spreadsheet (OLE2 signature) |

None needs an account. "Default host" decides which role collects a source when none is named: a local collector takes `local` and `any`; a remote collector takes `remote` only. `TWENTY_COLLECTOR_SOURCES` overrides that with a comma-separated list, or `all`. Sources that need a free key (Tiingo prices, FRED) or the SEC contact are not wired in yet.

## How a capture is kept

Under `<data>/captures/capture-v1/`:

- `objects/<sha256>.bin`: each distinct version of a file, stored once.
- `observations/<source>/<yyyy-mm>.jsonl`: one line per successful check, including unchanged ones (`notModified: true`), with URL, hash, size, time, collector name, and for shipped captures the time Quark received it. An unchanged daily listing is still evidence the symbols were listed that day.
- `status/<source>.json`: last success, validators for conditional requests, failures and any pause.

Files are owner-only and the store has a single writer. Nothing here goes into Git or the published image. Turning these raw files into dataset observations (French and Shiller into return series, listing snapshots into listing intervals) is the next step and needs the data-contract change approved in the plan.

## Politeness and failure

- One request at a time, at least 10 seconds between requests to the same host, conditional `If-None-Match` and `If-Modified-Since`.
- Redirects are followed only to the source's own hosts, at most three.
- `403`, `429`, a `cf-mitigated: challenge` header, or an HTML page where a file was expected all count as a **block**. The source pauses for 6 hours, doubling on each repeat up to 7 days. It is not retried from elsewhere.
- Other failures (bad content, `5xx`, timeouts) pause for 1 hour, doubling. Content that fails its check is never stored.
- No login, paywall or bot-challenge solving is built in. An optional HTTP egress proxy (`TWENTY_COLLECTOR_PROXY`, for example Gluetun's) only changes which address requests come from.

## Settings

| Variable | Where | Meaning |
|---|---|---|
| `TWENTY_COLLECTOR_ROLE` | both | `off` (default), `local` inside the app, `remote` with `--collector` |
| `TWENTY_COLLECTOR_SOURCES` | both | optional source list or `all` |
| `TWENTY_COLLECTOR_NAME` | both | label recorded on every observation, e.g. `quark` or `oracle-1` |
| `TWENTY_COLLECTOR_PROXY` | both | optional HTTP proxy URL |
| `TWENTY_INGEST_TOKEN` or `TWENTY_INGEST_TOKEN_FILE` | both | at least 32 characters; on Quark it enables `POST /api/ingest/captures`, on the VPS it authenticates shipping |
| `TWENTY_SHIP_URL` | remote | `https://twenty.pownet.uk/api/ingest/captures` |

Settings are checked at start-up: an unknown source, a short token or a remote role without a ship URL stops the process rather than failing later. **Settings → Data collection** in the app shows each source's last capture, which collector took it, the number of captures and any pause.

## Quark (local)

Add to the Twenty service's environment in Dockhand, with the token as an encrypted secret:

```yaml
TWENTY_COLLECTOR_ROLE: local
TWENTY_COLLECTOR_NAME: quark
TWENTY_INGEST_TOKEN: ${TWENTY_INGEST_TOKEN}   # only if a remote collector will ship here
```

Captures share the existing private `/data` volume. Collection runs every 15 minutes, takes what is due and sleeps.

## Remote VPS

The VPS runs the same image headless (`--collector`): no web server, collect what is due, ship to Quark, keep only the latest version of each file (so tomorrow's conditional request can still be confirmed) and anything not yet acknowledged.

1. **Host.** Oracle Cloud Always Free. CI publishes `linux/amd64` only, so use the `VM.Standard.E2.1.Micro` (AMD) shape, not Ampere A1, until an arm64 image is added. Expect Oracle to reclaim an idle instance; the VPS holds nothing that is not on Quark or still queued.
2. **Reaching Quark.** `twenty.pownet.uk` is LAN- and Tailscale-only. Join the VPS to the tailnet, and map the name to Quark's Tailscale address so the TLS certificate still matches.
3. **Run** with Docker Compose, the token in a private file:

```yaml
services:
  twenty-collector:
    image: ghcr.io/jellman86/twenty:dev
    command: ["--collector"]
    restart: unless-stopped
    read_only: true
    user: "1654:1654"
    environment:
      TWENTY_COLLECTOR_ROLE: remote
      TWENTY_COLLECTOR_NAME: oracle-1
      TWENTY_SHIP_URL: https://twenty.pownet.uk/api/ingest/captures
      TWENTY_INGEST_TOKEN_FILE: /run/secrets/twenty_ingest_token
      # TWENTY_COLLECTOR_SOURCES: nasdaq-listed,nasdaq-other,tiingo-tickers
    extra_hosts:
      - "twenty.pownet.uk:${QUARK_TAILSCALE_IP}"
    volumes:
      - collector_data:/data
      - ./twenty_ingest_token:/run/secrets/twenty_ingest_token:ro
    tmpfs:
      - /tmp:rw,nosuid,nodev,noexec,size=16m
    healthcheck:
      disable: true
volumes:
  collector_data:
```

Use the same token value as Quark's `TWENTY_INGEST_TOKEN`. The image's built-in health check probes the web port, which a headless collector does not open, so it is disabled here.

Quark checks every shipped capture's hash and size, keeps it once, and records the observation with the remote collector's name. Replaying a capture is harmless. A body that does not match its observation is refused.

## Tests

`tests/Twenty.Tests/CaptureTests.cs` covers conditional requests and unchanged-day observations, validation, block back-off, redirect limits, Shiller link discovery, role selection, start-up checks, and shipping to a real in-process receiver (wrong token refused, acknowledgement before deletion, idempotent replay, tampered body refused). On 16 October 2026 a local run against the live sources captured all six and passed every content check.
