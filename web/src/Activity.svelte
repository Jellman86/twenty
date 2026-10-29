<script lang="ts">
  import { onMount } from 'svelte';
  import { brokerCompany, loadBrokerNames, openOrderStatuses, orderDisplayStatus, orderStatusLabels } from './broker-display';
  import type { BrokerAccountRef, BrokerNames } from './broker-display';
  // Read-only journals, with optional company names from the current portfolio.
  type Progress = { filled: number; debitGbp: number; remainingReserve: number; status: string };
  type BrokerIntent = { id: string; batchId: string; phase: string; submittedAt?: string | null; errorCode: string | null; intent: { symbol: string; brokerTicker?: string; quantity: number; limitPrice: number; quoteCurrency: string }; progress: Progress };
  type BrokerAccount = { key: string; environment: string; account: string; batches: { id: string; month: string; createdAt: string }[]; intents: BrokerIntent[] };
  type PaperAccount = { id: string; runId: string; orders: { id: string; period: string; intent: { symbol: string; quantity: number; limitPrice: number }; progress: Progress }[] };
  type Row = { id: string; when: string; sortKey: string; symbol: string; brokerTicker?: string; brokerAccount?: BrokerAccountRef; account: string; kind: 'live' | 'demo' | 'sim'; ordered: number; filled: number; amount: number; status: string; detail: string | null };
  let rows = $state<Row[]>([]);
  let loading = $state(true);
  let errors = $state<string[]>([]);
  let filter = $state<'all' | 'filled' | 'open'>('all');
  let names = $state<BrokerNames | null>(null);
  let nameRequest: AbortController | undefined;
  const money = (n: number) => new Intl.NumberFormat('en-GB', { style: 'currency', currency: 'GBP' }).format(n);
  const shares = (n: number) => n.toLocaleString('en-GB', { maximumFractionDigits: 6 });
  const day = (iso: string) => new Date(iso).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' });
  const shown = $derived(rows.filter((r) => filter === 'all' || (filter === 'filled' ? r.filled > 0 : openOrderStatuses.has(r.status))));
  async function load() {
    loading = true;
    nameRequest?.abort();
    const request = nameRequest = new AbortController();
    names = null;
    const failed: string[] = [];
    const next: Row[] = [];
    const [broker, paper] = await Promise.allSettled([
      fetch('/api/execution/broker').then((r) => { if (!r.ok) throw new Error(); return r.json() as Promise<{ accounts: BrokerAccount[] }>; }),
      fetch('/api/execution/paper').then((r) => { if (!r.ok) throw new Error(); return r.json() as Promise<{ accounts: PaperAccount[] }>; }),
    ]);
    if (broker.status === 'fulfilled') {
      for (const a of broker.value.accounts ?? []) {
        const batches = new Map(a.batches.map((b) => [b.id, b]));
        for (const i of a.intents) {
          const batch = batches.get(i.batchId);
          const at = i.submittedAt ?? batch?.createdAt ?? '';
          next.push({ id: i.id, when: at ? day(at) : batch?.month ?? '—', sortKey: at || batch?.month || '', symbol: i.intent.symbol, brokerTicker: i.intent.brokerTicker,
            brokerAccount: { environment: a.environment, account: a.account },
            account: `${a.environment === 'live' ? 'Live' : 'Demo'} ${a.account}`, kind: a.environment === 'live' ? 'live' : 'demo',
            ordered: i.intent.quantity, filled: i.progress.filled, amount: i.progress.debitGbp, status: orderDisplayStatus(i), detail: i.errorCode });
        }
      }
    } else failed.push('broker orders');
    if (paper.status === 'fulfilled') {
      for (const a of paper.value.accounts ?? []) {
        for (const o of a.orders) {
          next.push({ id: o.id, when: o.period, sortKey: o.period, symbol: o.intent.symbol, account: 'Simulated', kind: 'sim',
            ordered: o.intent.quantity, filled: o.progress.filled, amount: o.progress.debitGbp, status: o.progress.status, detail: null });
        }
      }
    } else failed.push('simulated orders');
    rows = next.sort((a, b) => b.sortKey.localeCompare(a.sortKey));
    errors = failed;
    loading = false;
    if (next.some((r) => r.brokerAccount) && !request.signal.aborted) {
      void loadBrokerNames(request.signal).then((result) => {
        if (request === nameRequest && !request.signal.aborted) names = result;
      });
    }
  }
  onMount(() => { void load(); return () => nameRequest?.abort(); });
</script>

<div class="page-head">
  <div class="title"><p class="eyebrow">Trading</p><h1>Activity</h1><p class="lede">Every order the bot has placed, and how much of each was actually bought.</p></div>
  <div class="tools">
    <div class="seg" role="group" aria-label="Filter">
      <button aria-pressed={filter === 'all'} onclick={() => (filter = 'all')}>All</button>
      <button aria-pressed={filter === 'filled'} onclick={() => (filter = 'filled')}>Bought</button>
      <button aria-pressed={filter === 'open'} onclick={() => (filter = 'open')}>Open</button>
    </div>
    <button class="secondary small-button" disabled={loading} onclick={() => void load()}>{loading ? 'Refreshing…' : 'Refresh'}</button>
  </div>
</div>
{#if errors.length}<p class="error" role="alert">Could not load {errors.join(' or ')}. Refresh to try again.</p>{/if}
<section class="panel" aria-label="Orders and fills">
  {#if loading && !rows.length}
    <p class="loading" role="status">Loading activity…</p>
  {:else if !shown.length}
    <div class="empty"><h3>{rows.length ? 'Nothing matches this filter' : 'No orders yet'}</h3><p>{rows.length ? 'Choose All to see every order.' : 'Orders placed through Twenty appear here with their fills.'}</p></div>
  {:else}
    <div class="table-scroll">
      <table class="stack">
        <thead><tr><th>Stock</th><th>Date</th><th>Account</th><th>Shares</th><th>Filled</th><th>Paid</th><th>Status</th></tr></thead>
        <tbody>
          {#each shown as r (r.kind + r.id)}
            {@const company = brokerCompany(names, r.brokerAccount, r)}
            <tr>
              <td><strong>{company ?? r.symbol}</strong>{#if company}<small>{r.symbol}</small>{/if}</td>
              <td data-label="Date">{r.when}</td>
              <td data-label="Account"><span class="pill {r.kind}"><span class="d"></span>{r.account}</span></td>
              <td data-label="Shares">{shares(r.ordered)}</td>
              <td data-label="Filled">{shares(r.filled)}</td>
              <td data-label="Paid">{r.filled > 0 ? money(r.amount) : '—'}</td>
              <td data-label="Status" class:loss={['rejected', 'unknown'].includes(r.status)}>{orderStatusLabels[r.status] ?? r.status}{#if r.detail}<small>{r.detail}</small>{/if}</td>
            </tr>
          {/each}
        </tbody>
      </table>
    </div>
    <p class="dataset-note">Paid is the booked GBP debit. Manage open orders in <a class="text-link" href="#execution">Orders</a>.</p>
  {/if}
</section>
