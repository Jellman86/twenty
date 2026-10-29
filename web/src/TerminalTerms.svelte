<script lang="ts">
  import {terminalCommonFields,terminalDeliveryFields,terminalCashFields,terminalStockFields,emptyTerminal,terminalAmount} from './raw-paper-terminals';
  import type {TerminalDraft} from './raw-paper-terminals';
  let {rows=$bindable<TerminalDraft[]>([emptyTerminal()])}=$props();
</script>

<p class="dataset-note">Supply one row for every terminal action in the dataset. Cash and successor quantities are per retired cutoff share; the GBP fee is per entitled account. Evidence and actual settlement terms are fixed at creation. Times require seconds and UTC (Z or +00:00).</p>
{#each rows as row,index}
  <fieldset class="terminal">
    <legend>Terminal action {index+1}</legend>
    <label>Terminal outcome<select bind:value={row.outcome}><option value="cash">Cash payout</option><option value="stock">Stock delivery</option><option value="zero">Confirmed zero</option></select></label>
    {#each terminalCommonFields as [key,label,max]}
      <label>{label}<input bind:value={row[key]} maxlength={max} required spellcheck="false" placeholder={key.endsWith('At')?'YYYY-MM-DDTHH:mm:ssZ':undefined}/></label>
    {/each}
    {#if row.outcome==='zero'}
      <p class="dataset-note">A confirmed zero retires owned shares without a payment, delivery or fee. Previously edited settlement fields are omitted.</p>
    {:else}
      {#each [...terminalDeliveryFields,...(row.outcome==='cash'?terminalCashFields:terminalStockFields)] as [key,label,max]}
        <label>{label}<input bind:value={row[key]} maxlength={max} required={key!=='fxObservationId'} inputmode={terminalAmount(key)?'decimal':'text'} spellcheck="false" placeholder={key.endsWith('At')?'YYYY-MM-DDTHH:mm:ssZ':undefined}/></label>
      {/each}
      <p class="dataset-note">Declare the actual fee explicitly, including zero. Cash payouts need explicit withholding. Stock delivery never creates cash or permits selling shares before delivery.</p>
    {/if}
    <button type="button" class="secondary small-button" disabled={rows.length===1} onclick={()=>rows=rows.filter((_,i)=>i!==index)}>Remove terminal action {index+1}</button>
  </fieldset>
{/each}
<div class="actions"><button type="button" class="secondary small-button" disabled={rows.length>=1000} onclick={()=>rows=[...rows,emptyTerminal()]}>Add terminal action</button></div>

<style>
  fieldset.terminal {display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px;border:1px solid var(--border);border-radius:8px;padding:12px;min-width:0}
  legend {font-weight:600;padding:0 6px}
  .dataset-note {grid-column:1/-1}
  @media(max-width:640px){fieldset.terminal{grid-template-columns:minmax(0,1fr)}}
</style>
