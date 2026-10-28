<script lang="ts">
  import { onMount } from 'svelte';
  let { view }: { view: string } = $props();
  type Point = { year: number; contributions: number; balance: number };
  type Scenario = { assumptions: { monthly: number; years: number; annualReturn: number; target: number }; points: Point[]; requiredAnnualReturn: number | null };
  type Issue = { number: number; title: string; url: string; milestone: string };
  const money = (n: number) => new Intl.NumberFormat('en-GB', { style: 'currency', currency: 'GBP', maximumFractionDigits: 0 }).format(n);
  const phases = ['M1 · Research workbench', 'M2 · Trustworthy data', 'M3 · Evidence and models', 'M4 · Paper portfolio'];
  let monthly = $state(20), years = $state(30), annualPercent = $state(7);
  let result = $state<Scenario | null>(null), busy = $state(false), error = $state('');
  let issues = $state<Issue[]>([]), roadmapError = $state(false), roadmapLoaded = false;
  const final = $derived(result?.points.at(-1));
  const max = $derived(Math.max(1, ...(result?.points.flatMap((p) => [p.balance, p.contributions]) ?? [1])));
  const plot = (key: 'balance' | 'contributions') =>
    result?.points.map((p) => `${48 + (p.year / result!.assumptions.years) * 704},${226 - (p[key] / max) * 196}`).join(' ') ?? '';
  async function calculate() {
    busy = true;
    error = '';
    try {
      const response = await fetch('/api/projections', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-Twenty-Request': '1' },
        body: JSON.stringify({ monthly, years, annualReturn: annualPercent / 100, target: 1000000 }),
      });
      if (!response.ok)
        throw new Error(response.status === 400 ? 'Check the values: monthly £1–£100,000, years 1–60, annual return −99%–100%.' : 'The projection service is unavailable. Try again.');
      result = await response.json();
    } catch (e) {
      error = e instanceof Error ? e.message : 'Could not load the scenario. Try again.';
    } finally {
      busy = false;
    }
  }
  $effect(() => {
    if (view === 'roadmap' && !roadmapLoaded) {
      roadmapLoaded = true;
      fetch('/api/roadmap').then((r) => { if (!r.ok) throw new Error(); return r.json(); }).then((data) => (issues = data)).catch(() => (roadmapError = true));
    }
  });
  onMount(() => { if (view === 'workbench') void calculate(); });
</script>

{#if view === 'workbench'}
  <section class="panel scenario" aria-label="Contribution scenario">
    <form onsubmit={(e) => { e.preventDefault(); void calculate(); }}>
      <label>Monthly contribution <div class="input-wrap"><span>£</span><input aria-label="Monthly contribution in pounds" type="number" min="1" max="100000" step="0.01" bind:value={monthly} required /></div></label>
      <label>Time horizon <div class="input-wrap"><input aria-label="Time horizon in years" type="number" min="1" max="60" step="1" bind:value={years} required /><span>years</span></div></label>
      <label>Annual return <div class="input-wrap"><input aria-label="Assumed annual return in percent" type="number" min="-99" max="100" step="0.1" bind:value={annualPercent} required /><span>%</span></div></label>
      <button disabled={busy} type="submit">{busy ? 'Calculating…' : 'Calculate'}</button>
    </form>
    {#if error}<p class="error" role="alert">{error}</p>{/if}
    {#if result && final}
      <div class="scenario-summary" aria-live="polite">
        <div><span>Value after {result.assumptions.years} years</span><strong>{money(final.balance)}</strong></div>
        <div><span>Contributed</span><strong>{money(final.contributions)}</strong></div>
        <div><span>Assumed annual return</span><strong>{(result.assumptions.annualReturn * 100).toFixed(1)}%</strong></div>
      </div>
      <figure>
        <div class="chart-key"><span><i class="balance-key"></i>Illustrated value</span><span><i class="deposit-key"></i>Contributions</span></div>
        <svg class="projection-chart" viewBox="0 0 800 265" role="img" aria-label={`Illustrated ${money(final.balance)} from ${money(final.contributions)} over ${result.assumptions.years} years`}>
          {#each [0, 0.5, 1] as step}<line x1="48" y1={226 - step * 196} x2="752" y2={226 - step * 196} class="grid-line" /><text x="48" y={217 - step * 196} class="axis-label">{money(max * step)}</text>{/each}
          <polyline points={plot('contributions')} class="deposit-line" />
          <polyline points={plot('balance')} class="balance-line" />
          <circle cx="752" cy={226 - (final.balance / max) * 196} r="5" class="endpoint" />
          <text x="48" y="254" class="axis-label">Today</text><text x="752" y="254" text-anchor="end" class="axis-label">Year {result.assumptions.years}</text>
        </svg>
        <figcaption>Illustration, not a forecast. Constant return, monthly deposits; excludes inflation, fees and tax.</figcaption>
      </figure>
      <details>
        <summary>Yearly figures</summary>
        <div class="table-scroll"><table><thead><tr><th>Year</th><th>Contributed</th><th>Value</th></tr></thead><tbody>{#each result.points as point}<tr><td>{point.year}</td><td>{money(point.contributions)}</td><td>{money(point.balance)}</td></tr>{/each}</tbody></table></div>
      </details>
      <p class="dataset-note">Reaching £1m with {money(result.assumptions.monthly)} a month over {result.assumptions.years} years would need {result.requiredAnnualReturn === null ? 'a return outside the supported range' : `about ${(result.requiredAnnualReturn * 100).toFixed(1)}% a year`}.</p>
    {:else if !error}<p class="loading" role="status">Calculating…</p>{/if}
  </section>
{:else if view === 'roadmap'}
  {#if roadmapError}<p class="error" role="alert">Could not load the roadmap. <a class="text-link" href="https://github.com/Jellman86/twenty/issues">View the issues on GitHub.</a></p>{/if}
  {#each phases as phase}
    <section class="panel">
      <h2>{phase}</h2>
      <ul class="issue-list">
        {#each issues.filter((issue) => issue.milestone === phase) as issue}<li><a href={issue.url}><span class="issue-number">#{issue.number}</span><span>{issue.title}</span></a></li>{/each}
      </ul>
    </section>
  {/each}
  <a class="text-link" href="https://github.com/users/Jellman86/projects/1">Project board ↗</a>
{:else}
  <div class="evidence-grid">
    <article class="panel"><h2>Machine learning in asset pricing</h2><p>Gu, Kelly & Xiu (2020). Transfer to a small long-only ISA, net of costs, is untested.</p><a href="https://doi.org/10.1093/rfs/hhaa009" class="text-link">Paper ↗</a></article>
    <article class="panel"><h2>Equal-weight baseline</h2><p>DeMiguel, Garlappi & Uppal (2009). Same-universe equal weight stays the comparator.</p><a href="https://doi.org/10.1093/rfs/hhm075" class="text-link">Paper ↗</a></article>
    <article class="panel"><h2>Filing changes</h2><p>Cohen, Malloy & Nguyen (2020), “Lazy Prices”. Needs availability timestamps and coverage checks.</p><a href="https://doi.org/10.1111/jofi.12885" class="text-link">Paper ↗</a></article>
    <article class="panel"><h2>Multiple testing</h2><p>Bailey et al. (2017). Every trial is counted, including discarded ones.</p><a href="https://doi.org/10.21314/JCF.2016.322" class="text-link">Paper ↗</a></article>
  </div>
  <a class="text-link" href="https://github.com/Jellman86/twenty/blob/dev/docs/literature-review.md">Full literature review ↗</a>
{/if}
