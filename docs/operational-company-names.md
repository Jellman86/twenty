# Company names and order presentation

Activity and Broker orders now show company names from the existing portfolio response, retaining the strategy symbol below the name. A name is used only when the broker account/environment and exact `intent.brokerTicker` match the portfolio's instrument ID. The strategy symbol is a different namespace; it is not used to guess a match. Old records without a broker ticker can use their exact recorded symbol. Missing, conflicting or unavailable names fall back to the recorded symbol. Simulated orders are never labelled from broker positions.

The optional company lookup does not delay rendering the journal. It runs once on entry or explicit refresh, rather than on the five-second broker-status poll. Requests are aborted on navigation/refresh, and a delayed journal response cannot start another name lookup after leaving Orders. No cache of account positions persists across screens or sign-out. The lookup uses the existing same-origin, authenticated, read-only portfolio endpoint; it does not enable execution or change any order input.

Activity and Orders share the same status presentation. Earlier partial fills cannot conceal cancellation requests or an unknown/reconciliation state. The Signals, Simulated orders and Broker connection forms remain available under native expandable sections, leaving the broker order list as the main screen.

## Contracts and verification

Only frontend display and the rendered-UI checker changed. Existing portfolio, broker-journal and paper-ledger response formats, financial/accounting logic, report formats, frozen study inputs and execution payloads remain unchanged. No schema migration or new backend endpoint is introduced.

Private evidence: `/mnt/apps/developer/jellman86/artifacts/twenty/operational-company-names-20261028`.

- `preflight`: expected clean source and deployed revision `3e4a964` checked before editing.
- `red/ui/ui-checks.json`: 96 passed / seven failed against the previous UI, pinning missing company names, expanded advanced forms and the cancellation label in Orders.
- `navigation-red/ui/ui-checks.json`: 112 passed / one failed, demonstrating a late company lookup after leaving Orders.
- `reviewed-green/ui/ui-checks.json`: 113 passed / zero failed at 1280 and 390 pixels, including optional-lookup failure/delay, conflicting names, account/environment mismatch, broker-ticker namespace isolation, native expandable forms, status consistency and navigation cancellation.
- `reviewed-green/source-before.json`, `source-after.json` and `run.json`: frontend source, served assets and local backend binaries stayed unchanged throughout the workflow. Actual rendered screenshots are in `reviewed-green/ui`.

Broker data in these checks is intercepted synthetic HTTP, not genuine holdings. The same workflow still exercises the actual local authored-paper API on a fresh verified copy of the sealed fixture: explicit split-enabled creation, preparation without changing accepted balances, then application. The original sealed store and production registers remain untouched. All owned local API processes stop after the checks.

Names cover positions reported by the current broker portfolio, not a complete historical instrument directory. Sold or renamed instruments without an exact current match keep their recorded symbols. This does not reconstruct historical company identities. No genuine broker integration, production browser acceptance or full accessibility audit is claimed. Backend source is unchanged, so the .NET suite was not rerun locally for this frontend checkpoint; CI remains required before deployment.
