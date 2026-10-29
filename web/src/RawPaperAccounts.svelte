<script lang="ts">
  import {onMount} from 'svelte';
  import DividendTerms from './DividendTerms.svelte';
  import {dividendCatalogJson,emptyDividend} from './raw-paper-dividends';
  import TerminalTerms from './TerminalTerms.svelte';
  import {terminalCatalogJson,emptyTerminal} from './raw-paper-terminals';
  type Source={id:string;name:string;planSha256:string;selectionReportSha256:string;selectedCandidateId:string;monthlyGbp:number};
  type Holding={quantity:number;denomination?:{currency:string;unit:string}};
  type ClaimBalance={count:number;netExpectedValueGbp:number;expectedFeesGbp:number;expectedEquityGbp:number};
  type Balance={dividendClaims?:ClaimBalance;equityGbp:number;valuedAt?:string;cashGbp:number;contributionsGbp:number;feesGbp:number;holdings:Record<string,Holding>};
  type Account={id:string;version:string;studyId:string;planSha256:string;selectionSha256:string;candidateId:string;costScenario:string;status:string;acceptedPeriods:number;attempts:number;
    headReceiptSha256?:string;codeSha256:string;balance:Balance;
    policy:{firstDecisionAt:string;decisions:string[];datasetSha256:string;execution:unknown;actionMode?:string}};
  type Operation={id:string;status:string;startedAt:string;receiptSha256?:string;errorCode?:string};
  type Prepared={accountId:string;expectedReceiptSha256:string|null;period:number;decisionAt:string;nextDecisionAt:string;replaySha256:string;replayBytes:number;projectedBalance:Balance};
  type Support={pureSplits?:boolean;dividends?:boolean;terminals?:boolean;pendingClaims?:boolean;pendingDividendClaims?:boolean;pendingTerminalClaims?:boolean};
  let {sources=[],currentCode='',studyNames={},standalone=false,onUpdated=()=>{}}:{sources?:Source[];currentCode?:string;studyNames?:Record<string,string>;standalone?:boolean;onUpdated?:()=>void}=$props();
  let accounts=$state<Account[]>([]),selected=$state<Account|null>(null),operations=$state<Operation[]>([]),busy=$state(false),error=$state(''),ready=$state(false);
  let sourceId=$state(''),dataset=$state(''),calendars=$state(''),dates=$state(''),scenario=$state('base'),replaySha=$state(''),actionMode=$state('');
  let dividendRows=$state([emptyDividend()]),dividendsAvailable=$state(false),pendingDividendsAvailable=$state(false),authoredOnly=$state(false);
  let terminalRows=$state([emptyTerminal()]),terminalsAvailable=$state(false);
  let preparationAvailable=$state(false),pureSplitsAvailable=$state(false),support=$state<Support|null>(null),prepared=$state<Prepared|null>(null);
  let wanted='',revision=0;const requests=new Map<string,string>();
  const source=$derived(sources.find(s=>s.id===sourceId)??sources[0]??null);
  const money=(v:number)=>new Intl.NumberFormat('en-GB',{style:'currency',currency:'GBP'}).format(v);
  const day=(s:string)=>new Date(s).toLocaleDateString('en-GB',{day:'numeric',month:'short',year:'numeric',timeZone:'UTC'});
  const stamp=(s:string)=>new Date(s).toISOString().slice(0,16).replace('T',' ')+' UTC';
  const statusLabel:Record<string,string>={ready:'Ready',active:'Active',stopped:'Stopped',failed:'Failed',succeeded:'Applied',running:'Running',refused:'Refused'};
  // Frozen at creation; unsupported version/mode combinations remain read-only.
  const KNOWN:Record<string,string|undefined>={'raw-paper-batches-v1':undefined,'raw-paper-split-batches-v1':'pure-splits-v1','raw-paper-dividend-batches-v1':'cash-dividends-v1','raw-paper-dividend-claim-batches-v1':'dividend-claims-v1','raw-paper-terminal-batches-v1':'terminal-settlements-v1'};
  const known=(a:Account)=>a.version in KNOWN&&KNOWN[a.version]===a.policy.actionMode;
  const modeLabel=(a:Account)=>!known(a)?'Unsupported mode':a.policy.actionMode==='pure-splits-v1'?'Stock splits':a.policy.actionMode==='cash-dividends-v1'?'Cash dividends':a.policy.actionMode==='dividend-claims-v1'?'Pending dividends':a.policy.actionMode==='terminal-settlements-v1'?'Terminal settlements':'No corporate actions';
  const equity=(b:Balance)=>b.dividendClaims?.expectedEquityGbp??b.equityGbp;
  const name=(a:Account)=>studyNames[a.studyId]??`Study ${a.studyId.slice(0,8)}`;
  const writable=(a:Account)=>known(a)&&a.codeSha256===currentCode&&(a.policy.actionMode!=='dividend-claims-v1'||pendingDividendsAvailable)&&(a.policy.actionMode!=='terminal-settlements-v1'||terminalsAvailable)&&['ready','active'].includes(a.status);
  const messages:Record<string,string>={
    'raw-paper-dataset-unattested':'Re-import the exact authored dataset to record its provenance first.',
    'raw-paper-authored-only':'Paper accounts accept authored datasets and studies only.',
    'raw-paper-source-selection':'The study needs a frozen candidate and its selection record.',
    'raw-paper-information-window':'All history and dates must follow the study’s reserved window.',
    'raw-paper-already-created':'This study already has a paper account for these costs. Open it from the list.',
    'raw-paper-head-conflict':'The account has moved on. Refresh and prepare again.',
    'raw-paper-actions-unsupported':'This account cannot process that corporate action in its frozen mode.',
    'raw-paper-split-vintage':'Split inputs must come from a single original action record.',
    'raw-paper-split-ambiguous':'Two splits share an effective or booking time; the batch was refused.',
    'raw-paper-action-during-batch':'A corporate action falls inside the fill batch; the batch was refused.',
    'raw-paper-action-binding':'The action record does not match the frozen account inputs.',
    'raw-paper-dividend-policy':'The dividend terms do not match the frozen dataset or payment policy.',
    'raw-paper-dividend-unsettled':'This dividend is unpaid at a monthly boundary. Cross-month claims are not supported.',
    'raw-paper-dividend-mark-coverage':'An unpaid mark needs expected deductions with evidence already available at that time.',
    'raw-paper-dividend-final-unsettled':'Every captured dividend must be paid by the last account mark.',
    'raw-paper-dividend-claim-capacity':'At most eight dividend claims can remain unpaid at a mark or decision.',
    'raw-paper-dividend-ambiguous':'Dividend events overlap; their order needs a separate supported contract.',
    'raw-paper-terminal-policy':'The terminal terms do not match the frozen dataset or settlement policy.',
    'raw-paper-terminal-unsettled':'This terminal action is unsettled at a monthly boundary. Pending terminal claims need a separate supported policy.',
    'raw-paper-terminal-ambiguous':'Terminal events overlap; their order needs a separate supported contract.',
    'raw-ledger-terminal-fx':'The settlement needs its exact executable FX observation at the actual settlement time.',
    'raw-ledger-terminal-successor-history':'Successor actions or quantity changes require explicit ownership reconstruction.',
    'raw-paper-split-price':'The split price data is inconsistent with the share ratio.',
    'raw-price-share-units':'A price predates a known split, so it is in old share units. Refused rather than showing a false gain.',
    'raw-paper-action-history':'The corporate-action history changed from the frozen account.',
    'raw-ledger-action-history':'The ledger’s action history changed from the accepted record.',
    'raw-ledger-action-unsupported':'A known corporate action has not been booked for the held shares. The batch cannot be valued.',
    'raw-paper-stopped':'This account is stopped. Its balances and attempts are kept.',
    'raw-paper-history':'The replay changed an earlier accepted event.',
    'raw-paper-batch':'Supply exactly one complete batch on the frozen date grid.',
    'raw-paper-policy':'The deposit, dates, strategy or costs differ from the frozen account.',
    'raw-paper-request-contract':'The replay does not match this account’s contract.',
    'raw-paper-canonical':'The prepared replay must be the exact canonical batch.',
    'raw-paper-input-unavailable':'A required prepared input is missing or unreadable.',
    'raw-paper-integrity':'A stored input, receipt or record failed its integrity check.',
    'raw-paper-calendar':'The calendars differ from the frozen reviewed tables.',
    'raw-paper-capacity':'The account or storage limit has been reached.',
    'raw-paper-execution-refused':'The frozen costs cannot produce a complete next batch. Balances are unchanged.',
    'raw-paper-action-mode':'This corporate-action mode is not supported by the server.',
    'raw-ledger-cash':'Available settled cash cannot cover this batch or its settlement fees.',
    'raw-paper-arithmetic':'The inputs exceeded the decimal arithmetic limits. Balances are unchanged.',
    'process-restarted':'The server restarted before this attempt finished. The earlier balance is kept.',
    'study-code-changed':'This account uses an older build and is read-only.',
    'research-worker-busy':'Another calculation is running. Try again shortly.',
    'artifact-capacity':'Not enough storage space; nothing was started.'
  };
  async function api<T>(path:string,body?:unknown,rawJson=false):Promise<T>{
    const response=await fetch(path,body===undefined?undefined:{method:'POST',headers:{'Content-Type':'application/json','X-Twenty-Request':'1'},body:rawJson?body as string:JSON.stringify(body)});
    if(!response.ok){let code='Paper account request failed.';try{code=(await response.json()).error??code;}catch{}throw new Error(messages[code]??code);}
    return response.json();
  }
  async function refresh(){
    const result=await api<{accounts?:Account[];authoredOnly?:boolean;preparationAvailable?:boolean;pureSplitsAvailable?:boolean;actionSupport?:Support;dividendMode?:string;dividendScope?:string;pendingDividendMode?:string;pendingDividendScope?:string;terminalMode?:string;terminalScope?:string}>('/api/research/raw-paper-accounts');
    accounts=result.accounts??[];preparationAvailable=result.preparationAvailable===true;pureSplitsAvailable=result.pureSplitsAvailable===true;support=result.actionSupport??null;ready=true;
    authoredOnly=result.authoredOnly===true;
    dividendsAvailable=support?.dividends===true&&result.dividendMode==='cash-dividends-v1'&&result.dividendScope==='paid-before-monthly-marks';
    pendingDividendsAvailable=support?.pendingDividendClaims===true&&result.pendingDividendMode==='dividend-claims-v1'&&result.pendingDividendScope==='paid-by-final-monthly-mark';
    terminalsAvailable=support?.terminals===true&&result.terminalMode==='terminal-settlements-v1'&&result.terminalScope==='settled-before-monthly-marks';
    if(actionMode==='pure-splits-v1'&&!pureSplitsAvailable||actionMode==='cash-dividends-v1'&&!dividendsAvailable||actionMode==='dividend-claims-v1'&&!pendingDividendsAvailable||actionMode==='terminal-settlements-v1'&&!terminalsAvailable)actionMode='';
    if(wanted)selected=accounts.find(a=>a.id===wanted)??null;
    else if(standalone&&accounts.length&&!selected)void inspect(accounts[0]);
    if(prepared&&(!selected||selected.id!==prepared.accountId||(selected.headReceiptSha256??null)!==prepared.expectedReceiptSha256||!['ready','active'].includes(selected.status)))prepared=null;
  }
  async function inspect(account:Account){
    const r=++revision;wanted=account.id;selected=account;operations=[];error='';prepared=null;replaySha='';
    try{const rows=await api<Operation[]>(`/api/research/raw-paper-accounts/${account.id}/operations`);if(r===revision)operations=rows;}
    catch(e){if(r===revision)error=e instanceof Error?e.message:'Attempts unavailable.';}
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
      // Omit action fields by default. Dividend decimal tokens go directly to System.Decimal.
      if(actionMode==='pure-splits-v1'&&!pureSplitsAvailable||actionMode==='cash-dividends-v1'&&!dividendsAvailable||actionMode==='dividend-claims-v1'&&!pendingDividendsAvailable||actionMode==='terminal-settlements-v1'&&!terminalsAvailable)throw new Error('Refresh server support before creating this account.');
      const payload={studyId:source.id,planSha256:source.planSha256,selectionSha256:source.selectionReportSha256,costScenario:scenario,datasetSha256:dataset,calendars:pins,firstDecisionAt:grid[12],decisions:grid,...(actionMode?{actionMode}:{})};
      let json=JSON.stringify(payload);
      if(actionMode==='cash-dividends-v1'||actionMode==='dividend-claims-v1')json=json.slice(0,-1)+',"dividends":'+dividendCatalogJson(dividendRows,dataset,actionMode==='dividend-claims-v1')+'}';
      if(actionMode==='terminal-settlements-v1')json=json.slice(0,-1)+',"terminals":'+terminalCatalogJson(terminalRows,dataset)+'}';
      const body='{"requestId":'+JSON.stringify(requestId(json))+','+json.slice(1);
      const account=await api<Account>('/api/research/raw-paper-accounts',body,true);
      await refresh();await inspect(account);onUpdated();
    }catch(e){error=e instanceof Error?e.message:'Account creation failed.';}finally{busy=false;}
  }
  async function advance(){
    if(!selected)return;const account=selected;busy=true;error='';
    try{
      const payload={expectedReceiptSha256:account.headReceiptSha256??null,replaySha256:replaySha};
      const operation=await api<Operation>(`/api/research/raw-paper-accounts/${account.id}/advance`,{requestId:requestId(account.id+JSON.stringify(payload)),...payload});
      prepared=null;await refresh();if(wanted===account.id&&selected)await inspect(selected);onUpdated();
      if(operation.errorCode)error=messages[operation.errorCode]??operation.errorCode;
    }catch(e){error=e instanceof Error?e.message:'Batch unavailable.';}finally{busy=false;}
  }
  async function stop(){
    if(!selected||!confirm('Stop this paper account? It will accept no further batches. Balances and attempts are kept.'))return;
    const id=selected.id;busy=true;error='';
    try{await api(`/api/research/raw-paper-accounts/${id}/stop`,{});prepared=null;await refresh();onUpdated();}
    catch(e){error=e instanceof Error?e.message:'Stop failed.';}finally{busy=false;}
  }
  onMount(()=>{void refresh().catch(e=>{error=e.message;ready=true;});return()=>{revision++;};});
</script>

{#if standalone}
  <div class="page-head"><h1>Paper accounts</h1><button class="secondary small-button" disabled={busy} onclick={()=>void refresh().catch(e=>error=e.message)}>Refresh</button></div>
{:else}
  <div class="panel-heading"><h2>Paper accounts</h2></div>
{/if}
{#if error}<p class="error" role="alert">{error}</p>{/if}
{#if !ready}<p class="loading" role="status">Loading paper accounts…</p>{/if}
{#if ready&&authoredOnly}<p class="dataset-note">Balances use authored inputs for accounting rehearsals.</p>{/if}
{#if ready&&!accounts.length}<section class="panel empty"><h3>No paper accounts</h3><p>Create one below from a study with a frozen candidate.</p></section>{/if}

{#if accounts.length}
  <div class="accts" role="group" aria-label="Paper accounts">
    {#each accounts as account}
      <button class="acct" aria-pressed={selected?.id===account.id} disabled={busy} onclick={()=>void inspect(account)}>
        <span class="row"><strong>{name(account)} · {account.costScenario} costs</strong><span class="badge">{statusLabel[account.status]??account.status}</span></span>
        <span class="num">{money(equity(account.balance))}{#if account.balance.dividendClaims?.count}<small class="muted"> estimated</small>{/if}</span>
        <span class="muted">{account.acceptedPeriods} {account.acceptedPeriods===1?'period':'periods'} · {modeLabel(account)}</span>
      </button>
    {/each}
  </div>
{/if}

{#if selected}
  {@const a=selected}
  <div class="paper">
    <div class="col">
      <section class="panel" aria-labelledby="paper-balance-title">
        <div class="panel-heading"><h2 id="paper-balance-title">{name(a)} · {a.costScenario} costs</h2><span class="pill sim"><span class="d"></span>Paper · {modeLabel(a)}</span></div>
        {#if a.balance.dividendClaims?.count}<p class="eyebrow">Estimated equity</p>{/if}
        <div class="big"><b>{money(equity(a.balance))}</b><span class="muted">{a.balance.valuedAt?`at ${day(a.balance.valuedAt)}`:'not yet valued'}</span></div>
        <div class="kvs"><div class="kv"><span>Contributed</span><b>{money(a.balance.contributionsGbp)}</b></div><div class="kv"><span>Cash</span><b>{money(a.balance.cashGbp)}</b></div><div class="kv"><span>Fees</span><b>{money(a.balance.feesGbp)}</b></div><div class="kv"><span>Accepted</span><b>{a.acceptedPeriods} {a.acceptedPeriods===1?'period':'periods'}</b></div></div>
        {#if a.balance.dividendClaims}
          <dl class="claim-values"><dt>Holdings and cash</dt><dd>{money(a.balance.equityGbp)}</dd>
            <dt>Pending dividends</dt><dd>{a.balance.dividendClaims.count}</dd>
            <dt>Estimated dividend receivable</dt><dd>{money(a.balance.dividendClaims.netExpectedValueGbp)}</dd>
            <dt>Estimated dividend fees</dt><dd>{money(a.balance.dividendClaims.expectedFeesGbp)}</dd></dl>
          <p class="dataset-note">The receivable includes estimated deductions. Cash and Fees above contain settled amounts only.</p>
        {/if}
        <h3>Holdings</h3>
        {#if Object.keys(a.balance.holdings).length}
          <div class="table-scroll"><table class="stack"><thead><tr><th>Listing</th><th>Shares</th><th>Quoted in</th></tr></thead><tbody>{#each Object.entries(a.balance.holdings) as [id,holding]}<tr><td><strong>{id}</strong></td><td data-label="Shares">{holding.quantity}</td><td data-label="Quoted in">{holding.denomination?`${holding.denomination.currency}${holding.denomination.unit==='major'?'':` (${holding.denomination.unit})`}`:'—'}</td></tr>{/each}</tbody></table></div>
        {:else}<p class="dataset-note">No shares yet.</p>{/if}
      </section>
      <section class="panel" aria-labelledby="paper-attempts-title">
        <h2 id="paper-attempts-title">Batches</h2>
        {#if operations.length}
          <div class="table-scroll"><table class="stack"><thead><tr><th>Recorded</th><th>Result</th><th>Receipt</th></tr></thead><tbody>{#each operations as operation}<tr><td>{stamp(operation.startedAt)}</td><td data-label="Result" class:loss={!!operation.errorCode}>{statusLabel[operation.status]??operation.status}{#if operation.errorCode}<small>{messages[operation.errorCode]??operation.errorCode}</small>{/if}</td><td data-label="Receipt">{#if operation.status==='succeeded'}<a class="text-link" href={`/api/research/raw-paper-operations/${operation.id}/receipt`} download={`paper-receipt-${operation.id}.json`}>Download</a>{:else}—{/if}</td></tr>{/each}</tbody></table></div>
        {:else}<p class="dataset-note">No batches yet.</p>{/if}
      </section>
    </div>
    <div class="col">
      <section class="panel" aria-labelledby="paper-next-title">
        <h2 id="paper-next-title">Next batch</h2>
        {#if !known(a)}<p class="dataset-note">This account uses an unsupported mode. It is read-only.</p>
        {:else if a.codeSha256!==currentCode}<p class="dataset-note">Created with an older build. Read-only.</p>
        {:else if a.policy.actionMode==='dividend-claims-v1'&&!pendingDividendsAvailable}<p class="dataset-note">This server does not advertise this account’s pending-dividend policy. It is read-only.</p>
        {:else if a.policy.actionMode==='terminal-settlements-v1'&&!terminalsAvailable}<p class="dataset-note">This server does not advertise this account’s terminal-settlement policy. It is read-only.</p>
        {:else if !['ready','active'].includes(a.status)}<p class="dataset-note">This account is {statusLabel[a.status]?.toLowerCase()??a.status} and accepts no more batches.</p>{/if}
        {#if writable(a)&&preparationAvailable}
          <p class="dataset-note">Prepare previews the next month. Nothing changes until you apply it.</p>
          <button class="secondary" disabled={busy} onclick={()=>void prepare()}>{busy&&!prepared?'Preparing…':'Prepare next batch'}</button>
          {#if prepared}
            <div class="proj" aria-live="polite">
              <h3>Projected batch {prepared.period} · Not yet applied</h3>
              <p class="dataset-note">{day(prepared.decisionAt)} to {day(prepared.nextDecisionAt)}</p>
              <dl><dt>{prepared.projectedBalance.dividendClaims?.count?'Estimated equity':'Equity'}</dt><dd>{money(equity(prepared.projectedBalance))}</dd><dt>Cash</dt><dd>{money(prepared.projectedBalance.cashGbp)}</dd><dt>Contributed</dt><dd>{money(prepared.projectedBalance.contributionsGbp)}</dd><dt>Fees</dt><dd>{money(prepared.projectedBalance.feesGbp)}</dd>
                {#if prepared.projectedBalance.dividendClaims}<dt>Holdings and cash</dt><dd>{money(prepared.projectedBalance.equityGbp)}</dd>
                  <dt>Pending dividends</dt><dd>{prepared.projectedBalance.dividendClaims.count}</dd>
                  <dt>Estimated dividend receivable</dt><dd>{money(prepared.projectedBalance.dividendClaims.netExpectedValueGbp)}</dd>
                  <dt>Estimated dividend fees</dt><dd>{money(prepared.projectedBalance.dividendClaims.expectedFeesGbp)}</dd>{/if}
                {#each Object.entries(prepared.projectedBalance.holdings) as [id,holding]}<dt>{id} shares</dt><dd>{holding.quantity}</dd>{/each}</dl>
              <button disabled={busy} onclick={()=>void advance()}>{busy?'Applying…':'Apply batch'}</button>
            </div>
          {/if}
        {/if}
        {#if a.status==='ready'||a.status==='active'}{#if known(a)}<div class="actions"><button class="secondary small-button" disabled={busy} onclick={()=>void stop()}>Stop account</button></div>{/if}{/if}
      </section>
      <details class="panel">
        <summary>Account details</summary>
        <dl class="capabilities">
          <div><dt>Corporate actions</dt><dd>{modeLabel(a)}</dd></div>
          <div><dt>Candidate</dt><dd>{a.candidateId}</dd></div>
          <div><dt>Decision dates</dt><dd>{a.policy.decisions.length}, first deposit {day(a.policy.firstDecisionAt)}</dd></div>
          <div><dt>Attempts</dt><dd>{a.attempts}</dd></div>
        </dl>
        {#if support}
          <p class="eyebrow">Server action support</p>
          <div class="matrix">
            {#each [['Stock splits',support.pureSplits],['Dividends paid within each month',support.dividends],['Settled terminal actions',terminalsAvailable],['Pending dividend claims',support.pendingDividendClaims],['Pending terminal claims',support.pendingTerminalClaims]] as [label,ok]}<div><span>{label}</span><span class:gain={ok===true} class:muted={ok!==true}>{ok===true?'Supported':'Not supported'}</span></div>{/each}
          </div>
        {/if}
        <p class="proof">version {a.version}{a.policy.actionMode?` · mode ${a.policy.actionMode}`:''}<br/>account {a.id}<br/>study {a.studyId}<br/>plan {a.planSha256}<br/>selection {a.selectionSha256}<br/>dataset {a.policy.datasetSha256}<br/>build {a.codeSha256}<br/>head {a.headReceiptSha256??'none'}{#if prepared}<br/>prepared replay {prepared.replaySha256} · {prepared.replayBytes} bytes{/if}</p>
        <pre>{JSON.stringify(a.policy.execution,null,2)}</pre>
        {#if writable(a)}
          <details><summary>Apply a manually prepared replay</summary>
            <p class="dataset-note">Place the cumulative replay in the server inbox as &lt;SHA&gt;.raw-paper.json. It must keep every earlier event and add one complete batch.</p>
            <form onsubmit={e=>{e.preventDefault();void advance();}}><label>Replay SHA-256<input bind:value={replaySha} oninput={()=>prepared=null} pattern={'[0-9a-f]{64}'} minlength="64" maxlength="64" required disabled={busy} spellcheck="false"/></label><div class="actions"><button class="secondary" disabled={busy}>Apply replay</button></div></form>
          </details>
        {/if}
      </details>
    </div>
  </div>
{/if}

<details class="panel">
  <summary>New paper account</summary>
  {#if source}
    <form onsubmit={e=>{e.preventDefault();void create();}}><fieldset disabled={busy}>
      {#if sources.length>1}<label>Study<select bind:value={sourceId}>{#each sources as s}<option value={s.id}>{s.name}</option>{/each}</select></label>{/if}
      <p class="dataset-note">{source.name} · candidate {source.selectedCandidateId} · {money(source.monthlyGbp)} a month. One account per study and cost level.</p>
      <label>Costs<select bind:value={scenario}><option value="base">Base costs</option><option value="stress">Stressed costs</option></select></label>
      <label>Dataset SHA-256<input bind:value={dataset} pattern={'[0-9a-f]{64}'} minlength="64" maxlength="64" required spellcheck="false"/></label>
      <label>Calendar pins (JSON)<textarea bind:value={calendars} rows="3" maxlength="6000" required spellcheck="false"></textarea></label>
      <label>Decision dates (JSON)<textarea bind:value={dates} rows="4" maxlength="6000" required spellcheck="false"></textarea></label>
      {#if pureSplitsAvailable||dividendsAvailable||pendingDividendsAvailable||terminalsAvailable}
        <label>Corporate actions<select bind:value={actionMode}><option value="">No corporate actions</option>{#if pureSplitsAvailable}<option value="pure-splits-v1">Stock splits</option>{/if}{#if dividendsAvailable}<option value="cash-dividends-v1">Cash dividends</option>{/if}{#if pendingDividendsAvailable}<option value="dividend-claims-v1">Pending dividends</option>{/if}{#if terminalsAvailable}<option value="terminal-settlements-v1">Terminal settlements</option>{/if}</select></label>
        <p class="dataset-note">Fixed permanently at creation. Corporate-action modes cannot be combined. Pending terminal claims are not supported.</p>
      {/if}
      {#if actionMode==='cash-dividends-v1'&&dividendsAvailable}
        <p class="dataset-note">Authored cash dividends only. Every captured claim must be paid before each monthly mark, including the first decision.</p>
        <DividendTerms bind:rows={dividendRows}/>
      {:else if actionMode==='dividend-claims-v1'&&pendingDividendsAvailable}
        <p class="dataset-note">Authored cash dividends may remain unpaid between months. Declare expected deductions separately; every captured claim must settle by the last account mark.</p>
        <DividendTerms bind:rows={dividendRows} withClaims/>
      {:else if actionMode==='terminal-settlements-v1'&&terminalsAvailable}
        <p class="dataset-note">Authored zero, cash or stock terminal outcomes only. Every captured claim must settle before each monthly mark, including the first decision. Mixed consideration is not supported.</p>
        <TerminalTerms bind:rows={terminalRows}/>
      {/if}
      <div class="actions"><button disabled={busy}>Create account</button></div>
    </fieldset></form>
  {:else}
    <p class="dataset-note">Needs a current authored study with a frozen candidate. Prepare one in <a class="text-link" href="#research">Research</a>.</p>
  {/if}
</details>

<style>
  form,fieldset {display:grid;gap:12px;min-width:0;border:0;padding:0;margin:12px 0 0}
  textarea {resize:vertical}
  .claim-values {display:grid;grid-template-columns:minmax(0,1fr) auto;gap:8px;margin:16px 0}
  .claim-values dd {margin:0;font-variant-numeric:tabular-nums;text-align:right}
  details.panel > summary {font-weight:600;color:var(--text)}
</style>
