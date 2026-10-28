# Operational UI — 28 October 2026

The owner chose Opus's Ember Ledger concept and asked for holdings and everyday controls, removing the research-first narrative. The app now opens on Portfolio. Company names, tickers, shares, native-currency quotes, account-currency values, gain/loss and cash come from the existing portfolio endpoint. Shares booked by Twenty remain distinguished from outside purchases. Missing values stay unavailable. Simulated portfolios show booked cost rather than an invented market valuation.

Activity reads the existing broker and simulated journals. Orders retains the existing explicit submission, reconciliation and cancellation actions. Paper accounts has its own page, with accepted balances separate from prepare/apply projections and explicit receipt downloads. Research remains a secondary route, including older bookmarked routes. Authentication and session expiry are retained. The charcoal/orange styling also covers login, settings and research.

Opus implemented `5c8e106`. Parent review added a visible two-row navigation on phones and kept cancellation/reconciliation phases visible when an order has earlier partial fills. Unfilled cancellation requests remain in the Open filter. These corrections change display only; no backend accounting, scientific identity, persisted format or execution authority changed.

## Verification

Private evidence root: `/mnt/apps/developer/jellman86/artifacts/twenty/opus-operational-ui-20261028`.

- `opus-preflight` and `parent-review-preflight`: expected clean source revisions checked before editing.
- `baseline-before-ui`: the operational landing regression failed against the previous UI.
- `ui-final/ui-checks.json`: Opus's 78 checks passed.
- `parent-red/ui/ui-checks.json`: 80 passed / three failed before the review fixes: phone navigation clipped links, and partial fills masked cancellation/unknown phases.
- `parent-green/ui/ui-checks.json`: 84 passed / zero failed after the fixes, including a further unfilled-cancellation regression. Screenshots cover the actual rendered Svelte app at 1280 and 390 pixels.
- `parent-svelte-check.log` and `parent-svelte-build.log`: zero Svelte errors/warnings and a successful production build. Ruff and `git diff --check` passed.

The committed `scripts/ui_workflow_check.py` exercises company names, GBX versus USD quotes, GBP values, gain signs, managed shares, missing/empty/error states, navigation, read-only page loading, login and session expiry. Broker positions and journal responses are explicitly synthetic intercepted HTTP fixtures; no genuine broker was called.

Paper checks use the real native API against a fresh verified copy of the sealed authored workspace. The workflow creates an opted-in stress-cost account, prepares the base account's third period without changing its two accepted periods, then explicitly applies it. The accepted sample advances from £39.50 / 0.9875 shares to £59.25 / 1.48125 shares. The sealed master and production registers are untouched. Local API processes were stopped after each run.

This frontend checkpoint did not rerun the .NET suite locally: backend source is unchanged by the UI commits. CI still runs the full application checks before image deployment. These checks establish engineering behaviour, not genuine financial returns or live broker integration. No full accessibility audit, Mac test or production browser acceptance is claimed. Research's inner advanced panels retain some explanatory copy. Activity records currently contain symbols rather than company names. Deployment requires successful exact-commit CI, image publication and Git-backed Dockhand verification.
