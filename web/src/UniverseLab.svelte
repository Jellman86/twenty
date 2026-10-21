<script lang="ts">
  import { onMount } from 'svelte';
  import SearchDiagnostics from './SearchDiagnostics.svelte';
  import UniverseStudies from './UniverseStudies.svelte';

  type Trial = {
    id: string; datasetId: string; synthetic: boolean; universeId: string; status: string;
    createdAt: string; errorCode: string | null; codeCommit: string;
    informationStart: string | null; informationEnd: string | null;
    requestArtifactSha256: string | null;
    studyStage?: string;
  };
  type Summary = {
    strategy: string; endingEquityGbp: number; contributionsGbp: number;
    tradingCostGbp: number; maxDrawdown: number; annualisedTimeWeightedReturn: number;
  };
  type Report = {
    engineVersion: string; evaluationStart: string; evaluationEnd: string;
    summaries: Summary[]; limitations: string[];
    equity: { periodEnd: string; strategy: string; state: {
      stockTransfers?: { listingId: string; successorListingId: string; notionalGbp: number }[];
    } }[];
  };
  let trials = $state<Trial[]>([]);
  let selected = $state<Trial | null>(null);
  let report = $state<Report | null>(null);
  let loading = $state(false), inspecting = $state(false), error = $state(''), totalTrials = $state(0);
  let inspectionRevision = 0;
  const labels: Record<string, string> = { equal_weight: 'Equal weight', momentum: 'Momentum', cash: 'Cash' };
  const amount = (value: number) => new Intl.NumberFormat('en-GB', { maximumFractionDigits: 2 }).format(value);
  const percent = (value: number) => `${(value * 100).toFixed(1)}%`;
  const transfers = $derived(report?.equity.flatMap(e => (e.state.stockTransfers ?? []).map(t =>
    ({ ...t, periodEnd: e.periodEnd, strategy: e.strategy }))) ?? []);

  async function read<T>(path: string): Promise<T> {
    const response = await fetch(path);
    if (!response.ok) throw new Error(`Could not load the research record (${response.status}).`);
    return response.json();
  }
  async function refresh() {
    loading = true; error = '';
    try {
      const [rows, counts] = await Promise.all([read<Trial[]>('/api/research/universe-runs'),
        read<{ universeTrials?: number }>('/api/research/trials')]);
      trials = rows; totalTrials = counts.universeTrials ?? rows.length;
      if (selected) {
        selected = rows.find(t => t.id === selected!.id) ?? selected;
        if (selected.status === 'succeeded' && !report) await inspect(selected);
      }
    }
    catch (e) { error = e instanceof Error ? e.message : 'Trial history is unavailable.'; }
    finally { loading = false; }
  }
  async function inspect(trial: Trial) {
    const revision = ++inspectionRevision;
    selected = trial; report = null; error = ''; inspecting = trial.status === 'succeeded';
    if (!inspecting) return;
    try {
      const result = await read<Report>(`/api/research/universe-runs/${trial.id}/report`);
      if (revision === inspectionRevision && selected?.id === trial.id) report = result;
    } catch (e) {
      if (revision === inspectionRevision && selected?.id === trial.id) error = e instanceof Error ? e.message : 'Report is unavailable.';
    } finally { if (revision === inspectionRevision && selected?.id === trial.id) inspecting = false; }
  }
  async function openStudyReport(id: string) {
    const revision = ++inspectionRevision; selected = null; report = null; error = '';
    try { const trial = await read<Trial>(`/api/research/universe-runs/${id}`); if (revision === inspectionRevision) await inspect(trial); }
    catch (e) { if (revision === inspectionRevision) error = e instanceof Error ? e.message : 'Trial is unavailable.'; }
  }
  onMount(() => { void refresh(); });
</script>

<UniverseStudies onInspect={id => void openStudyReport(id)} onUpdated={() => void refresh()} />

<section class="panel compact evidence-intro" aria-labelledby="universe-title">
  <div class="panel-heading">
    <div><p class="eyebrow">CHANGING-UNIVERSE RESEARCH</p><h2 id="universe-title">Entries, exits and successor holdings</h2></div>
    <button class="secondary small-button" disabled={loading} onclick={() => void refresh()}>{loading ? 'Loading…' : 'Refresh history'}</button>
  </div>
  <p>Inspect registered equal-weight, momentum and cash tests over listings that enter or leave the sample. Failed attempts remain part of the research record.</p>
  {#if error}<p class="error" role="alert">{error}</p>{/if}
  {#if !loading && !trials.length && !error}
    <p class="dataset-note">No changing-universe trials have been registered. Historical coverage and capture availability must support the requested dates before a credible test can run.</p>
  {:else if trials.length}
    <p class="dataset-note">Showing {trials.length} of {totalTrials} registered attempts (latest 100). Opening a record reads existing evidence; it does not start another test.</p>
    <div class="table-scroll"><table>
      <thead><tr><th>Dataset / universe</th><th>Inputs</th><th>Status</th><th>Inspect</th></tr></thead>
      <tbody>{#each trials as trial (trial.id)}
        <tr class:selected-run={selected?.id === trial.id}>
          <td><strong>{trial.datasetId}</strong><small>{trial.universeId} · {new Date(trial.createdAt).toLocaleString('en-GB')}</small></td>
          <td>{trial.synthetic ? 'Synthetic fixture' : 'Imported; coverage unverified'}</td>
          <td><span class="run-status" data-status={trial.status}>{trial.status}</span></td>
          <td><button class="secondary small-button" onclick={() => void inspect(trial)} aria-label={`Inspect universe trial ${trial.id.slice(0, 8)}`}>Open</button></td>
        </tr>
      {/each}</tbody>
    </table></div>
  {/if}
  {#if selected}
    <div class="running-note">
      <p><strong>{selected.datasetId}</strong> · {selected.synthetic ? 'Synthetic engineering inputs' : 'Imported inputs; historical coverage is not certified'}</p>
      <p class="dataset-note">Information window: {selected.informationStart ?? 'Not retained'}–{selected.informationEnd ?? 'Not retained'}. This includes warm-up and the declared price-age lookback. Code: {selected.codeCommit.slice(0, 12)}.</p>
      {#if selected.requestArtifactSha256}
        <a class="text-link" href={`/api/research/universe-runs/${selected.id}/request`} target="_blank" rel="noreferrer">Open retained request ↗</a>
      {:else}<p class="dataset-note">This older trial has no retained request artifact.</p>{/if}
      {#if selected.errorCode}<p class="error" role="status">Test refused or failed: {selected.errorCode}. Its attempt remains recorded.</p>{/if}
      {#if inspecting}<p aria-live="polite">Loading the stored report…</p>{/if}
    </div>
  {/if}
  {#if report}
    <p class="dataset-note">Evaluated {report.evaluationStart}–{report.evaluationEnd} · {report.engineVersion}. Values use GBP-labelled notional accounting with local-currency returns and no FX conversion; they are not achievable GBP wealth. No external market benchmark is supplied.</p>
    <div class="table-scroll"><table>
      <caption>Simulated exposure units; identical contributions across strategies</caption>
      <thead><tr><th>Strategy</th><th>Ending value</th><th>Contributed</th><th>Costs</th><th>Annualised return</th><th>Max drawdown</th></tr></thead>
      <tbody>{#each report.summaries as summary}
        <tr><td>{labels[summary.strategy] ?? summary.strategy}</td><td>{amount(summary.endingEquityGbp)}</td><td>{amount(summary.contributionsGbp)}</td><td>{amount(summary.tradingCostGbp)}</td><td>{percent(summary.annualisedTimeWeightedReturn)}</td><td>{percent(summary.maxDrawdown)}</td></tr>
      {/each}</tbody>
    </table></div>
    {#if transfers.length}
      <details><summary>Successor exposure transfers ({transfers.length})</summary>
        <p class="dataset-note">Stock consideration remains invested in the successor. These transfers are not sales; subsequent rebalance trades pay normal costs.</p>
        <div class="table-scroll"><table>
          <thead><tr><th>Period end</th><th>Strategy</th><th>Original</th><th>Successor</th><th>Exposure value</th></tr></thead>
          <tbody>{#each transfers as transfer}<tr><td>{transfer.periodEnd}</td><td>{labels[transfer.strategy] ?? transfer.strategy}</td><td>{transfer.listingId}</td><td>{transfer.successorListingId}</td><td>{amount(transfer.notionalGbp)}</td></tr>{/each}</tbody>
        </table></div>
      </details>
    {/if}
    <details><summary>Report limitations</summary><ul class="limitations">{#each report.limitations as limitation}<li>{limitation}</li>{/each}</ul></details>
  {/if}
</section>
{#if report && selected?.status === 'succeeded' && !selected.studyStage?.startsWith('final-')}
  <SearchDiagnostics anchorRunId={selected.id} family="universe" />
{/if}
