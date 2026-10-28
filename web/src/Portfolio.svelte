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
  // 'broker' or a simulated portfolio id.
  let view = $state('broker');
  const brokerMessages: Record<string, string> = {
    'broker-auth-rejected': 'Trading 212 rejected the API key. Check it has not been revoked.',
    'broker-permission-denied': 'The API key is missing a read permission (account, positions, orders or history).',
  };
  const isoCurrency = (c: string | null | undefined) => !!c && /^[A-Z]{3}$/.test(c) && c !== 'GBX';
  const money = (n: number | null | undefined, currency = 'GBP') =>
    n == null ? '—' : new Intl.NumberFormat('en-GB', { style: 'currency', currency }).format(n);
  // Quotes stay in the listing's own unit; GBX (pence) is never relabelled as pounds.
  const price = (n: number | null, currency: string | null) =>
    n == null ? '—' : isoCurrency(currency) ? money(n, currency!) : `${n.toLocaleString('en-GB', { minimumFractionDigits: 2, maximumFractionDigits: 4 })} ${currency ?? ''}`.trim();
  const shares = (n: number) => n.toLocaleString('en-GB', { maximumFractionDigits: 6 });
  const signed = (n: number | null, currency: string | null) =>
    n == null || !isoCurrency(currency) ? '—' : `${n > 0 ? '+' : n < 0 ? '−' : ''}${money(Math.abs(n), currency!)}`;
  const tone = (n: number | null) => (n == null || n === 0 ? '' : n > 0 ? 'gain' : 'loss');
  const broker = $derived(overview?.broker ?? null);
  const gain = $derived(broker?.totalValue != null && broker.totalCost != null ? broker.totalValue - broker.totalCost : null);
  const gainPct = $derived(gain != null && broker?.totalCost ? (gain / broker.totalCost) * 100 : null);
  const sim = $derived(overview?.simulated.find((s) => s.id === view) ?? null);
  async function refresh() {
    busy = true;
    error = '';
    try {
      const response = await fetch('/api/portfolio');
      if (!response.ok) throw new Error();
      overview = await response.json();
      if (view !== 'broker' && !overview!.simulated.some((s) => s.id === view)) view = 'broker';
      if (view === 'broker' && !overview!.brokerConfigured && overview!.simulated.length) view = overview!.simulated[0].id;
      updated = new Date().toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
    } catch {
      error = 'Portfolio unavailable. Check the connection and refresh.';
    } finally {
      busy = false;
    }
  }
  onMount(() => void refresh());
</script>

<div class="page-head">
  <h1>Portfolio</h1>
  <div class="tools">
    {#if overview && (overview.brokerConfigured || overview.simulated.length)}
      <div class="seg" role="group" aria-label="Account">
        {#if overview.brokerConfigured}
          <button aria-pressed={view === 'broker'} onclick={() => (view = 'broker')}>Trading 212{broker ? ` · ${broker.environment === 'live' ? 'Live' : 'Demo'}` : ''}</button>
        {/if}
        {#each overview.simulated as s, i}
          <button aria-pressed={view === s.id} onclick={() => (view = s.id)}>Simulated{overview.simulated.length > 1 ? ` ${i + 1}` : ''}</button>
        {/each}
      </div>
    {/if}
    <button class="secondary small-button" disabled={busy} onclick={() => void refresh()}>{busy ? 'Refreshing…' : 'Refresh'}</button>
  </div>
</div>

{#if error}
  <p class="error" role="alert">{error}</p>
{:else if !overview}
  <p class="loading" role="status">Loading portfolio…</p>
{:else if sim}
  <section class="panel" aria-labelledby="sim-title">
    <div class="panel-heading">
      <h2 id="sim-title">Simulated portfolio{sim.lastMonth ? ` · to ${sim.lastMonth}` : ''}</h2>
      <span class="pill sim" data-account-kind="simulated"><span class="d"></span>Simulated money{sim.synthetic ? ' · synthetic data' : ''}</span>
    </div>
    <div class="kvs">
      <div class="kv"><span>Contributed</span><b>{money(sim.contributionsGbp)}</b></div>
      <div class="kv"><span>Cash</span><b>{money(sim.cashGbp)}</b></div>
      <div class="kv"><span>Reserved for open orders</span><b>{money(sim.reservedGbp)}</b></div>
      <div class="kv"><span>Open orders</span><b>{sim.openOrders}</b></div>
    </div>
  </section>
  <section class="panel" aria-labelledby="sim-holdings-title">
    <h2 id="sim-holdings-title">Holdings</h2>
    {#if sim.holdings.length}
      <div class="table-scroll">
        <table class="stack holdings">
          <thead><tr><th>Asset</th><th>Shares</th><th>Booked cost</th><th>Average cost</th></tr></thead>
          <tbody>{#each sim.holdings as h}<tr><td><strong>{h.symbol}</strong></td><td data-label="Shares">{shares(h.quantity)}</td><td data-label="Booked cost">{money(h.costGbp)}</td><td data-label="Average cost">{money(h.averagePriceGbp)}</td></tr>{/each}</tbody>
        </table>
      </div>
      <p class="dataset-note">The simulator records purchase cost only, not market value.</p>
    {:else}
      <div class="empty"><h3>No purchases yet</h3><p>Filled simulated orders appear here. Create them in <a class="text-link" href="#execution">Orders</a>.</p></div>
    {/if}
  </section>
{:else if broker}
  <section class="summary">
    <div class="panel">
      <div class="panel-heading">
        <span class="label">Holdings value</span>
        <span class="pill {broker.environment === 'live' ? 'live' : 'demo'}" data-account-kind={broker.environment}><span class="d"></span>{broker.environment === 'live' ? 'Live' : 'Demo'} · {broker.account}</span>
      </div>
      <div class="big">
        <b>{broker.totalValue != null ? money(broker.totalValue, broker.currency) : 'Unavailable'}</b>
        {#if gain != null}<span class={tone(gain)}>{signed(gain, broker.currency)}{gainPct != null ? ` (${gainPct > 0 ? '+' : gainPct < 0 ? '−' : ''}${Math.abs(gainPct).toFixed(2)}%)` : ''}</span>{/if}
      </div>
      {#if broker.totalValue == null && broker.holdings.length}<p class="dataset-note">Totals need every holding valued in {broker.currency}; some are unavailable.</p>{/if}
      {#if updated}<p class="dataset-note">Updated {updated}</p>{/if}
    </div>
    <div class="kvs two">
      <div class="kv"><span>Cash available</span><b>{money(broker.availableCash, broker.currency)}</b></div>
      <div class="kv"><span>Reserved for orders</span><b>{money(broker.reservedCash, broker.currency)}</b></div>
      <div class="kv"><span>Invested</span><b>{money(broker.totalCost, broker.currency)}</b></div>
      <div class="kv"><span>Holdings</span><b>{broker.holdings.length}</b></div>
    </div>
  </section>
  <section class="panel" aria-labelledby="holdings-title">
    <h2 id="holdings-title">Holdings</h2>
    {#if broker.holdings.length}
      <div class="table-scroll">
        <table class="stack holdings">
          <thead><tr><th>Company</th><th>Shares</th><th>Average price</th><th>Price</th><th>Value</th><th>Gain / loss</th></tr></thead>
          <tbody>
            {#each broker.holdings as h}
              {@const currency = h.valueCurrency ?? broker.currency}
              <tr>
                <td><strong>{h.name ?? h.symbol}</strong><small>{h.symbol}</small>{#if h.managedQuantity > 0}<span class="tag" title="Shares bought through Twenty's own order journal; the rest were bought outside the app.">{shares(h.managedQuantity)} via Twenty</span>{/if}</td>
                <td data-label="Shares">{shares(h.quantity)}</td>
                <td data-label="Average price">{price(h.averagePrice, h.priceCurrency)}</td>
                <td data-label="Price">{price(h.currentPrice, h.priceCurrency)}</td>
                <td data-label="Value">{h.currentValue == null ? '—' : money(h.currentValue, currency)}</td>
                <td data-label="Gain / loss" class={tone(h.unrealizedProfitLoss)}>{signed(h.unrealizedProfitLoss, h.valueCurrency)}</td>
              </tr>
            {/each}
          </tbody>
        </table>
      </div>
      <p class="dataset-note">Prices are in each listing's currency; value and gain in {broker.currency}, including FX.</p>
    {:else}
      <div class="empty"><h3>No holdings</h3><p>This account holds only cash. Purchases appear here once filled.</p></div>
    {/if}
  </section>
{:else if overview.brokerConfigured}
  <div class="error" role="alert">{brokerMessages[overview.brokerError ?? ''] ?? 'Trading 212 could not be read just now (rate limit or network). Refresh in a minute.'}{#if overview.brokerError}<small>{overview.brokerError}</small>{/if}</div>
{:else}
  <section class="panel empty">
    <h3>No broker connected</h3>
    <p>Add read-only Trading 212 credentials to the server's private configuration to see your holdings. <a class="text-link" href="#settings">Settings</a></p>
  </section>
{/if}
