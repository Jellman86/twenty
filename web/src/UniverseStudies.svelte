<script lang="ts">
  import { onMount } from 'svelte';
  let { onInspect = (_id: string) => {}, onUpdated = () => {} }: { onInspect?: (id: string) => void; onUpdated?: () => void } = $props();
  type Dataset = { datasetSha256: string; datasetId: string; synthetic: boolean; createdAt: string; universeIds: string[] };
  type Row = { id: string; name: string; status: string; synthetic: boolean; completedRuns: number; plannedRuns: number };
  type Study = Row & { planSha256: string; errorCode: string | null; selectedCandidateId: string | null;
    finalRequestedAt: string | null; finalBaseRunId: string | null; finalStressRunId: string | null; finalWorstMarginNotional: number | null;
    plan: { synthetic: boolean; developmentStart: string; developmentEnd: string; reservedStart: string; reservedEnd: string; unitContract: string };
    priorTrials: unknown[]; priorUniverseTrials: unknown[]; priorRawTrials?: unknown[];
    developmentRuns: { candidateId: string; baseRunId: string; stressRunId: string }[];
    scores: { candidateId: string; baseRunId: string; stressRunId: string; baseEndingNotional: number; stressEndingNotional: number; worstMarginNotional: number }[];
    selectionDiagnostics: { months: number; candidates: number; comparisons: number } | null };
  let datasets = $state<Dataset[]>([]), history = $state<Row[]>([]), selected = $state<Study | null>(null);
  let datasetSha = $state(''), universeId = $state(''), name = $state('Changing-universe momentum study');
  let from = $state(''), to = $state(''), monthly = $state(20), busy = $state(false), error = $state('');
  let wanted = '', viewRevision = 0, historyRevision = 0, createSignature = '', createRequestId = '', historySignature = '';
  const finalRequests = new Map<string, string>();
  const dataset = $derived(datasets.find(d => d.datasetSha256 === datasetSha));
  const active = $derived(history.some(s => ['development', 'final-evaluation'].includes(s.status)));
  const amount = (n: number) => new Intl.NumberFormat('en-GB', { maximumFractionDigits: 2 }).format(n);
  const stamp = (value: string) => new Date(value).toISOString().slice(0, 16).replace('T', ' ') + ' UTC';
  const messages: Record<string, string> = {
    'study-reserved-window-conflict': 'Another study reserves these calendar outcomes.',
    'study-known-reserved-exposure': 'Earlier registered work reaches this final window. It cannot be presented as unseen.',
    'study-holdout-spent': 'The final pair is already recorded and cannot be replaced.',
    'study-universe-exposure-unknown': 'An older imported trial has no retained information window. Its exposure cannot be assumed absent.',
    'universe-study-development-window-too-short': 'Choose at least 62 consecutive monthly decisions: warm-up, 24 development intervals, a selection gap and 24 final intervals.',
    'universe-panel-decisions': 'The dates must form a supported monthly grid within the dataset creation cutoff.',
    'study-code-changed': 'The scientific code changed. This declaration cannot silently continue under new code.',
    'study-trial-incomplete': 'A declared trial failed or stopped. The study retains it and stops the family.',
    'study-plan-binding': 'This request does not match the immutable declaration. Reload the original study.',
    'study-holdout-time-unknown': 'A spent final is missing its exact timing evidence. Restore the original study records before declaring more work.',
    'research-worker-busy': 'Another research calculation currently owns the worker.'
  };
  async function api<T>(path: string, body?: unknown): Promise<T> {
    const response = await fetch(path, body === undefined ? undefined : { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Twenty-Request': '1' }, body: JSON.stringify(body) });
    if (!response.ok) { const value = await response.json(); throw new Error(messages[value.error] ?? value.error ?? 'Study request failed.'); }
    return response.json();
  }
  async function refresh() {
    const revision = ++historyRevision, view = viewRevision, id = wanted;
    const rows = await api<Row[]>('/api/research/universe-studies');
    if (revision !== historyRevision) return;
    history = rows;
    const signature = JSON.stringify(rows.map(r => [r.id, r.status, r.completedRuns, r.plannedRuns]));
    if (signature !== historySignature) { historySignature = signature; onUpdated(); }
    if (id) {
      const study = await api<Study>(`/api/research/universe-studies/${id}`);
      if (view === viewRevision && wanted === id) selected = study;
    }
  }
  async function inspect(id: string) {
    const view = ++viewRevision; wanted = id; selected = null; error = '';
    try { const study = await api<Study>(`/api/research/universe-studies/${id}`); if (view === viewRevision) selected = study; }
    catch (e) { if (view === viewRevision) error = e instanceof Error ? e.message : 'Study unavailable.'; }
  }
  function decisions() {
    const first = new Date(`${from}-01T23:00:00Z`), last = new Date(`${to}-01T23:00:00Z`);
    if (!Number.isFinite(+first) || !Number.isFinite(+last) || first > last) throw new Error('Choose a start and end month.');
    const grid: string[] = [];
    for (let at = first; at <= last; at = new Date(Date.UTC(at.getUTCFullYear(), at.getUTCMonth() + 1, 1, 23))) {
      if (grid.length >= 600) throw new Error('Choose at most 600 monthly decisions.');
      grid.push(new Date(Date.UTC(at.getUTCFullYear(), at.getUTCMonth() + 1, 0, 23)).toISOString());
    }
    return grid;
  }
  async function register() {
    if (!dataset) return; busy = true; error = ''; const view = ++viewRevision;
    try {
      const payload = { name, purpose: dataset.synthetic ? 'engineering' : 'research', datasetSha256: datasetSha, universeId,
        decisions: decisions(), maximumPriceAgeDays: 45, monthlyGbp: monthly,
        costs: { fxBps: 15, halfSpreadBps: 5, slippageBps: 5, buyTaxBps: 0, minimumOrderGbp: 1 } };
      const signature = JSON.stringify(payload);
      if (!createRequestId || signature !== createSignature) { createRequestId = crypto.randomUUID(); createSignature = signature; }
      const study = await api<Study>('/api/research/universe-studies', { requestId: createRequestId, ...payload });
      if (view === viewRevision) { wanted = study.id; selected = study; }
      await refresh();
    } catch (e) { if (view === viewRevision) error = e instanceof Error ? e.message : 'Study request failed.'; onUpdated(); }
    finally { busy = false; }
  }
  async function evaluate() {
    if (!selected) return; const study = selected, view = viewRevision; busy = true; error = '';
    if (!finalRequests.has(study.id)) finalRequests.set(study.id, crypto.randomUUID());
    try {
      const result = await api<Study>(`/api/research/universe-studies/${study.id}/evaluate`, { requestId: finalRequests.get(study.id), planSha256: study.planSha256 });
      if (view === viewRevision) selected = result; await refresh();
    } catch (e) { if (view === viewRevision) error = e instanceof Error ? e.message : 'Final request failed.'; }
    finally { busy = false; }
  }
  async function cancel() {
    if (!selected) return; const study = selected, view = viewRevision; busy = true; error = '';
    try { await api(`/api/research/universe-studies/${study.id}/cancel`, {}); await refresh(); }
    catch (e) { if (view === viewRevision) error = e instanceof Error ? e.message : 'Stop request failed.'; }
    finally { busy = false; }
  }
  $effect(() => { if (!datasetSha && datasets.length) datasetSha = datasets[0].datasetSha256; });
  $effect(() => { if (dataset && !dataset.universeIds.includes(universeId)) universeId = dataset.universeIds[0] ?? ''; });
  onMount(() => {
    let stopped = false;
    void Promise.all([api<Dataset[]>('/api/research/universe-datasets').then(rows => { if (!stopped) datasets = rows; }), refresh()]).catch(e => { if (!stopped) error = e.message; });
    const timer = setInterval(() => { if (active && !stopped && !busy) void refresh().catch(e => { if (!stopped) error = e.message; }); }, 2000);
    return () => { stopped = true; viewRevision++; historyRevision++; clearInterval(timer); };
  });
</script>
<section class="panel lab-results" aria-labelledby="universe-study-title">
  <div class="panel-heading"><div><p class="eyebrow">REGISTER A FINITE UNIVERSE STUDY</p><h2 id="universe-study-title">Reserve the final window before comparing candidates</h2></div></div>
  <p>Compare the existing momentum baseline at 1, 2, 4 and 8 target holdings under base and doubled variable costs. All eight development trials are recorded before work starts. Each window starts empty and rebalances fractional notional exposure monthly. One full monthly selection gap separates development from the reserved 24 final intervals.</p>
  <p class="dataset-note">Local-currency adjusted returns use notional exposure units. They are not achievable GBP wealth, raw share execution or live-trading approval. Base costs: 15 bps FX, 5 bps half-spread and 5 bps slippage per side, zero buy tax, £1 notional minimum. These are unverified flat research assumptions.</p>
  {#if datasets.length}
    <form class="signal-form" onsubmit={e => { e.preventDefault(); void register(); }}>
      <label>Captured dataset<select bind:value={datasetSha} disabled={busy}>{#each datasets as d}<option value={d.datasetSha256}>{d.datasetId} · {d.synthetic ? 'Authored fixture' : 'Imported; coverage unverified'}</option>{/each}</select></label>
      <label>Universe<select bind:value={universeId} disabled={busy}>{#each dataset?.universeIds ?? [] as id}<option value={id}>{id}</option>{/each}</select></label>
      <label>Study name<input bind:value={name} maxlength="80" required disabled={busy} /></label>
      <label>First month<input type="month" bind:value={from} required disabled={busy} /></label>
      <label>Last month<input type="month" bind:value={to} required disabled={busy} /></label>
      <label>Monthly notional contribution<input type="number" bind:value={monthly} min="1" max="1000" required disabled={busy} /></label>
      <button disabled={busy || !universeId}>{busy ? 'Recording…' : 'Declare and run all eight development trials'}</button>
    </form>
    <p class="dataset-note">Month-end decisions use 23:00 UTC and a maximum 45-day price age. At least 62 consecutive months are required. Prices and eligibility must actually have been available by each decision. The dataset cutoff is {dataset ? stamp(dataset.createdAt) : 'unknown'}. {dataset?.synthetic ? 'Engineering rehearsal only; authored final outcomes are already inspectable.' : 'Imported data still require independent coverage and availability acceptance.'}</p>
  {:else}<p class="dataset-note">No changing-universe dataset has been imported. Captured evidence must be converted and validated before it can support a declaration.</p>{/if}
  {#if error}<p class="error" role="alert">{error}</p>{/if}
  {#if history.length}<div class="table-scroll"><table><thead><tr><th>Study</th><th>Status</th><th>Completed / declared</th><th>Inspect</th></tr></thead><tbody>{#each history as row}<tr><td>{row.name}<small>{row.synthetic ? 'Engineering rehearsal' : 'Imported research'}</small></td><td>{row.status}</td><td>{row.completedRuns} / {row.plannedRuns}</td><td><button class="secondary small-button" disabled={busy} onclick={() => void inspect(row.id)} aria-label={`Open universe study ${row.id.slice(0, 8)}`}>Open</button></td></tr>{/each}</tbody></table></div>{/if}
  {#if selected}
    <h3>{selected.name} · {selected.status}</h3>
    <p class="dataset-note">Development {stamp(selected.plan.developmentStart)}–{stamp(selected.plan.developmentEnd)}; reserved final outcomes {stamp(selected.plan.reservedStart)}–{stamp(selected.plan.reservedEnd)}. Each window starts with an empty account. Earlier local disclosure: {selected.priorTrials.length} model trials and {selected.priorUniverseTrials.length} universe trials{#if selected.priorRawTrials?.length} and {selected.priorRawTrials.length} raw-execution trials{/if}.</p>
    <p>{selected.plan.unitContract}</p>
    {#if selected.errorCode}<p class="error" role="status">{messages[selected.errorCode] ?? selected.errorCode}</p>{/if}
    {#if selected.selectedCandidateId}<p><strong>Frozen choice: {selected.selectedCandidateId}.</strong> The rule maximises the smaller base/stress ending-notional margin over equal weight and cash; ordinal candidate ID breaks ties.</p>{/if}
    {#if selected.status === 'candidate-frozen'}<p>Requesting the final base/stress pair permanently spends this study's window before either trial starts. A failure cannot create a replacement or substitute another candidate. This is a fresh account, not a continuation of development holdings.</p><button disabled={busy} onclick={() => void evaluate()}>{selected.plan.synthetic ? 'Spend the final engineering rehearsal pair' : 'Consume final window and evaluate the frozen choice'}</button>{/if}
    {#if ['development', 'candidate-frozen', 'final-evaluation'].includes(selected.status)}<button class="secondary" disabled={busy} onclick={() => void cancel()}>Stop this universe study</button>{/if}
    {#if selected.finalRequestedAt}<p class="dataset-note">Final pair requested {stamp(selected.finalRequestedAt)}. The claim remains spent, including on failure, restart or cancellation.</p>{/if}
    {#if selected.status === 'engineering-complete'}<p><strong>Engineering rehearsal complete.</strong> This cannot nominate a real investment strategy.</p>{:else if selected.status === 'no-candidate'}<p>No development candidate cleared the notional screen. The final window remains unspent.</p>{:else if ['research-complete', 'research-rejected'].includes(selected.status)}<p>The frozen choice {selected.status === 'research-complete' ? 'passed' : 'failed'} the notional research screen. No replacement is allowed. Raw execution, GBP/FX, terminal-data and prospective evidence remain separate gates.</p>{/if}
    {#if selected.finalWorstMarginNotional !== null}<p>Final worst base/stress notional margin: <strong>{amount(selected.finalWorstMarginNotional)}</strong>. This is not a significance level or an investable GBP return.</p>{/if}
    {#if selected.finalBaseRunId && selected.finalStressRunId}<button class="secondary small-button" onclick={() => onInspect(selected!.finalBaseRunId!)}>Inspect final base report</button><button class="secondary small-button" onclick={() => onInspect(selected!.finalStressRunId!)}>Inspect final stress report</button>{/if}
    {#if selected.scores.length}<div class="table-scroll"><table><caption>Complete declared family; ending exposure units under both cost settings</caption><thead><tr><th>Candidate</th><th>Base</th><th>Stress</th><th>Worst margin</th><th>Reports</th></tr></thead><tbody>{#each selected.scores as score}<tr><td>{score.candidateId}{score.candidateId === selected.selectedCandidateId ? ' · frozen' : ''}</td><td>{amount(score.baseEndingNotional)}</td><td>{amount(score.stressEndingNotional)}</td><td>{amount(score.worstMarginNotional)}</td><td><button class="secondary small-button" onclick={() => onInspect(score.baseRunId)}>Base</button><button class="secondary small-button" onclick={() => onInspect(score.stressRunId)}>Stress</button></td></tr>{/each}</tbody></table></div>{/if}
    <details><summary>All declared trial pairs, including failures</summary><ul>{#each selected.developmentRuns as pair}<li>{pair.candidateId}: <button class="secondary small-button" onclick={() => onInspect(pair.baseRunId)}>Inspect development base for {pair.candidateId}</button> <button class="secondary small-button" onclick={() => onInspect(pair.stressRunId)}>Inspect development stress for {pair.candidateId}</button></li>{/each}</ul></details>
    {#if selected.selectionDiagnostics}<p class="dataset-note">Paired family diagnostic: {selected.selectionDiagnostics.candidates} base/stress streams, {selected.selectionDiagnostics.months} common months, {selected.selectionDiagnostics.comparisons} comparisons. Conditional resampling does not correct all earlier programme searches.</p>{/if}
    <a class="text-link" href={`/api/research/universe-studies/${selected.id}/plan`} target="_blank" rel="noreferrer">Exact immutable plan ↗</a>
    <a class="text-link" href={`/api/research/universe-studies/${selected.id}`} target="_blank" rel="noreferrer">Full declaration, trial hashes and diagnostics ↗</a>
    <a class="text-link" href={`/api/research/universe-studies/${selected.id}/events`} target="_blank" rel="noreferrer">Study events ↗</a>
  {/if}
</section>

<style>
  form label { min-width: 0; }
  input, select { display: block; width: 100%; min-width: 0; height: 42px; border: 1px solid #cbd7e1; border-radius: 5px; padding: 0 10px; background: white; font: inherit; color: var(--navy); margin-top: 7px; }
</style>
