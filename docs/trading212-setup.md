# Trading 212 setup and current integration shape

Reviewed 15 October 2026 against the official Help Centre, API reference and downloadable OpenAPI description. No credentials or account information were used in development.

## Can anyone create a key?

Eligible Trading 212 **Invest and Stocks & Shares ISA** accounts expose the API; this is not a general key for every product. The Help Centre excludes SIPP. In the relevant account, open **Menu → Settings → API (Beta) → Generate API key**. Accept the displayed risk notice, name the key and select its permissions. Configure an IP allowlist where practical. Save both the API Key and API Secret privately; the secret is shown only once. [Official key instructions](https://helpcentre.trading212.com/hc/en-us/articles/14584770928157-Trading-212-API-key).

Start with read permissions for account information, instruments, positions, orders and history. Order execution is a separate scope (`orders:execute` in the current schema). Do not paste either credential into chat, Git, a URL, screenshots or an issue. Demo and live use different credentials and origins. [Authentication and environments](https://docs.trading212.com/api).

Key availability establishes a technical capability; it does not override the API agreement. The linked terms explicitly restrict Algorithmic Trading in §4.2(a), with a definition in §11 that includes automated order initiation/timing/quantity. This is stronger than the testing/live discrepancy in our first review. The user has instructed development to continue without seeking clarification. We are continuing the application, transport and simulator; we do not claim the documented restriction has disappeared. No enquiry was sent or agreement accepted by this project. [Current linked API terms](https://www.trading212.com/legal-documentation/API-Terms_EN.pdf).

## Private application configuration

Twenty currently supports an optional **read-only account summary connection**. The durable coordinator and optional automatic worker are implemented but disabled without explicit private execution configuration, evidence and compatible demo validation. See [the coordinator setup](broker-execution.md). The default deployment has no credentials and makes no broker calls.

Create two private, read-only files outside the checkout and data archive, containing the key and secret separately. Set the following private container configuration:

| Setting | Value / purpose |
|---|---|
| `TWENTY_T212_ENVIRONMENT` | Explicit `demo` or `live`; no inferred default |
| `TWENTY_T212_KEY_FILE` | Absolute path to the mounted key file |
| `TWENTY_T212_SECRET_FILE` | Absolute path to the mounted secret file |
| `TWENTY_DATA_ROOT` | Private writable dataset/run/ledger directory; `/data` in the image |

Use Dockhand's encrypted settings and read-only secret mounts for deployment. Do not add literal credentials to Compose, repository `.env` files or generated Dockhand files. The application returns only a masked account number in the dashboard. Changing environments requires restarting through Dockhand; a demo credential must never silently fall back to live.

## Inputs needed for a real purchase

| Input | Required shape | Why it cannot be guessed |
|---|---|---|
| Account identity | Environment + broker account ID + independently verified ISA type + primary currency | Account summary supplies ID/currency, **no account-type field** |
| Strategy | Frozen model/config/data hashes, feature cutoff and a current eligible symbol ranking | A historical/synthetic backtest's last choices are not current market signals |
| Instrument mapping | Research security ID → broker ticker, listing/currency and dated ISA eligibility | Symbol names alone are ambiguous; metadata does not prove historical ISA eligibility |
| Executable price | Raw ask/limit in explicit GBP or GBX, observation timestamp, source | Adjusted total-return indices are not executable share prices; no general quote endpoint is advertised |
| Order rules | Verified share step/precision, minimum notional, price tick and tax/fee bounds | The current metadata schema does not supply every rule |
| Sleeve | Explicit £20 contribution policy, owned holdings, reserved cash and pending exposure | Other ISA cash/holdings must not automatically become strategy capital |
| Reconciliation | Durable intent, submit-start record, broker acknowledgement, order/fill IDs, cumulative quantities/debits, history cursor | Acknowledgement is not a fill; missing from pending orders is not cancellation |

The implemented planner initially accepts GBP/GBX purchases only. It rounds quantities down and reserves a maximum fee. Foreign-currency buys require a verified conversion/fee bound before they can provide a GBP spending cap. An unrestricted quantity market order cannot guarantee spending at most £20.

The transport implements Basic key/secret authentication, fixed demo/live origins, paced requests with rate-reset handling, quantity-limit submission, positions/pending orders, bounded history and cancellation requests. Exact bodies are archived privately without authentication headers. It does not retry POSTs. Timeouts, unexpected responses and malformed acknowledgements become **unknown**, requiring reconciliation rather than replay. A pending-order 404 and accepted cancellation both require further fill/history investigation. [Official request/response contract](https://docs.trading212.com/_bundle/api.json?download=).

## What can be exercised now

1. Train/backtest a hypothesis in Growth lab and inspect its frozen historical choices.
2. In Decisions & orders, create that run's simulated monthly contribution and buy plan.
3. Simulate partial fills, complete them or cancel the remainder. Repeated reconciliation cannot duplicate cash debits or holdings.
4. Pause new simulated decisions; the pause, funding history and fills survive restarts.
5. With private credentials configured, explicitly request a read-only account summary or capture actual history/positions evidence before configuring wallet interpretation.
6. With independently verified private inputs, configure the separate demo coordinator. Synthetic receipts cannot enable live; [full operating contract](broker-execution.md).

The simulator uses unrelated, labelled synthetic raw quotes and a separate account per run. It tests the software path, not investability, execution quality, current stock recommendations or future profit. The coordinator, broker-history/fill reconciliation and frozen current-signal path are implemented and tested with authored fixtures. Authenticated acceptance, current market/quote feeds and a defensible selected strategy remain tracked work in issue #10 and the data issues.
