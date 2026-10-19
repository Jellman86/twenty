<script lang="ts">
  import { onMount } from 'svelte';
  let { anchorRunId } = $props<{anchorRunId:string}>();
  type Interval={runId:string;comparator:string;annualisedMeanActiveReturn:number;pointwiseLower:number;pointwiseUpper:number;simultaneousLower:number;simultaneousUpper:number};
  type Sensitivity={expectedBlockMonths:number;expectedBlocksPerPath:number;simultaneousRadius:number;intervals:Interval[]};
  type Pbo={blocks:number;combinations:number;probabilityOfBacktestOverfitting:number;medianLogit:number;medianInSampleWinnerMonthlySharpe:number;medianOutOfSampleWinnerMonthlySharpe:number;fractionWinnersNegativeOutOfSample:number};
  type Deflated={winnerRunId:string;monthlySharpe:number;annualisedSharpe:number;skewness:number;kurtosis:number;trialSharpeStandardDeviation:number;averageCorrelation:number|null;probabilisticSharpeVsZero:number;sensitivities:{basis:string;trials:number;expectedMaximumMonthlySharpe:number;deflatedSharpeRatio:number}[]};
  type Selection={statistic:string;pbo:Pbo|null;deflatedSharpe:Deflated|null;limitations:string[]};
  type Attempt={id:string;anchorRunId:string;status:string;createdAt:string;finishedAt:string|null;errorCode:string|null;synthetic:boolean|null;registrySnapshotAt:string|null;trials:{runId:string;included:boolean;reason:string}[];report:{months:number;candidates:number;comparisons:number;replicates:number;firstPeriodEnd:string;lastPeriodEnd:string;sensitivities:Sensitivity[];limitations:string[];selection?:Selection|null}|null};
  type History={total:number;attempts:{id:string;anchorRunId:string;status:string;createdAt:string;registered:number;included:number;errorCode:string|null}[]};
  let attempt=$state<Attempt|null>(null),history=$state<History>({total:0,attempts:[]}),busy=$state(false),error=$state('');
  let requestId='';
  const labels:Record<string,string>={benchmark:'Dataset benchmark',momentum:'Momentum',equal_weight:'All-asset equal targets',cash:'Zero-interest cash'};
  const messages:Record<string,string>={'diagnostic-busy':'Another diagnostic is running. Try again when it finishes.','diagnostic-common-window-too-short':'These candidates share fewer than 24 development months. More common history is needed.','diagnostic-candidate-count':'The compatible family exceeds the bounded 64-candidate analysis. It has not been silently trimmed.','diagnostic-report-contract':'A stored report does not match its recorded contract. Inspect its integrity before analysing it.'};
  const percent=(n:number)=>`${(n*100).toFixed(2)} pp`;
  const prob=(n:number)=>`${(n*100).toFixed(1)}%`;
  const ann=(n:number)=>(n*Math.sqrt(12)).toFixed(2);
  const basisLabels:Record<string,string>={'family-as-independent':'Family trials, as independent','family-correlation-adjusted':'Correlation-adjusted trials','registered-snapshot-as-independent':'All registered trials, as independent'};
  const selection=$derived(attempt?.report?.selection??null);
  const range=(x:Interval)=>`${percent(x.simultaneousLower)} to ${percent(x.simultaneousUpper)}`;
  const rows=$derived(attempt?.report?.sensitivities[0]?.intervals??[]);
  const exclusions=$derived(Object.entries((attempt?.trials??[]).filter(t=>!t.included).reduce<Record<string,number>>((all,t)=>{all[t.reason]=(all[t.reason]??0)+1;return all;},{})));
  async function api(path:string,body?:unknown){const response=await fetch(path,body===undefined?undefined:{method:'POST',headers:{'Content-Type':'application/json','X-Twenty-Request':'1'},body:JSON.stringify(body)});if(!response.ok){const result=await response.json();throw new Error(messages[result.error]??result.error??'diagnostic-unavailable');}return response.json();}
  async function refresh(){history=await api('/api/research/diagnostics');}
  async function inspect(id:string){error='';try{attempt=await api(`/api/research/diagnostics/${id}`);}catch(e){error=e instanceof Error?e.message:'diagnostic-unavailable';}}
  async function analyse(){busy=true;error='';const anchor=anchorRunId;requestId||=crypto.randomUUID();try{const result:Attempt=await api('/api/research/diagnostics',{requestId,anchorRunId:anchor});requestId='';if(anchor===anchorRunId)attempt=result;await refresh();}catch(e){error=e instanceof Error?e.message:'diagnostic-unavailable';}finally{busy=false;}}
  $effect(()=>{anchorRunId;attempt=null;error='';requestId='';});
  onMount(()=>{
    void refresh().catch(e=>error=e.message);
    const timer=setInterval(()=>{const id=attempt?.id;if(id&&attempt?.status==='running')void api(`/api/research/diagnostics/${id}`).then((result:Attempt)=>{if(attempt?.id===id)attempt=result;if(result.status!=='running')void refresh();}).catch(e=>error=e.message);},1500);
    return()=>clearInterval(timer);
  });
</script>
<section class="panel lab-results" aria-labelledby="diagnostic-title">
  <div class="panel-heading"><div><p class="eyebrow">UNCERTAINTY ACROSS RECORDED CANDIDATES</p><h2 id="diagnostic-title">How much of the lead could be noise?</h2></div><span class="badge">RETROSPECTIVE DIAGNOSTIC</span></div>
  <p>Compare net monthly returns with the benchmark, momentum, equal targets and cash. Whole months are resampled together across the compatible candidate family. This preserves shared market movements and short runs of returns; it cannot repair biased data or establish a durable edge.</p>
  <button class="secondary" disabled={busy||attempt?.status==='running'} onclick={()=>void analyse()}>{busy||attempt?.status==='running'?'Analysing recorded returns…':'Analyse the recorded candidate family'}</button>
  <p class="dataset-note">The family includes every completed local trial matching this run's dataset, engine, allocation policy, contribution, costs and reserved-window length. It uses their common calendar window. Other attempts remain disclosed; these bands do not correct the entire research programme or approve a stock purchase.</p>
  {#if error}<p class="error" role="alert">{error}</p>{/if}
  {#if attempt?.errorCode}<p class="error" role="alert">Analysis {attempt.status}: {messages[attempt.errorCode]??attempt.errorCode}. This attempt remains recorded.</p>{/if}
  {#if attempt?.report}
    <p class="dataset-note">{attempt.synthetic?'Synthetic returns: this checks the software and establishes no investment evidence.':'Imported returns: provenance, survivorship and timing still require an independent audit.'} Register snapshot: {attempt.registrySnapshotAt?new Date(attempt.registrySnapshotAt).toLocaleString('en-GB'):'unknown'}.</p>
    <div class="lab-metrics"><div><span>Local trials in snapshot</span><strong>{attempt.trials.length}</strong></div><div><span>Compatible candidates</span><strong>{attempt.report.candidates}</strong></div><div><span>Common development months</span><strong>{attempt.report.months}</strong></div><div><span>Comparisons in each joint band</span><strong>{attempt.report.comparisons}</strong></div></div>
    <p class="dataset-note">{attempt.report.firstPeriodEnd}–{attempt.report.lastPeriodEnd} · {attempt.report.replicates} resamples for each expected block length. Values are annualised arithmetic mean differences in percentage points, not compound growth. {attempt.report.months<60?'The common history is short; uncertainty estimates may be unstable.':''}</p>
    <div class="table-scroll"><table><caption>Approximate 95% simultaneous bands within each block setting, over all included candidates and comparators. The three settings are separate sensitivities.</caption><thead><tr><th>Recorded run</th><th>Compared with</th><th>Mean lead</th>{#each attempt.report.sensitivities as s}<th>{s.expectedBlockMonths}-month blocks</th>{/each}</tr></thead><tbody>{#each rows as row,i}<tr><td>{row.runId.slice(0,8)}</td><td>{labels[row.comparator]}</td><td>{percent(row.annualisedMeanActiveReturn)}</td>{#each attempt.report.sensitivities as s}<td>{range(s.intervals[i])}</td>{/each}</tr>{/each}</tbody></table></div>
    {#if selection}
    <h3>Selection-bias estimates</h3>
    <p class="dataset-note">Combinatorially symmetric cross-validation asks whether picking the best of this family in-sample tends to beat the family median out-of-sample; the deflated Sharpe asks whether the family's Sharpe winner clears the maximum expected from luck across declared trial counts. Both are scoped to this recorded family.</p>
    <div class="lab-metrics"><div><span>Probability of backtest overfitting</span><strong>{selection.pbo?prob(selection.pbo.probabilityOfBacktestOverfitting):'Unavailable'}</strong></div><div><span>Winners losing out-of-sample</span><strong>{selection.pbo?prob(selection.pbo.fractionWinnersNegativeOutOfSample):'Unavailable'}</strong></div><div><span>Sharpe winner (annualised)</span><strong>{selection.deflatedSharpe?`${selection.deflatedSharpe.winnerRunId.slice(0,8)} · ${selection.deflatedSharpe.annualisedSharpe.toFixed(2)}`:'Unavailable'}</strong></div><div><span>Deflated Sharpe (most conservative count)</span><strong>{selection.deflatedSharpe?prob(selection.deflatedSharpe.sensitivities.at(-1)?.deflatedSharpeRatio??0):'Unavailable'}</strong></div></div>
    {#if selection.pbo}<p class="dataset-note">{selection.pbo.combinations.toLocaleString('en-GB')} symmetric splits of {selection.pbo.blocks} blocks. Median in-sample winner Sharpe {ann(selection.pbo.medianInSampleWinnerMonthlySharpe)} annualised; median out-of-sample {ann(selection.pbo.medianOutOfSampleWinnerMonthlySharpe)}; median logit {selection.pbo.medianLogit.toFixed(2)}.</p>{/if}
    {#if selection.deflatedSharpe}
    <div class="table-scroll"><table><caption>Deflated Sharpe of run {selection.deflatedSharpe.winnerRunId.slice(0,8)} under declared trial-count assumptions. PSR vs zero (no selection correction): {prob(selection.deflatedSharpe.probabilisticSharpeVsZero)}. Skewness {selection.deflatedSharpe.skewness.toFixed(2)}, kurtosis {selection.deflatedSharpe.kurtosis.toFixed(2)}, average pairwise correlation {selection.deflatedSharpe.averageCorrelation===null?'not estimable (a stream has no variance)':selection.deflatedSharpe.averageCorrelation.toFixed(2)}.</caption><thead><tr><th>Trial-count assumption</th><th>Trials</th><th>Expected max Sharpe (annualised)</th><th>Deflated Sharpe</th></tr></thead><tbody>{#each selection.deflatedSharpe.sensitivities as s}<tr><td>{basisLabels[s.basis]??s.basis}</td><td>{s.trials.toFixed(1)}</td><td>{ann(s.expectedMaximumMonthlySharpe)}</td><td>{prob(s.deflatedSharpeRatio)}</td></tr>{/each}</tbody></table></div>
    {/if}
    <ul class="limitations">{#each selection.limitations as limit}<li>{limit}</li>{/each}</ul>
    {/if}
    <details><summary>Family coverage, pointwise ranges and assumptions</summary>
      <p>Excluded local attempts: {exclusions.map(([reason,count])=>`${count} ${reason.replaceAll('-',' ')}`).join(' · ')||'none'}. A smaller compatible family does not erase these searches. Trials outside this workspace and manual inspection are unknown.</p>
      <p>Expected block starts per path: {attempt.report.sensitivities.map(s=>`${s.expectedBlockMonths}-month setting: ${s.expectedBlocksPerPath.toFixed(1)}`).join(' · ')}. This counts the initial block and probabilistic restarts; it is not an effective independent sample count.</p>
      <div class="table-scroll"><table><caption>Pointwise percentile ranges, before correction for choosing among candidates</caption><thead><tr><th>Run / comparator</th>{#each attempt.report.sensitivities as s}<th>{s.expectedBlockMonths}-month blocks</th>{/each}</tr></thead><tbody>{#each rows as row,i}<tr><td>{row.runId.slice(0,8)} / {labels[row.comparator]}</td>{#each attempt.report.sensitivities as s}<td>{percent(s.intervals[i].pointwiseLower)} to {percent(s.intervals[i].pointwiseUpper)}</td>{/each}</tr>{/each}</tbody></table></div>
      <ul class="limitations">{#each attempt.report.limitations as limit}<li>{limit}</li>{/each}</ul>
      <a class="text-link" href={`/api/research/diagnostics/${attempt.id}`} target="_blank" rel="noreferrer">Full recorded inputs, hashes and diagnostic ↗</a>
    </details>
  {/if}
  {#if history.attempts.length}<details><summary>Diagnostic history · {history.total} recorded attempts</summary><p class="dataset-note">Latest {history.attempts.length} attempts; changing the family or repeating analyses is visible here.</p><div class="table-scroll"><table><thead><tr><th>Recorded</th><th>Anchor run</th><th>Status</th><th>Inspect</th></tr></thead><tbody>{#each history.attempts as a}<tr><td>{new Date(a.createdAt).toLocaleString('en-GB')}</td><td>{a.anchorRunId.slice(0,8)}</td><td>{a.status}</td><td><button class="secondary small-button" disabled={busy} onclick={()=>void inspect(a.id)}>Open</button></td></tr>{/each}</tbody></table></div></details>{/if}
  <a class="text-link" href="https://github.com/Jellman86/twenty/blob/dev/docs/search-diagnostics.md" target="_blank" rel="noreferrer">Statistical method and research basis ↗</a>
</section>
