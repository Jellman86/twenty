<script lang="ts">
  import { onMount } from 'svelte';
  import { brokerCompany, loadBrokerNames, orderDisplayStatus, orderStatusLabels } from './broker-display';
  import type { BrokerNames } from './broker-display';
  type Dataset={id:string;name:string;end:string;synthetic:boolean};
  type Run={id:string;datasetName:string;status:string;config:{hypothesis:string}};
  type Signal={id:string;runId:string;decisionDate:string;featureCutoff:string;generatedAt:string;inputObservedAt:string;synthetic:boolean;modelSha256:string;datasetSha256:string;predictions:{symbol:string;score:number;targetWeight:number}[]};
  type Intent={id:string;phase:string;brokerOrderId:number|null;reportedFilled:number;errorCode:string|null;intent:{symbol:string;brokerTicker?:string;quantity:number;limitPrice:number;quoteCurrency:string};progress:{filled:number;debitGbp:number;remainingReserve:number;status:string}};
  type Account={key:string;environment:string;account:string;allocatedCapitalGbp:number;cashGbp:number;reservedGbp:number;blockCode:string|null;intents:Intent[]};
  type Runtime={configured:boolean;enabled:boolean;automatic:boolean;environment:string;monthlyAllowanceGbp:number|null;credentialsConfigured:boolean;paused:boolean;configurationError:string|null;accounts:Account[];demoReceipts:{id:string;synthetic:boolean}[]};
  let datasets=$state<Dataset[]>([]),runs=$state<Run[]>([]),signals=$state<Signal[]>([]),runtime=$state<Runtime|null>(null),runId=$state(''),datasetId=$state(''),busy=$state(false),error=$state(''),signalError=$state(''),lastCycle=$state(''),captureNote=$state(''),bindings=$state<Record<string,string>>({});
  let names=$state<BrokerNames|null>(null);
  let nameRequest:AbortController|undefined;
  let stopped=false;
  const money=(n:number)=>new Intl.NumberFormat('en-GB',{style:'currency',currency:'GBP'}).format(n);
  const messages:Record<string,string>={'signal-current-panel-required':'A current signal needs observations through last month. Historical fixtures cannot be used.','execution-backtest-policy-mismatch':'Select a contribution-only strategy run; full-rebalance runs cannot approve this policy.','execution-private-configuration-required':'Execution is not configured on the server.','execution-genuine-demo-required':'Live orders need a reconciled demo receipt for this strategy and code first.','execution-paused':'New orders are paused.'};
  async function api(path:string,body?:unknown){const response=await fetch(path,body===undefined?undefined:{method:'POST',headers:{'Content-Type':'application/json','X-Twenty-Request':'1'},body:JSON.stringify(body)});if(!response.ok){const value=await response.json();throw new Error(messages[value.error]??value.error??'service-unavailable');}return response.status===202?null:response.json();}
  async function refresh(){const [state,records]=await Promise.all([api('/api/execution/broker'),api('/api/execution/signals')]);runtime=state;signals=records;}
  function refreshNames(){
    if(stopped)return;
    nameRequest?.abort();const request=nameRequest=new AbortController();names=null;
    if(!runtime?.accounts.some(a=>a.intents.length))return;
    void loadBrokerNames(request.signal).then(result=>{if(request===nameRequest&&!request.signal.aborted)names=result;});
  }
  async function refreshAll(){await refresh();refreshNames();}
  async function createSignal(){busy=true;signalError='';try{await api('/api/execution/signals',{runId,datasetId});await refresh();}catch(e){signalError=e instanceof Error?e.message:'signal-failed';}finally{busy=false;}}
  async function cycle(){busy=true;error='';try{const result=await api('/api/execution/broker/cycle',{});lastCycle=`${result.status} · ${result.submitted} submitted${result.code?` · ${messages[result.code]??result.code}`:''}`;await refresh();}catch(e){error=e instanceof Error?e.message:'cycle-failed';}finally{busy=false;}}
  async function capture(){busy=true;error='';try{const result=await api('/api/execution/broker/capture',{});captureNote=`${result.environment}: ${result.positions} positions, ${result.pendingOrders} pending orders, ${result.historyRows} history rows archived privately.`;}catch(e){error=e instanceof Error?e.message:'capture-failed';}finally{busy=false;}}
  async function action(path:string){busy=true;error='';try{await api(path,{});await refresh();}catch(e){error=e instanceof Error?e.message:'operation-failed';}finally{busy=false;}}
  onMount(()=>{
    void Promise.all([api('/api/research/datasets'),api('/api/research/runs'),refreshAll()]).then(([data,all])=>{datasets=data;runs=all.filter((r:Run)=>r.status==='succeeded');runId=runs[0]?.id??'';datasetId=[...datasets].sort((a,b)=>b.end.localeCompare(a.end))[0]?.id??'';}).catch(e=>error=e.message);
    const timer=setInterval(()=>{if(!stopped&&runtime?.enabled)void refresh().catch(e=>error=e.message);},5000);
    return()=>{stopped=true;nameRequest?.abort();clearInterval(timer);};
  });
</script>
<section class="panel" aria-labelledby="broker-ledger-title">
  <div class="panel-heading"><h2 id="broker-ledger-title">Broker orders</h2><span class="pill {runtime?.environment==='live'?'live':runtime?.environment==='demo'?'demo':''}"><span class="d"></span>{runtime?(runtime.environment==='disconnected'?'Not connected':runtime.environment):'…'}</span></div>
  {#if runtime && !runtime.configured}<p class="dataset-note">Broker execution is not configured on this server. Orders placed here would appear below.</p>{/if}
  {#if runtime?.configurationError}<p class="error">Configuration problem: {runtime.configurationError}</p>{/if}
  {#if runtime?.configured}
    <div class="lab-metrics"><div><span>Execution</span><strong>{runtime.enabled?'Enabled':'Disabled'}</strong></div><div><span>Automatic</span><strong>{runtime.automatic?'On':'Off'}</strong></div><div><span>Monthly allowance</span><strong>{runtime.monthlyAllowanceGbp?money(runtime.monthlyAllowanceGbp):'Not set'}</strong></div><div><span>Demo receipts</span><strong>{runtime.demoReceipts.filter(r=>!r.synthetic).length}</strong></div></div>
  {/if}
  <div class="execution-actions">
    <button disabled={busy||!runtime?.enabled||!runtime?.credentialsConfigured} onclick={()=>void cycle()}>{runtime?.environment==='live'?'Run live order cycle':'Run demo order cycle'}</button>
    <button class="secondary" disabled={busy} onclick={()=>void refreshAll().catch(e=>error=e.message)}>Refresh</button>
    <button class="secondary" disabled={busy||!runtime?.credentialsConfigured} onclick={()=>void capture()}>Save broker snapshot</button>
  </div>
  {#if error}<p class="error" role="alert">{error}</p>{/if}
  {#if lastCycle}<p class="notice" aria-live="polite">{lastCycle}</p>{/if}
  {#if captureNote}<p class="dataset-note">{captureNote}</p>{/if}
  {#each runtime?.accounts??[] as account}
    <div class="broker-account">
      <h3>{account.environment==='live'?'Live':'Demo'} account {account.account}</h3>
      {#if account.blockCode}<p class="error">New orders blocked: {messages[account.blockCode]??account.blockCode}. Existing reservations are kept.</p>{/if}
      <div class="lab-metrics"><div><span>Allocated</span><strong>{money(account.allocatedCapitalGbp)}</strong></div><div><span>Cash</span><strong>{money(account.cashGbp)}</strong></div><div><span>Reserved</span><strong>{money(account.reservedGbp)}</strong></div></div>
      {#if account.intents.length}
        <div class="table-scroll"><table class="stack"><thead><tr><th>Stock</th><th>Status</th><th>Shares</th><th>Filled</th><th>Paid</th><th>Reserved</th><th></th></tr></thead><tbody>
          {#each account.intents as order}
            {@const company=brokerCompany(names,account,order.intent)}
            {@const state=orderDisplayStatus(order)}
          <tr>
            <td><strong>{company??order.intent.symbol}</strong>{#if company}<small>{order.intent.symbol}</small>{/if}<small>limit {order.intent.limitPrice} {order.intent.quoteCurrency}</small></td>
            <td data-label="Status" class:loss={['rejected','unknown'].includes(state)}>{orderStatusLabels[state]??state}{#if order.errorCode}<small>{order.errorCode}</small>{/if}</td>
            <td data-label="Shares">{order.intent.quantity.toFixed(6)}</td>
            <td data-label="Filled">{order.progress.filled.toFixed(6)}{#if order.reportedFilled!==order.progress.filled}<small>broker {order.reportedFilled.toFixed(6)}</small>{/if}</td>
            <td data-label="Paid">{money(order.progress.debitGbp)}</td>
            <td data-label="Reserved">{money(order.progress.remainingReserve)}</td>
            <td>{#if ['planned','pending'].includes(order.phase)}<button class="secondary small-button" disabled={busy||!runtime?.enabled} onclick={()=>void action(`/api/execution/broker/${account.key}/${order.id}/cancel`)}>Cancel</button>{:else if order.phase==='unknown'}<label>Broker order ID (checked in Trading 212)<input type="text" inputmode="numeric" bind:value={bindings[order.id]} /></label><button class="secondary small-button" disabled={busy||!bindings[order.id]||!/^\d+$/.test(bindings[order.id])} onclick={()=>void action(`/api/execution/broker/${account.key}/${order.id}/bind?orderId=${encodeURIComponent(bindings[order.id])}`)}>Link order</button>{/if}</td>
          </tr>{/each}
        </tbody></table></div>
      {:else}<p class="dataset-note">No orders in this account yet.</p>{/if}
    </div>
  {/each}
</section>

<details class="panel" aria-labelledby="forward-signals-title">
  <summary id="forward-signals-title">Signals</summary>
  <form class="signal-form" onsubmit={(e)=>{e.preventDefault();void createSignal();}}><label>Strategy run<select bind:value={runId} required>{#each runs as run}<option value={run.id}>{run.config.hypothesis}</option>{/each}</select></label><label>Latest observations<select bind:value={datasetId} required>{#each datasets as dataset}<option value={dataset.id}>{dataset.name} · to {dataset.end}</option>{/each}</select></label><button class="secondary" disabled={busy||!runId||!datasetId}>Record signal</button></form>
  {#if signalError}<p class="error" role="alert">{signalError}</p>{/if}
  {#if signals.length}<div class="table-scroll"><table class="stack"><thead><tr><th>Month</th><th>Data to</th><th>Picks</th><th>Inputs</th></tr></thead><tbody>{#each signals as signal}<tr><td><strong>{signal.decisionDate.slice(0,7)}</strong></td><td data-label="Data to">{signal.featureCutoff}</td><td data-label="Picks">{signal.predictions.filter(p=>p.targetWeight>0).map(p=>`${p.symbol} ${(p.targetWeight*100).toFixed(1)}%`).join(' · ')||'Cash'}</td><td data-label="Inputs">{signal.synthetic?'Synthetic':'Imported, unaudited'}</td></tr>{/each}</tbody></table></div>{:else}<p class="dataset-note">No signals recorded.</p>{/if}
</details>
