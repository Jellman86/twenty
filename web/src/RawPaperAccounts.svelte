<script lang="ts">
  import {onMount} from 'svelte';
  type Source={id:string;name:string;planSha256:string;selectionReportSha256:string;selectedCandidateId:string;monthlyGbp:number};
  type Account={id:string;studyId:string;planSha256:string;selectionSha256:string;candidateId:string;costScenario:string;status:string;acceptedPeriods:number;attempts:number;
    headReceiptSha256?:string;codeSha256:string;balance:{equityGbp:number;valuedAt?:string;cashGbp:number;contributionsGbp:number;feesGbp:number;holdings:Record<string,{quantity:number}>};
    policy:{firstDecisionAt:string;decisions:string[];datasetSha256:string;execution:unknown}};
  type Operation={id:string;status:string;startedAt:string;receiptSha256?:string;errorCode?:string};
  type Prepared={accountId:string;expectedReceiptSha256:string|null;period:number;decisionAt:string;nextDecisionAt:string;replaySha256:string;replayBytes:number;projectedBalance:Account['balance']};
  let {source=null,currentCode='',onUpdated=()=>{}}:{source?:Source|null;currentCode?:string;onUpdated?:()=>void}=$props();
  let accounts=$state<Account[]>([]),selected=$state<Account|null>(null),operations=$state<Operation[]>([]),busy=$state(false),error=$state(''),ready=$state(false);
  let dataset=$state(''),calendars=$state(''),dates=$state(''),scenario=$state('base'),replaySha=$state('');
  let preparationAvailable=$state(false),prepared=$state<Prepared|null>(null);
  let wanted='',revision=0;const requests=new Map<string,string>();
  const money=(v:number)=>new Intl.NumberFormat('en-GB',{style:'currency',currency:'GBP'}).format(v);
  const stamp=(s:string)=>new Date(s).toISOString().slice(0,16).replace('T',' ')+' UTC';
  const messages:Record<string,string>={
    'raw-paper-dataset-unattested':'Explicitly reimport the exact authored dataset to record its manifest provenance before paper admission. This request did not open its market original.',
    'raw-paper-authored-only':'This first paper workflow accepts authored datasets and authored source studies only.',
    'raw-paper-source-selection':'The source must have a frozen development candidate and its exact selection artifact.',
    'raw-paper-information-window':'The entire new history/date grid must follow the source study’s reserved window.',
    'raw-paper-already-created':'This study and cost scenario already has a paper account. Open its retained account below.',
    'raw-paper-head-conflict':'The account has advanced. Refresh its records before submitting the next batch.',
    'raw-paper-actions-unsupported':'This paper workflow refuses corporate actions. Action integration remains separate work.',
    'raw-paper-stopped':'This account no longer accepts batches. Its accepted balances and attempts remain recorded.',
    'raw-paper-history':'The submitted replay changed an earlier accepted event.',
    'raw-paper-batch':'Supply exactly one complete batch on the frozen date grid, retaining every earlier event.',
    'raw-paper-policy':'The submitted deposit, signal dates, strategy or execution assumptions differ from the frozen account.',
    'raw-paper-request-contract':'The submitted replay does not match this paper-account contract.',
    'raw-paper-canonical':'The prepared replay must contain its exact canonical JSON bytes.',
    'raw-paper-input-unavailable':'An exact prepared input or retained original is missing or unreadable.',
    'raw-paper-integrity':'A retained input, receipt or account record failed its hash/binding check.',
    'raw-paper-calendar':'The submitted calendars differ from the frozen reviewed tables.',
    'raw-paper-capacity':'The account, operation or artifact bound has been reached.',
    'raw-paper-execution-refused':'The frozen execution assumptions cannot produce a complete next batch. Accepted balances are unchanged.',
    'raw-ledger-cash':'The modelled opening fill would exceed available cash. Preparation leaves the account unchanged.',
    'raw-paper-arithmetic':'The frozen inputs exceeded the decimal arithmetic bounds. Accepted balances are unchanged.',
    'process-restarted':'The process stopped before this attempt committed. The earlier accepted balance is retained.',
    'study-code-changed':'This account uses older scientific code. Its records remain inspectable.',
    'research-worker-busy':'Another calculation owns the shared worker. Retry after it finishes.',
    'artifact-capacity':'Insufficient unreserved artifact space; no new calculation was admitted.'
  };
  async function api<T>(path:string,body?:unknown):Promise<T>{
    const response=await fetch(path,body===undefined?undefined:{method:'POST',headers:{'Content-Type':'application/json','X-Twenty-Request':'1'},body:JSON.stringify(body)});
    if(!response.ok){let code='Raw paper request failed.';try{code=(await response.json()).error??code;}catch{}throw new Error(messages[code]??code);}
    return response.json();
  }
  async function refresh(){
    const result=await api<{accounts?:Account[];preparationAvailable?:boolean}>('/api/research/raw-paper-accounts');accounts=result.accounts??[];preparationAvailable=result.preparationAvailable===true;ready=true;
    if(wanted)selected=accounts.find(a=>a.id===wanted)??null;
    if(prepared&&(!selected||selected.id!==prepared.accountId||(selected.headReceiptSha256??null)!==prepared.expectedReceiptSha256||!['ready','active'].includes(selected.status)))prepared=null;
  }
  async function inspect(account:Account){
    const r=++revision;wanted=account.id;selected=account;operations=[];error='';prepared=null;replaySha='';
    try{const rows=await api<Operation[]>(`/api/research/raw-paper-accounts/${account.id}/operations`);if(r===revision)operations=rows;}
    catch(e){if(r===revision)error=e instanceof Error?e.message:'Operations unavailable.';}
  }
  function requestId(key:string){if(!requests.has(key))requests.set(key,crypto.randomUUID());return requests.get(key)!;}
  async function prepare(){
    if(!selected)return;const account=selected,r=revision;busy=true;error='';prepared=null;
    try{
      const result=await api<Prepared>(`/api/research/raw-paper-accounts/${account.id}/prepare`,{expectedReceiptSha256:account.headReceiptSha256??null});
      if(r===revision&&wanted===account.id&&selected&&(selected.headReceiptSha256??null)===result.expectedReceiptSha256){prepared=result;replaySha=result.replaySha256;}
    }catch(e){if(r===revision)error=e instanceof Error?e.message:'Preparation failed.';}finally{busy=false;}
  }
  async function create(){
    if(!source)return;busy=true;error='';
    try{
      let pins:unknown,grid:unknown;try{pins=JSON.parse(calendars);grid=JSON.parse(dates);}catch{throw new Error('Calendar pins and decision dates must be JSON arrays.');}
      if(!Array.isArray(grid)||grid.length<14||grid.length>133||grid.some(d=>typeof d!=='string'))throw new Error('Supply 14–133 ordered UTC decision dates, including the 13-date signal history.');
      const payload={studyId:source.id,planSha256:source.planSha256,selectionSha256:source.selectionReportSha256,costScenario:scenario,datasetSha256:dataset,calendars:pins,firstDecisionAt:grid[12],decisions:grid};
      const account=await api<Account>('/api/research/raw-paper-accounts',{requestId:requestId(JSON.stringify(payload)),...payload});
      await refresh();await inspect(account);onUpdated();
    }catch(e){error=e instanceof Error?e.message:'Account creation failed.';}finally{busy=false;}
  }
  async function advance(){
    if(!selected)return;const account=selected;busy=true;error='';
    try{
      const payload={expectedReceiptSha256:account.headReceiptSha256??null,replaySha256:replaySha};
      const operation=await api<Operation>(`/api/research/raw-paper-accounts/${account.id}/advance`,{requestId:requestId(account.id+JSON.stringify(payload)),...payload});
      await refresh();if(wanted===account.id&&selected)await inspect(selected);onUpdated();
      if(operation.errorCode)error=messages[operation.errorCode]??operation.errorCode;
    }catch(e){error=e instanceof Error?e.message:'Batch unavailable.';}finally{busy=false;}
  }
  async function stop(){
    if(!selected)return;const id=selected.id;busy=true;error='';
    try{await api(`/api/research/raw-paper-accounts/${id}/stop`,{});prepared=null;await refresh();onUpdated();}
    catch(e){error=e instanceof Error?e.message:'Stop failed.';}finally{busy=false;}
  }
  onMount(()=>{void refresh().catch(e=>error=e.message);return()=>{revision++;};});
</script>
<section class="panel lab-results" aria-labelledby="raw-paper-title">
  <div class="panel-heading"><div><p class="eyebrow">INDEPENDENT RAW PAPER LEDGERS</p><h2 id="raw-paper-title">Authored paper batches</h2></div><span class="badge">ENGINEERING REHEARSAL</span></div>
  <p>Rehearse new raw-share fills and GBP cash from an empty ledger using a frozen candidate. These accounts use authored inputs and complete offline batches. Corporate actions, pending claims, asynchronous or partial fills and genuine forward evaluation remain unsupported.</p>
  {#if source}
    <details><summary>Create an authored account from {source.name}</summary>
      <p class="dataset-note">Candidate {source.selectedCandidateId}; monthly deposit {money(source.monthlyGbp)}; the selected base/stress execution policy stays fixed. Supply a separate authored dataset, reviewed calendar pins and the complete date grid. All history must follow the source’s reserved window. One account per study/cost scenario; stopping or failure retains the account and attempts.</p>
      <form onsubmit={e=>{e.preventDefault();void create();}}><fieldset disabled={busy}>
        <label>Paper dataset SHA-256<input bind:value={dataset} pattern={'[0-9a-f]{64}'} minlength="64" maxlength="64" required spellcheck="false"/></label>
        <label>Frozen paper costs<select bind:value={scenario}><option value="base">Base costs</option><option value="stress">Stressed costs</option></select></label>
        <label>Paper calendar pins (JSON array)<textarea bind:value={calendars} rows="3" maxlength="6000" required spellcheck="false"></textarea></label>
        <label>Frozen UTC decision dates (JSON array)<textarea bind:value={dates} rows="4" maxlength="6000" required spellcheck="false"></textarea></label>
        <button class="secondary" disabled={busy}>Create empty authored account</button>
      </fieldset></form>
    </details>
  {:else}<p class="dataset-note">Open a current authored study with a frozen development candidate to create an account. Existing accounts remain inspectable below.</p>{/if}
  {#if error}<p class="error" role="alert">{error}</p>{/if}
  {#if ready&&!accounts.length}<p class="dataset-note">No raw paper accounts are recorded. Existing model paper accounts use their separate simulator.</p>{/if}
  {#if accounts.length}<div class="table-scroll"><table><caption>All retained raw paper accounts</caption><thead><tr><th>Source study</th><th>Candidate / costs</th><th>Status</th><th>Periods</th><th>Contributions</th><th>Inspect</th></tr></thead><tbody>{#each accounts as account}<tr><td>{account.studyId.slice(0,8)}</td><td>{account.candidateId} · {account.costScenario}</td><td>{account.status}</td><td>{account.acceptedPeriods}</td><td>{money(account.balance.contributionsGbp)}</td><td><button class="secondary small-button" disabled={busy} onclick={()=>void inspect(account)} aria-label={`Inspect raw paper ${account.id.slice(0,8)}`}>Open</button></td></tr>{/each}</tbody></table></div>{/if}
  {#if selected}
    <h3>Raw paper account · {selected.id.slice(0,8)}</h3>
    <p>Candidate {selected.candidateId} · {selected.costScenario} costs · source study {selected.studyId.slice(0,8)}. Marked equity {money(selected.balance.equityGbp)}{selected.balance.valuedAt?` at ${stamp(selected.balance.valuedAt)}`:''}. Accepted cash {money(selected.balance.cashGbp)} · contributions {money(selected.balance.contributionsGbp)} · booked fees {money(selected.balance.feesGbp)}. {selected.acceptedPeriods} accepted periods; {selected.attempts} retained attempts.</p>
    <div class="table-scroll"><table><caption>Delivered holdings from accepted fills</caption><thead><tr><th>Listing</th><th>Raw quantity</th></tr></thead><tbody>{#each Object.entries(selected.balance.holdings) as [id,holding]}<tr><td>{id}</td><td>{holding.quantity}</td></tr>{/each}</tbody></table></div>
    <details><summary>Frozen paper policy and original bindings</summary><p class="proof">Source study <code>{selected.studyId}</code><br/>Frozen plan <code>{selected.planSha256}</code><br/>Frozen selection <code>{selected.selectionSha256}</code><br/>Dataset <code>{selected.policy.datasetSha256}</code><br/>Scientific build <code>{selected.codeSha256}</code><br/>Accepted head <code>{selected.headReceiptSha256??'No accepted batch'}</code></p><pre>{JSON.stringify(selected.policy.execution,null,2)}</pre><p class="dataset-note">{selected.policy.decisions.length} frozen history/decision dates; first deposit {stamp(selected.policy.firstDecisionAt)}. Receipts retain the full original decimal accounting.</p></details>
    {#if selected.codeSha256!==currentCode}<p class="dataset-note">This account belongs to an older or unavailable scientific build. Its receipts and accepted state remain inspectable.</p>{/if}
    {#if (selected.status==='ready'||selected.status==='active')&&currentCode===selected.codeSha256}
      {#if preparationAvailable}
        <p class="dataset-note">Prepare one next batch using the frozen candidate and retained authored inputs. Preparation checks the cumulative replay and leaves accepted balances unchanged. Apply it separately to record the batch.</p>
        <button class="secondary" disabled={busy} onclick={()=>void prepare()}>Prepare next authored batch</button>
        {#if prepared}
          <h4>Projected batch {prepared.period} · awaiting application</h4>
          <p>{stamp(prepared.decisionAt)} to {stamp(prepared.nextDecisionAt)}. Projected closing equity {money(prepared.projectedBalance.equityGbp)}, cash {money(prepared.projectedBalance.cashGbp)}, cumulative contributions {money(prepared.projectedBalance.contributionsGbp)} and booked fees {money(prepared.projectedBalance.feesGbp)}.</p>
          <p class="proof">Prepared replay <code>{prepared.replaySha256}</code> · {prepared.replayBytes} bytes.</p>
          <button class="secondary" disabled={busy} onclick={()=>void advance()}>Apply prepared batch</button>
        {/if}
      {/if}
      <details><summary>Apply a manually prepared replay</summary>
      <p class="dataset-note">Supply the cumulative replay in the server inbox as &lt;SHA&gt;.raw-paper.json. It must retain the exact earlier event sequence and add one complete batch on this account’s date grid. A refused admitted attempt stops further batches and retains the earlier accepted balance.</p>
      <form onsubmit={e=>{e.preventDefault();void advance();}}><label>Prepared cumulative paper replay SHA-256<input bind:value={replaySha} oninput={()=>prepared=null} pattern={'[0-9a-f]{64}'} minlength="64" maxlength="64" required disabled={busy} spellcheck="false"/></label><button class="secondary" disabled={busy}>{busy?'Checking batch…':'Apply one complete paper batch'}</button></form>
      </details>
    {/if}
    {#if selected.status==='ready'||selected.status==='active'}<button class="secondary" disabled={busy} onclick={()=>void stop()}>Stop future paper batches</button>{/if}
    <div class="table-scroll"><table><caption>Immutable attempts; uncommitted fills do not change accepted balances</caption><thead><tr><th>Recorded UTC</th><th>Status / refusal</th><th>Evidence</th></tr></thead><tbody>{#each operations as operation}<tr><td>{stamp(operation.startedAt)}</td><td>{operation.status}{operation.errorCode?` · ${operation.errorCode}`:''}</td><td>{#if operation.status==='succeeded'}<a class="text-link" href={`/api/research/raw-paper-operations/${operation.id}/receipt`} target="_blank" rel="noreferrer">Original receipt ↗</a>{/if}</td></tr>{/each}</tbody></table></div>
  {/if}
</section>
<style>
  form,fieldset {display:block;min-width:0;border:0;padding:0;margin:16px 0}
  label {display:block;min-width:0;font-size:14px;margin:12px 0}
  input,select,textarea {display:block;width:100%;min-width:0;box-sizing:border-box;border:1px solid #cbd7e1;border-radius:5px;padding:10px;background:white;font:inherit;color:var(--navy);margin-top:7px}
  textarea {resize:vertical}
  form button {margin:8px 0}
  .proof,pre {overflow-wrap:anywhere;white-space:pre-wrap;font-size:12px}
</style>
