<script lang="ts">
  import { onMount } from 'svelte';
  import BrokerExecution from './BrokerExecution.svelte';
  type Run={id:string;datasetName:string;status:string;config:{hypothesis:string;monthlyGbp:number}};
  type Order={id:string;period:string;intent:{symbol:string;quantity:number;limitPrice:number;maximumDebitGbp:number};progress:{status:string;filled:number;debitGbp:number;remainingReserve:number}};
  type Account={id:string;runId:string;cashGbp:number;contributionsGbp:number;quantities:Record<string,number>;orders:Order[];decisions:{month:string;signalDate:string;weights:Record<string,number>;notes:string[]}[]};
  type Capabilities={broker:{mode:string;credentialsConfigured:boolean;automaticPurchases:boolean;gates:string[]}};
  let runs=$state<Run[]>([]),accounts=$state<Account[]>([]),capabilities=$state<Capabilities|null>(null),runId=$state(''),month=$state(new Date().toISOString().slice(0,7)),paused=$state(false),busy=$state(false),error=$state(''),selected=$state(''),summary=$state('');
  let requestId='';
  const account=$derived(accounts.find(a=>a.id===selected)??accounts[0]);
  const reserved=$derived(account?.orders.reduce((total,o)=>total+o.progress.remainingReserve,0)??0);
  const money=(x:number)=>new Intl.NumberFormat('en-GB',{style:'currency',currency:'GBP'}).format(x);
  const status:Record<string,string>={pending:'Open',partially_filled:'Part filled',filled:'Filled',cancelled:'Cancelled',rejected:'Rejected'};
  const messages:Record<string,string>={'execution-paused':'New orders are paused. Resume them first.','service-unavailable':'The service is unavailable. Try again.'};
  async function api(path:string,body?:unknown){const r=await fetch(path,body===undefined?undefined:{method:'POST',headers:{'X-Twenty-Request':'1','Content-Type':'application/json'},body:JSON.stringify(body)});const value=await r.json();if(!r.ok)throw new Error(messages[value.error]??value.error??'service-unavailable');return value;}
  async function refresh(){const result=await api('/api/execution/paper');accounts=result.accounts;paused=result.paused;if(!accounts.some(a=>a.id===selected))selected=accounts[0]?.id??'';}
  async function action(path:string,body:unknown={}){busy=true;error='';try{const result=await api(path,body);if(result.id)selected=result.id;await refresh();}catch(e){error=e instanceof Error?e.message:'operation-failed';}finally{busy=false;}}
  async function rehearse(){busy=true;error='';requestId ||= crypto.randomUUID();try{const result=await api('/api/execution/paper',{requestId,runId,month});requestId='';selected=result.id;await refresh();}catch(e){error=e instanceof Error?e.message:'rehearsal-failed';}finally{busy=false;}}
  async function readBroker(){busy=true;error='';try{const result=await api('/api/execution/broker/summary');const inAccountCurrency=(x:number)=>new Intl.NumberFormat('en-GB',{style:'currency',currency:result.currency}).format(x);summary=`${result.environment} · account ${result.account} · available ${inAccountCurrency(result.cash.availableToTrade)}`;}catch(e){error=e instanceof Error?e.message:'broker-unavailable';}finally{busy=false;}}
  onMount(()=>{void Promise.all([api('/api/research/runs'),api('/api/research/capabilities'),refresh()]).then(([all,cap])=>{runs=all.filter((r:Run)=>r.status==='succeeded');runId=runs[0]?.id??'';capabilities=cap;}).catch(e=>error=e.message);});
</script>
<div class="page-head">
  <h1>Orders</h1>
  <div class="tools">
    {#if paused}<span class="pill live"><span class="d"></span>New orders paused</span>{/if}
    <button class="secondary small-button" disabled={busy} onclick={()=>void action(`/api/execution/pause?paused=${!paused}`)}>{paused?'Resume new orders':'Pause new orders'}</button>
  </div>
</div>
{#if error}<p class="error" role="alert">{error}</p>{/if}

<BrokerExecution />

<section class="panel" aria-labelledby="sim-orders-title">
  <div class="panel-heading"><h2 id="sim-orders-title">Simulated orders</h2><span class="pill sim"><span class="d"></span>Simulated money</span></div>
  <form class="signal-form" onsubmit={(e)=>{e.preventDefault();void rehearse();}}>
    <label>Strategy run<select bind:value={runId} required>{#each runs as r}<option value={r.id}>{r.config.hypothesis} · {r.datasetName}</option>{/each}</select></label>
    <label>Month<input type="month" bind:value={month} required min="2000-01" max="2200-12"/></label>
    <button disabled={busy||paused||!runId} type="submit">Create simulated orders</button>
  </form>
  {#if !runs.length}<p class="dataset-note">No completed strategy run yet. Run one in <a class="text-link" href="#research">Research</a>.</p>{/if}
  {#if account}
    {#if accounts.length>1}<label>Simulated portfolio<select bind:value={selected}>{#each accounts as a,i}<option value={a.id}>Simulated {i+1} · {a.decisions.length} months</option>{/each}</select></label>{/if}
    <div class="lab-metrics"><div><span>Contributed</span><strong>{money(account.contributionsGbp)}</strong></div><div><span>Cash</span><strong>{money(account.cashGbp)}</strong></div><div><span>Reserved</span><strong>{money(reserved)}</strong></div><div><span>Free cash</span><strong>{money(account.cashGbp-reserved)}</strong></div></div>
    {#if account.orders.length}
      <div class="table-scroll"><table class="stack"><thead><tr><th>Stock</th><th>Month</th><th>Status</th><th>Shares</th><th>Filled</th><th>Limit</th><th>Paid</th></tr></thead><tbody>{#each account.orders as order}<tr><td><strong>{order.intent.symbol}</strong></td><td data-label="Month">{order.period}</td><td data-label="Status">{status[order.progress.status]??order.progress.status}</td><td data-label="Shares">{order.intent.quantity.toFixed(4)}</td><td data-label="Filled">{order.progress.filled.toFixed(4)}</td><td data-label="Limit">{money(order.intent.limitPrice)}</td><td data-label="Paid">{money(order.progress.debitGbp)}</td></tr>{/each}</tbody></table></div>
    {:else}<p class="dataset-note">No simulated orders in this portfolio.</p>{/if}
    {#if reserved}
      <div class="execution-actions"><button class="secondary" disabled={busy} onclick={()=>void action(`/api/execution/paper/${account.id}/reconcile?partial=true`)}>Simulate partial fills</button><button disabled={busy} onclick={()=>void action(`/api/execution/paper/${account.id}/reconcile`)}>Simulate remaining fills</button><button class="secondary" disabled={busy} onclick={()=>void action(`/api/execution/paper/${account.id}/cancel`)}>Cancel unfilled</button></div>
    {/if}
    <details><summary>How simulated orders work</summary><p class="dataset-note">Uses the last model targets of a completed run; it is not a current forecast. Shares round down, fees are reserved, and fills happen at the limit price. Reconciling twice cannot double-charge.</p>{#each account.decisions as decision}<p class="dataset-note"><strong>{decision.month}</strong> · signal {decision.signalDate} · {Object.entries(decision.weights).map(([symbol,weight])=>`${symbol} ${(weight*100).toFixed(1)}%`).join(' · ')||'All cash'}</p>{/each}</details>
  {/if}
</section>

<section class="panel" aria-labelledby="broker-setup-title">
  <div class="panel-heading"><h2 id="broker-setup-title">Broker connection</h2><span class="badge">{capabilities?.broker.mode??'…'}</span></div>
  <div class="lab-metrics"><div><span>Credentials</span><strong>{capabilities?(capabilities.broker.credentialsConfigured?'Configured':'Missing'):'…'}</strong></div><div><span>Automatic purchases</span><strong>{capabilities?.broker.automaticPurchases?'On':'Off'}</strong></div></div>
  <button class="secondary small-button" disabled={busy||!capabilities?.broker.credentialsConfigured} onclick={()=>void readBroker()}>Read account summary</button>
  {#if summary}<p class="dataset-note">{summary}</p>{/if}
  <details><summary>Still required before real automatic orders</summary><ul class="limitations">{#each capabilities?.broker.gates??[] as gate}<li>{gate}</li>{/each}</ul><a class="text-link" href="https://github.com/Jellman86/twenty/blob/dev/docs/trading212-setup.md" target="_blank" rel="noreferrer">Setup instructions ↗</a></details>
</section>
