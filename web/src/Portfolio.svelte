<script lang="ts">
  import { onMount } from 'svelte';
  type Holding = { symbol: string; name: string | null; quantity: number; averagePrice: number | null; currentPrice: number | null; priceCurrency: string | null; costValue: number | null; currentValue: number | null; unrealizedProfitLoss: number | null; valueCurrency: string | null; managedQuantity: number };
  type Broker = { environment: string; account: string; currency: string; availableCash: number; reservedCash: number; holdings: Holding[]; totalCost: number | null; totalValue: number | null };
  type SimHolding = { symbol: string; quantity: number; costGbp: number; averagePriceGbp: number | null };
  type Sim = { id: string; runId: string; synthetic: boolean; contributionsGbp: number; cashGbp: number; reservedGbp: number; holdings: SimHolding[]; openOrders: number; lastMonth: string | null };
  type Overview = { broker: Broker | null; brokerError: string | null; brokerConfigured: boolean; simulated: Sim[] };
  let overview = $state<Overview | null>(null);
  let busy = $state(false);
  let error = $state('');
  let updated = $state('');
  const money = (n: number | null | undefined, currency = 'GBP') =>
    n == null ? '—' : new Intl.NumberFormat('en-GB', { style: 'currency', currency }).format(n);
  const price = (n: number | null, currency: string | null) =>
    n == null ? '—' : currency && /^[A-Z]{3}$/.test(currency) && currency !== 'GBX' ? money(n, currency) : `${n} ${currency ?? ''}`.trim();
  const shares = (n: number) => n.toLocaleString('en-GB', { maximumFractionDigits: 6 });
  const signed = (n: number | null, currency: string | null) => (n == null ? '—' : `${n > 0 ? '+' : ''}${money(n, currency ?? 'GBP')}`);
  const gain = $derived(overview?.broker?.totalValue != null && overview.broker.totalCost != null ? overview.broker.totalValue - overview.broker.totalCost : null);
  async function refresh() {
    busy = true;
    error = '';
    try {
      const response = await fetch('/api/portfolio');
      if (!response.ok) throw new Error('The portfolio is unavailable. Try again.');
      overview = await response.json();
      updated = new Date().toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
    } catch (e) {
      error = e instanceof Error ? e.message : 'The portfolio is unavailable.';
    } finally {
      busy = false;
    }
  }
  onMount(() => void refresh());
</script>

<div class="page-heading">
  <div>
    <p class="eyebrow">PORTFOLIO</p>
    <h1>What we have bought.<br /><em>What it is worth.</em></h1>
    <p class="intro">Real holdings come straight from the broker's own position records.<br class="desktop-break" /> Simulated portfolios show only booked purchases.</p>
  </div>
  <div class="mode-stamp">
    <span>{overview?.broker ? overview.broker.environment.toUpperCase() + ' ACCOUNT' : 'BROKER'}</span>
    <strong>{overview?.broker ? `Account ${overview.broker.account}` : overview?.brokerConfigured ? 'Unavailable' : 'Not connected'}</strong>
    <small>{updated ? `Updated ${updated}` : 'Loading…'}</small>
  </div>
</div>
{#if error}<p class="error" role="alert">{error}</p>{/if}

<section class="panel lab-results" aria-labelledby="broker-holdings-title">
  <div class="panel-heading">
    <div><p class="eyebrow">TRADING 212</p><h2 id="broker-holdings-title">Broker holdings</h2></div>
    <button class="secondary small-button" disabled={busy} onclick={() => void refresh()}>{busy ? 'Refreshing…' : 'Refresh'}</button>
  </div>
  {#if overview?.broker}
    {@const b = overview.broker}
    <div class="lab-metrics">
      <div><span>Holdings value</span><strong>{money(b.totalValue, b.currency)}</strong></div>
      <div><span>Cost</span><strong>{money(b.totalCost, b.currency)}</strong></div>
      <div><span>Unrealised gain / loss</span><strong class:negative={gain != null && gain < 0}>{signed(gain, b.currency)}</strong></div>
      <div><span>Cash available</span><strong>{money(b.availableCash, b.currency)}</strong></div>
    </div>
    {#if b.holdings.length}
      <div class="table-scroll">
        <table>
          <thead><tr><th>Holding</th><th>Shares</th><th>Average price</th><th>Current price</th><th>Cost</th><th>Value</th><th>Gain / loss</th><th>Bought by Twenty</th></tr></thead>
          <tbody>
            {#each b.holdings as h}
              <tr>
                <td>{h.name ?? h.symbol}<small>{h.symbol}</small></td>
                <td>{shares(h.quantity)}</td>
                <td>{price(h.averagePrice, h.priceCurrency)}</td>
                <td>{price(h.currentPrice, h.priceCurrency)}</td>
                <td>{money(h.costValue, h.valueCurrency ?? b.currency)}</td>
                <td>{money(h.currentValue, h.valueCurrency ?? b.currency)}</td>
                <td class:negative={(h.unrealizedProfitLoss ?? 0) < 0}>{signed(h.unrealizedProfitLoss, h.valueCurrency)}</td>
                <td>{h.managedQuantity ? shares(h.managedQuantity) : '—'}</td>
              </tr>
            {/each}
          </tbody>
        </table>
      </div>
      <p class="dataset-note">Prices are in each instrument's own currency; cost, value and gain are in the account currency, including the broker's FX effect. "Bought by Twenty" counts shares in Twenty's own execution journal; the rest were bought outside the app.</p>
    {:else}
      <div class="lab-empty"><h3>No holdings yet.</h3><p>The account holds only cash. Purchases appear here after they fill.</p></div>
    {/if}
  {:else if overview?.brokerConfigured}
    <p class="error">{overview.brokerError === 'broker-auth-rejected' ? 'Trading 212 rejected the API key. Check it was copied correctly and has not been revoked.' : overview.brokerError === 'broker-permission-denied' ? 'The API key lacks a read permission (account, positions, orders or history).' : 'The broker could not be read just now. Its rate limits or a network problem can cause this; refresh in a minute.'}<small>{overview.brokerError}</small></p>
  {:else if overview}
    <div class="lab-empty"><h3>No broker connected.</h3><p>Add read-only Trading 212 credentials to the private configuration to see real holdings here. See <a class="text-link" href="#settings">Settings</a>.</p></div>
  {/if}
</section>

<section class="panel lab-results" aria-labelledby="simulated-holdings-title">
  <div class="panel-heading"><div><p class="eyebrow">PAPER LEDGER</p><h2 id="simulated-holdings-title">Simulated portfolios</h2></div><span class="badge">SIMULATED MONEY</span></div>
  {#each overview?.simulated ?? [] as s}
    <div class="broker-account">
      <h3>Experiment {s.runId.slice(0, 8)}{s.lastMonth ? ` · last month ${s.lastMonth}` : ''}{s.synthetic ? ' · synthetic data' : ''}</h3>
      <p>Contributed {money(s.contributionsGbp)} · cash {money(s.cashGbp)} · reserved for {s.openOrders} open {s.openOrders === 1 ? 'order' : 'orders'} {money(s.reservedGbp)}</p>
      {#if s.holdings.length}
        <div class="table-scroll">
          <table>
            <thead><tr><th>Asset</th><th>Shares</th><th>Booked cost</th><th>Average cost</th></tr></thead>
            <tbody>{#each s.holdings as h}<tr><td>{h.symbol}</td><td>{shares(h.quantity)}</td><td>{money(h.costGbp)}</td><td>{money(h.averagePriceGbp)}</td></tr>{/each}</tbody>
          </table>
        </div>
      {:else}<p class="dataset-note">No filled purchases yet.</p>{/if}
    </div>
  {:else}
    {#if overview}<div class="lab-empty"><h3>No simulated purchases.</h3><p>Rehearse a month in <a class="text-link" href="#execution">Decisions & orders</a>.</p></div>{/if}
  {/each}
  <p class="dataset-note">The simulator models no market prices after purchase, so these show what was bought and its booked cost, not a current value.</p>
</section>
