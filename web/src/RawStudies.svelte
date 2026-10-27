<script lang="ts">
  import { onMount } from 'svelte';
  import SearchDiagnostics from './SearchDiagnostics.svelte';
  import RawStudyPlan from './RawStudyPlan.svelte';
  let { onUpdated = () => {} }: { onUpdated?: () => void } = $props();
  type Candidate = { id:string; config:{topK:number;maximumWeight:number} };
  type Study = { id:string;name:string;status:string;planSha256:string;errorCode?:string;developmentTrialIds:string[];
    selectedCandidateId?:string;selectionReportSha256?:string;finalRequestId?:string;finalPassed?:boolean;finalWorstMarginGbp?:number;
    priorTrials:unknown[];priorUniverseTrials:unknown[];priorRawTrials:unknown[];
    plan:{synthetic:boolean;purpose:string;codeSha256:string;datasetSha256:string;universeId:string;developmentStart:string;developmentEnd:string;
      reservedStart:string;reservedEnd:string;monthlyGbp:number;candidates:Candidate[];limitations:string[]} };
  type Trial = { id:string;studyId:string;trialId:string;candidateId:string;costScenario:string;status:string;errorCode?:string;
    requestSha256:string;reportSha256?:string;publication?:{stageSha256:string;reportSha256:string;verifiedAt:string;workerCommit:string} };
  type Month = { decisionAt:string;endAt:string;contributionGbp:number;openingEquityAfterDepositGbp:number;endingEquityGbp:number;
    cashGbp:number;periodReturn:number;pendingClaimCount?:number;expectedClaimValueGbp?:number };
  type Report = {version:string;trialId:string;planSha256:string;datasetSha256:string;limitations:string[];
    strategies:{strategy:string;endingEquityGbp:number;contributionsGbp:number;feesGbp:number;months:Month[]}[]};
  let studies=$state<Study[]>([]),trials=$state<Trial[]>([]),selected=$state<Study|null>(null),trial=$state<Trial|null>(null),report=$state<Report|null>(null);
  let name=$state('Raw GBP study'),planSha=$state(''),busy=$state(false),error=$state(''),loading=$state(true),acknowledged=$state(false),currentCode=$state(''),planPreparationAvailable=$state(false);
  let wanted='',viewRevision=0,reportRevision=0,refreshRevision=0,registerSignature='',registerRequestId='';
  const requests=new Map<string,string>();
  const money=(value:number)=>new Intl.NumberFormat('en-GB',{style:'currency',currency:'GBP',maximumFractionDigits:2}).format(value);
  const stamp=(value:string)=>new Date(value).toISOString().slice(0,16).replace('T',' ')+' UTC';
  const cost=(value:string)=>value==='base'?'Base costs':value==='stress'?'Stressed costs':value;
  const roles:Record<string,string>={momentum:'Momentum',equal_weight:'Equal-weight comparator',cash:'Zero-interest cash'};
  const messages:Record<string,string>={
    'study-code-changed':'This plan belongs to an older scientific build. Its artifacts remain inspectable; new work requires a compatible declaration.',
    'raw-study-build':'This plan requires its recorded accounting build and runtime. Existing records remain inspectable.',
    'study-holdout-spent':'The permanent final claim is already spent. A replacement request cannot reopen it.',
    'raw-study-input-unavailable':'The prepared plan or one of its originals is missing from the server inbox.',
    'study-reserved-window-conflict':'Another study reserves these calendar outcomes.',
    'study-known-reserved-exposure':'Earlier registered work reaches these outcomes; they cannot be declared unseen.',
    'raw-study-selection-incomplete':'All eight declared development trials must succeed before selection.',
    'raw-study-plan-integrity':'The retained plan failed its integrity check.',
    'raw-study-report-integrity':'The retained report is unavailable or failed its integrity check.',
    'raw-study-execution-refused':'The calculation refused its execution assumptions. Inspect the trial and original request.',
    'raw-study-publication-capacity':'There is not enough artifact headroom for the final pair. This request did not spend the final evaluation.',
    'raw-study-publication-stage-unavailable':'The exact verified stage is missing or unreadable. Recovery requires its originally verified bytes.',
    'raw-study-publication-stage-integrity':'The staged result failed its original binding or hash. Recovery has retained the original claim and marker.',
    'raw-study-publication-report-conflict':'An existing report conflicts with the verified stage. It has not been overwritten.',
    'raw-study-publication-original-unavailable':'An exact dataset original needed to verify the staged receipts is unavailable.',
    'raw-study-publication-write':'Publication could not finish. The original verified marker remains pending.',
    'raw-study-publication-interrupted':'Verification or publication stopped. Recovery retains the original attempt.',
    'raw-study-clock-regression':'The host clock predates a recorded research event. No new state transition was accepted.',
    'research-worker-busy':'Another research calculation owns the worker. Try this operation after it finishes.',
    'artifact-capacity':'The artifact quota is full. Original evidence remains retained.'
  };
  const family=$derived(selected?trials.filter(t=>t.studyId===selected?.id):[]);
  const historical=$derived(!!selected&&!!currentCode&&selected.plan.codeSha256!==currentCode);
  const isFinal=$derived(!!trial&&!!selected&&!selected.developmentTrialIds.includes(trial.id));
  const expectedBasis=$derived(report?.version==='raw-study-development-v4'||report?.version==='raw-study-final-v2');
  const active=$derived(studies.some(s=>s.status==='development'||s.status==='final-evaluation'));
  const publications=$derived(family.filter(t=>t.status==='publishing'));
  function candidateLabel(row:Trial){const c=selected?.plan.candidates.find(c=>c.id===row.candidateId);return c?`${c.config.topK} target ${c.config.topK===1?'name':'names'}, ${c.config.maximumWeight*100}% cap`:row.candidateId;}
  async function api<T>(path:string,body?:unknown):Promise<T>{
    const response=await fetch(path,body===undefined?undefined:{method:'POST',headers:{'Content-Type':'application/json','X-Twenty-Request':'1'},body:JSON.stringify(body)});
    if(!response.ok){let code='Raw study request failed.';try{code=(await response.json()).error??code;}catch{}throw new Error(messages[code]??code);}
    return response.json();
  }
  async function refresh(){
    const revision=++refreshRevision;
    const [register,rows]=await Promise.all([api<{studies:Study[];planPreparationAvailable?:boolean}>('/api/research/raw-studies'),api<Trial[]>('/api/research/raw-trials')]);
    if(revision!==refreshRevision)return;
    planPreparationAvailable=register.planPreparationAvailable===true;studies=register.studies;trials=rows;loading=false;
    if(wanted){const updated=studies.find(s=>s.id===wanted);if(updated)selected=updated;}
  }
  function inspect(study:Study){wanted=study.id;viewRevision++;reportRevision++;selected=study;trial=null;report=null;acknowledged=false;error='';}
  async function inspectReport(row:Trial){
    const revision=++reportRevision,studyId=selected?.id;trial=row;report=null;error='';
    try{const value=await api<Report>(`/api/research/raw-trials/${row.id}/report`);if(revision===reportRevision&&selected?.id===studyId)report=value;}
    catch(e){if(revision===reportRevision)error=e instanceof Error?e.message:'Report unavailable.';}
  }
  async function register(){
    busy=true;error='';const revision=++viewRevision;
    try{
      const payload={name,planSha256:planSha};const signature=JSON.stringify(payload);
      if(signature!==registerSignature||!registerRequestId){registerSignature=signature;registerRequestId=crypto.randomUUID();}
      const study=await api<Study>('/api/research/raw-studies',{requestId:registerRequestId,...payload});
      onUpdated();
      await refresh();if(revision===viewRevision)inspect(study);
    }catch(e){if(revision===viewRevision)error=e instanceof Error?e.message:'Registration failed.';}
    finally{busy=false;}
  }
  async function transition(operation:'start'|'select'|'evaluate-final'|'recover-publication'|'cancel'){
    if(!selected||operation==='evaluate-final'&&!acknowledged)return;
    const study=selected,revision=viewRevision,key=study.id+':'+operation;busy=true;error='';
    if(!requests.has(key))requests.set(key,crypto.randomUUID());
    try{
      const result=await api<Study>(`/api/research/raw-studies/${study.id}/${operation}`,operation==='cancel'?{}:{requestId:operation==='recover-publication'?study.finalRequestId:requests.get(key),planSha256:study.planSha256});
      onUpdated();
      if(revision===viewRevision){selected=result;acknowledged=false;}
      await refresh();
    }catch(e){if(revision===viewRevision)error=e instanceof Error?e.message:'Study request failed.';}
    finally{busy=false;}
  }
  onMount(()=>{
    let stopped=false;
    void Promise.all([refresh(),api<{researchIdentity:{sha256:string}}>('/api/research/capabilities').then(c=>{if(!stopped)currentCode=c.researchIdentity.sha256;})]).catch(e=>{if(!stopped){loading=false;error=e.message;}});
    const timer=setInterval(()=>{if(active&&!busy&&!stopped)void refresh().catch(e=>{if(!stopped)error=e.message;});},2500);
    return()=>{stopped=true;refreshRevision++;viewRevision++;reportRevision++;clearInterval(timer);};
  });
</script>

<section class="panel lab-results" aria-labelledby="raw-study-title">
  <div class="panel-heading"><div><p class="eyebrow">RAW SHARES AND GBP ACCOUNTING</p><h2 id="raw-study-title">Prepare and run a raw study</h2></div><span class="badge">RESEARCH ONLY</span></div>
  <p>Four declared momentum candidates each run under base and stressed costs, with their own equal-weight and cash accounts. Contributions, raw shares, labelled FX, fees and corporate-action claims remain separate. These are bounded research assumptions, with no investment nomination or broker execution.</p>
  {#if planPreparationAvailable}<RawStudyPlan disabled={busy} onPrepared={sha=>{planSha=sha;error='';}}/>{/if}
  <details open={!!planSha}><summary>Register a prepared plan</summary>
    <p class="dataset-note">Prepare a plan above or supply its exact bytes in the server’s inbox. The reviewed calendar, action catalog and dataset still require the documented input workflow. Registration retains all eight trial requests before work starts. This form admits an existing plan; it does not author market inputs or upload files.</p>
    <form class="signal-form" onsubmit={e=>{e.preventDefault();void register();}}>
      <label>Study name<input bind:value={name} maxlength="80" required disabled={busy}/></label>
      <label>Prepared plan SHA-256<input bind:value={planSha} pattern={'[0-9a-f]{64}'} minlength="64" maxlength="64" required spellcheck="false" autocomplete="off" disabled={busy}/></label>
      <button class="primary" disabled={busy}>{busy?'Recording…':'Register prepared raw study'}</button>
    </form>
  </details>
  {#if error}<p class="error" role="alert">{error}</p>{/if}
  {#if loading}<p class="dataset-note">Loading recorded raw studies…</p>{:else if !studies.length}<p class="dataset-note">No raw studies are registered. Prepared plans require independently reviewed inputs; demo model runs are a separate workflow.</p>{/if}
  {#if studies.length}<div class="table-scroll"><table><caption>All retained raw studies; failed and stopped families remain recorded</caption><thead><tr><th>Study</th><th>Inputs</th><th>Status</th><th>Inspect</th></tr></thead><tbody>{#each studies as row}<tr><td>{row.name}</td><td>{row.plan.synthetic?'Authored fixture':'Imported; provenance requires audit'}</td><td>{row.status.replaceAll('-',' ')}</td><td><button class="secondary small-button" disabled={busy} onclick={()=>inspect(row)} aria-label={`Inspect raw study ${row.name}`}>Open</button></td></tr>{/each}</tbody></table></div>{/if}
  {#if selected}
    <h3>{selected.name}</h3>
    <p class="dataset-note">{selected.plan.synthetic?'Authored inputs validate engineering only.':'Imported inputs are not evidence of complete investable coverage.'} Development: {stamp(selected.plan.developmentStart)}–{stamp(selected.plan.developmentEnd)}. Reserved final outcomes: {stamp(selected.plan.reservedStart)}–{stamp(selected.plan.reservedEnd)}. Monthly deposit {money(selected.plan.monthlyGbp)}. Each stage starts empty.</p>
    <p class="dataset-note">Earlier disclosed trials: {selected.priorTrials.length} model, {selected.priorUniverseTrials.length} universe and {selected.priorRawTrials.length} raw. Full originals, policies and limitations remain in the frozen plan.</p>
    {#if historical}<p class="dataset-note">This declaration uses an older scientific build. Its records remain inspectable; starting, selecting or first final spending cannot silently change its code.</p>{/if}
    {#if selected.errorCode}<p class="error" role="alert">Recorded study refusal: {messages[selected.errorCode]??selected.errorCode}.</p>{/if}
    <div class="raw-controls">
      {#if selected.status==='registered'}<button class="primary" disabled={busy||historical||!currentCode} onclick={()=>void transition('start')}>Start eight development trials</button>{/if}
      {#if selected.status==='development-complete'}<button class="secondary" disabled={busy||historical||!currentCode} onclick={()=>void transition('select')}>Freeze development selection</button>{/if}
      {#if ['registered','development','development-complete','candidate-frozen','final-evaluation'].includes(selected.status)}<button class="secondary" disabled={busy} onclick={()=>void transition('cancel')}>Stop this study</button>{/if}
      <button class="secondary" disabled={busy} onclick={()=>void refresh().catch(e=>error=e.message)}>Refresh raw records</button>
    </div>
    {#if selected.status==='final-evaluation'&&publications.length}
      <p>A verified final result awaits publication. Recovery replays its frozen receipts to verify the original result before publishing the same bytes. The original claim, candidate and trial ids stay fixed. A missing stage requires exact restoration or stopping the study.</p>
      {#each publications as pending}
        {#if pending.errorCode}<p class="error" role="alert">{messages[pending.errorCode]??pending.errorCode}</p>{/if}
        {#if pending.publication}<details><summary>Original verification · {pending.trialId}</summary><p class="dataset-note publication-proof">{stamp(pending.publication.verifiedAt)} · Worker {pending.publication.workerCommit}<br/>Expected report SHA-256: <code>{pending.publication.reportSha256}</code><br/>Expected stage SHA-256: <code>{pending.publication.stageSha256}</code></p></details>{/if}
      {/each}
      <button class="secondary" disabled={busy||historical||!currentCode||!selected.finalRequestId} onclick={()=>void transition('recover-publication')}>Publish the verified final result</button>
    {/if}
    {#if selected.selectedCandidateId}<p>Frozen development candidate: {selected.selectedCandidateId}. This ending-wealth screen is not uncertainty adjustment or investment approval.</p>{/if}
    {#if selected.status==='candidate-frozen'&&!selected.finalRequestId}
      <div class="raw-final">
        <h4>One permanent final pair</h4>
        <p>Requesting the final pair spends this study’s reserved outcomes before calculation. Failure, cancellation or publication faults keep the claim spent. Missing or unsupported final-window action coverage can therefore end the attempt permanently.</p>
        <label><input type="checkbox" bind:checked={acknowledged} disabled={busy||historical}/> I understand this permanently spends the reserved final evaluation, including if work fails.</label>
        <button class="primary" disabled={busy||historical||!currentCode||!acknowledged} onclick={()=>void transition('evaluate-final')}>Spend reserved final evaluation</button>
      </div>
    {/if}
    {#if selected.finalRequestId}<p class="dataset-note">Permanent final claim recorded. It cannot be reset or replaced. {selected.finalPassed===undefined?'No completed final screen is recorded.':selected.plan.synthetic?'The authored final screen checks lifecycle only.':selected.finalPassed?'The frozen final screen passed; this grants no investment or broker authority.':'The frozen final screen did not pass.'}{selected.finalWorstMarginGbp!==undefined?` Worst base/stress margin: ${money(selected.finalWorstMarginGbp)}.`:''}</p>{/if}
    <div class="raw-links"><a class="text-link" href={`/api/research/raw-studies/${selected.id}/plan`} target="_blank" rel="noreferrer">Frozen plan ↗</a><a class="text-link" href={`/api/research/raw-studies/${selected.id}/events`} target="_blank" rel="noreferrer">Study events ↗</a>{#if selected.selectionReportSha256}<a class="text-link" href={`/api/research/raw-studies/${selected.id}/selection`} target="_blank" rel="noreferrer">Frozen selection ↗</a>{/if}</div>
    <div class="table-scroll"><table><caption>Original raw trial requests, costs and retained outcomes</caption><thead><tr><th>Candidate</th><th>Costs</th><th>Stage</th><th>Status / refusal</th><th>Evidence</th></tr></thead><tbody>{#each family as row}<tr><td>{candidateLabel(row)}</td><td>{cost(row.costScenario)}</td><td>{selected.developmentTrialIds.includes(row.id)?'Development':'Reserved final'}</td><td>{row.status}{row.errorCode?` · ${row.errorCode}`:''}</td><td><a class="text-link" href={`/api/research/raw-trials/${row.id}/request`} target="_blank" rel="noreferrer">Request ↗</a>{#if row.status==='succeeded'}<button class="secondary small-button" disabled={busy} onclick={()=>void inspectReport(row)} aria-label={`Inspect raw report ${row.trialId}`}>Open {selected.developmentTrialIds.includes(row.id)?'development':'final'} report</button>{/if}</td></tr>{/each}</tbody></table></div>
    {#if trial}
      <h3>{isFinal?'Recorded final':'Development'} report · {trial.trialId}</h3>
      {#if report}
        <p class="dataset-note">{report.version} · {cost(trial.costScenario)}. {expectedBasis?'Monthly opening and ending GBP equity include frozen interim claim estimates; trading cash remains separate. Stage ends must be settled.':'Earlier report format: inspect its original limitations for the mark basis.'} Returns exclude the month’s deposit; ending wealth includes all contributions.</p>
        <div class="table-scroll"><table><caption>Independent accounts under the same trial conventions</caption><thead><tr><th>Account</th><th>Ending GBP</th><th>Contributions</th><th>Actual booked fees</th></tr></thead><tbody>{#each report.strategies as account}<tr><td>{roles[account.strategy]??account.strategy}</td><td>{money(account.endingEquityGbp)}</td><td>{money(account.contributionsGbp)}</td><td>{money(account.feesGbp)}</td></tr>{/each}</tbody></table></div>
        <details><summary>Monthly balances and pending estimates</summary>{#each report.strategies as account}<h4>{roles[account.strategy]??account.strategy}</h4><div class="table-scroll"><table><thead><tr><th>Period end UTC</th><th>Deposit</th><th>Opening after deposit</th><th>Ending equity</th><th>Cash</th><th>Period return</th><th>Pending claims</th><th>Expected claim value</th></tr></thead><tbody>{#each account.months as month}<tr><td>{stamp(month.endAt)}</td><td>{money(month.contributionGbp)}</td><td>{money(month.openingEquityAfterDepositGbp)}</td><td>{money(month.endingEquityGbp)}</td><td>{money(month.cashGbp)}</td><td>{(month.periodReturn*100).toFixed(2)}%</td><td>{expectedBasis?month.pendingClaimCount??0:'Not recorded'}</td><td>{expectedBasis?money(month.expectedClaimValueGbp??0):'Not recorded'}</td></tr>{/each}</tbody></table></div>{/each}</details>
        <ul class="limitations">{#each report.limitations as limitation}<li>{limitation}</li>{/each}</ul>
        <a class="text-link" href={`/api/research/raw-trials/${trial.id}/report`} target="_blank" rel="noreferrer">Full original report and replay receipts ↗</a>
      {:else if !error}<p class="dataset-note">Loading the requested report…</p>{/if}
    {/if}
  {/if}
  <a class="text-link" href="https://github.com/Jellman86/twenty/blob/dev/docs/raw-study-admission.md" target="_blank" rel="noreferrer">Prepared plan and input workflow ↗</a>
</section>
{#if trial&&selected&&selected.developmentTrialIds.includes(trial.id)&&trial.status==='succeeded'}{#key trial.id}<SearchDiagnostics anchorRunId={trial.id} family="raw"/>{/key}{/if}

<style>
  form label {min-width:0}
  form input {display:block;width:100%;min-width:0;height:42px;border:1px solid #cbd7e1;border-radius:5px;padding:0 10px;background:white;font:inherit;color:var(--navy);margin-top:7px}
  .raw-controls,.raw-links {display:flex;flex-wrap:wrap;gap:12px;margin:16px 0}
  .raw-final {border:1px solid #d2ac62;padding:16px;margin:20px 0;border-radius:5px}
  .raw-final label {display:flex;align-items:flex-start;gap:10px;margin-bottom:14px}
  .raw-final input {flex-shrink:0;margin-top:4px}
  td button {display:block;margin-top:8px}
  .publication-proof {overflow-wrap:anywhere}
  .publication-proof code {word-break:break-all}
</style>
