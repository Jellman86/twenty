<script lang="ts">
  let { disabled=false, onPrepared }: {disabled?:boolean;onPrepared:(sha:string)=>void}=$props();
  type Execution={maximumPriceAgeDays:number;maximumFxAgeDays:number;cashReserveGbp:number;feePerFillGbp:number;
    fills:{halfSpreadBps:number;slippageBps:number;minimumOrderValue:number;maximumParticipation:number;quantityStep:number}};
  type Plan={datasetSha256:string;universeId:string;synthetic:boolean;purpose:string;codeSha256:string;
    decisions:string[];monthlyGbp:number;developmentStart:string;developmentEnd:string;reservedStart:string;reservedEnd:string;
    calendars:{calendarId:string;venue:string;sha256:string}[];actionCatalogSha256:string;baseExecution:Execution;stressExecution:Execution;limitations:string[]};
  let dataset=$state(''),universe=$state(''),synthetic=$state('true'),firstMonth=$state(''),decisionCount=$state(73),decisionTime=$state('23:00');
  let monthly=$state(20),priceAge=$state(45),fxAge=$state(2),reserve=$state(5),fee=$state(1),spread=$state(5),slippage=$state(5),minimum=$state(1),participation=$state(0.05),step=$state(0);
  let calendars=$state(''),actions=$state(''),reviewed=$state(false),busy=$state(false),error=$state(''),sha=$state(''),plan=$state<Plan|null>(null);
  const stamp=(v:string)=>new Date(v).toISOString().slice(0,16).replace('T',' ')+' UTC';
  const failures:Record<string,string>={
    'raw-study-plan-request':'Choose a first calendar month, between 2 and 600 decisions, and a valid UTC time.',
    'universe-study-development-window-too-short':'This grid needs more months for warm-up, development, the unused interval and 24 final periods.',
    'raw-study-plan-capacity':'The server retains at most 100 prepared plan files. Existing plans remain available.',
    'artifact-capacity':'The artifact quota has insufficient unreserved space for another plan.',
    'raw-study-execution-policy':'The execution assumptions or their doubled spread/slippage stress are invalid.',
    'raw-study-calendar-pins':'Supply 1–16 distinct reviewed calendar pins with ids, venues and SHA-256 hashes.',
    'raw-study-action-catalog':'Supply the exact action-catalog SHA-256 hash.',
    'universe-study-declaration':'Check the dataset hash and universe id.'
  };
  function changed(){plan=null;sha='';error='';}
  async function prepare(){
    busy=true;error='';plan=null;sha='';
    try{
      let pins:unknown;try{pins=JSON.parse(calendars);}catch{throw new Error('Calendar pins must be a JSON array.');}
      const response=await fetch('/api/research/raw-study-plans',{method:'POST',headers:{'Content-Type':'application/json','X-Twenty-Request':'1'},body:JSON.stringify({
        datasetSha256:dataset,universeId:universe,synthetic:synthetic==='true',purpose:synthetic==='true'?'engineering':'research',
        firstMonth:firstMonth+'-01',decisionCount,decisionTimeUtc:decisionTime.length===5?decisionTime+':00':decisionTime,monthlyGbp:monthly,
        execution:{maximumPriceAgeDays:priceAge,maximumFxAgeDays:fxAge,cashReserveGbp:reserve,feePerFillGbp:fee,
          halfSpreadBps:spread,slippageBps:slippage,minimumOrderValue:minimum,maximumParticipation:participation,quantityStep:step},
        calendars:pins,actionCatalogSha256:actions
      })});
      if(!response.ok){let code='Plan preparation failed.';try{code=(await response.json()).error??code;}catch{}throw new Error(failures[code]??code);}
      const result:{planSha256:string;planBase64:string}=await response.json();
      const bytes=Uint8Array.from(atob(result.planBase64),c=>c.charCodeAt(0));
      plan=JSON.parse(new TextDecoder().decode(bytes));sha=result.planSha256;
    }catch(e){error=e instanceof Error?e.message:'Plan preparation failed.';}
    finally{busy=false;}
  }
</script>

<details>
  <summary>Prepare a raw study plan</summary>
  <p class="dataset-note">Freeze metadata using this server’s scientific build. Preparation reads no market values and registers no trial or final claim. The dataset, reviewed calendar tables and action catalog must be supplied separately before admission. All hashes remain unverified until admission.</p>
  <form onsubmit={e=>{e.preventDefault();void prepare();}} oninput={changed}>
    <fieldset disabled={busy||disabled}>
      <div class="plan-fields">
        <label>Dataset SHA-256<input bind:value={dataset} pattern={'[0-9a-f]{64}'} minlength="64" maxlength="64" required spellcheck="false"/></label>
        <label>Universe id<input bind:value={universe} maxlength="128" required/></label>
        <label>Input provenance<select bind:value={synthetic}><option value="true">Authored fixture · engineering</option><option value="false">Imported · research; requires provenance audit</option></select></label>
        <label>First calendar month<input type="month" bind:value={firstMonth} required/></label>
        <label>Monthly decisions<input type="number" bind:value={decisionCount} min="2" max="600" step="1" required/></label>
        <label>Month-end decision time (UTC)<input type="time" bind:value={decisionTime} required/></label>
        <label>Monthly deposit (GBP)<input type="number" bind:value={monthly} min="1" max="1000" step="any" required/></label>
        <label>Maximum price age (days)<input type="number" bind:value={priceAge} min="1" max="92" step="1" required/></label>
        <label>Maximum FX age (days)<input type="number" bind:value={fxAge} min="1" max="92" step="1" required/></label>
        <label>Cash reserve (GBP)<input type="number" bind:value={reserve} min="0" step="any" required/></label>
        <label>Fee per fill (GBP)<input type="number" bind:value={fee} min="0" step="any" required/></label>
        <label>Half spread (basis points)<input type="number" bind:value={spread} min="0" max="2500" step="any" required/></label>
        <label>Slippage (basis points)<input type="number" bind:value={slippage} min="0" max="2500" step="any" required/></label>
        <label>Minimum order (local currency)<input type="number" bind:value={minimum} min="0" step="any" required/></label>
        <label>Maximum session participation (fraction)<input type="number" bind:value={participation} min="0" max="1" step="any" required/></label>
        <label>Share quantity step (0 permits fractions)<input type="number" bind:value={step} min="0" step="any" required/></label>
        <label>Action catalog SHA-256<input bind:value={actions} pattern={'[0-9a-f]{64}'} minlength="64" maxlength="64" required spellcheck="false"/></label>
      </div>
      <label>Reviewed calendar pins (JSON array)<textarea bind:value={calendars} rows="5" maxlength="6000" required spellcheck="false" placeholder={'[{"calendarId":"reviewed-id","venue":"XNAS","sha256":"64 lowercase hex characters"}]'}></textarea></label>
      <p class="dataset-note">Decisions fall on calendar month ends at the chosen UTC time; no exchange session is inferred. Execution still requires reviewed sessions. The displayed cost values are editable examples. Stress doubles spread and slippage only.</p>
      <label class="plan-ack"><input type="checkbox" bind:checked={reviewed} required/> I reviewed the input assertions, dates and every execution assumption above.</label>
      <button class="secondary" disabled={busy||disabled}>{busy?'Preparing…':'Prepare frozen plan'}</button>
    </fieldset>
  </form>
  {#if error}<p class="error" role="alert">{error}</p>{/if}
  {#if plan}
    <h3>Prepared plan · review before registration</h3>
    <p class="dataset-note">{plan.synthetic?'Authored engineering inputs':'Imported research inputs; provenance not certified'} · Universe {plan.universeId}. {plan.decisions.length} decisions; monthly deposit £{plan.monthlyGbp}. Development {stamp(plan.developmentStart)}–{stamp(plan.developmentEnd)}. Reserved final {stamp(plan.reservedStart)}–{stamp(plan.reservedEnd)}. Four candidates (1, 2, 4, 8 target names), eight development trials and two final slots will be declared at admission.</p>
    <div class="table-scroll"><table><caption>Frozen execution assumptions</caption><thead><tr><th>Assumption</th><th>Base</th><th>Stress</th></tr></thead><tbody>
      {#each [['Price age, days',plan.baseExecution.maximumPriceAgeDays,plan.stressExecution.maximumPriceAgeDays],['FX age, days',plan.baseExecution.maximumFxAgeDays,plan.stressExecution.maximumFxAgeDays],['Reserve, GBP',plan.baseExecution.cashReserveGbp,plan.stressExecution.cashReserveGbp],['Fee per fill, GBP',plan.baseExecution.feePerFillGbp,plan.stressExecution.feePerFillGbp],['Half spread, bps',plan.baseExecution.fills.halfSpreadBps,plan.stressExecution.fills.halfSpreadBps],['Slippage, bps',plan.baseExecution.fills.slippageBps,plan.stressExecution.fills.slippageBps],['Minimum order, local',plan.baseExecution.fills.minimumOrderValue,plan.stressExecution.fills.minimumOrderValue],['Participation fraction',plan.baseExecution.fills.maximumParticipation,plan.stressExecution.fills.maximumParticipation],['Quantity step',plan.baseExecution.fills.quantityStep,plan.stressExecution.fills.quantityStep]] as row}<tr><td>{row[0]}</td><td>{row[1]}</td><td>{row[2]}</td></tr>{/each}
    </tbody></table></div>
    <details><summary>Frozen hashes, calendars and limitations</summary><div class="plan-hashes"><p>Plan <code>{sha}</code></p><p>Scientific build <code>{plan.codeSha256}</code></p><p>Dataset <code>{plan.datasetSha256}</code></p><p>Action catalog <code>{plan.actionCatalogSha256}</code></p>{#each plan.calendars as pin}<p>{pin.calendarId} · {pin.venue} · <code>{pin.sha256}</code></p>{/each}</div><ul class="limitations">{#each plan.limitations as limitation}<li>{limitation}</li>{/each}</ul></details>
    <button class="secondary" disabled={busy||disabled} onclick={()=>onPrepared(sha)}>Use this plan for registration</button>
    <p class="dataset-note">This copies the frozen hash into the separate registration form. Admission validates the exact originals and records all trials; it does not start them.</p>
  {/if}
</details>
<style>
  fieldset {border:0;padding:0;margin:0;min-width:0}
  .plan-fields {display:grid;grid-template-columns:repeat(auto-fit,minmax(min(230px,100%),1fr));gap:16px;margin:16px 0}
  label {display:block;min-width:0;font-size:14px}
  input:not([type=checkbox]),select,textarea {display:block;width:100%;min-width:0;border:1px solid #cbd7e1;border-radius:5px;padding:10px;background:white;font:inherit;color:var(--navy);margin-top:7px;box-sizing:border-box}
  textarea {resize:vertical}
  .plan-ack {display:flex;gap:10px;align-items:flex-start;margin:16px 0}
  .plan-ack input {flex-shrink:0;margin-top:3px}
  .plan-hashes {overflow-wrap:anywhere;font-size:13px}
</style>
