<script lang="ts">
  import {dividendFields,dividendClaimFields,emptyDividend,isAmount} from './raw-paper-dividends';
  import type {DividendDraft} from './raw-paper-dividends';
  let {rows=$bindable<DividendDraft[]>([emptyDividend()]),withClaims=false}=$props();
</script>

<p class="dataset-note">Supply one row for every dividend in the dataset. Amounts are per cutoff share; the GBP fee is charged once per entitled account. Times require seconds and UTC (Z or +00:00). Leave the FX ID empty only when no conversion is needed. Evidence and payment terms are fixed at creation.</p>
{#each rows as row,index}
  <fieldset class="dividend">
    <legend>Dividend {index+1}</legend>
    {#each dividendFields as [key,label,max]}
      <label>{label}<input bind:value={row[key]} maxlength={max} required={key!=='fxObservationId'} inputmode={isAmount(key)?'decimal':'text'} spellcheck="false" placeholder={key.endsWith('At')?'YYYY-MM-DDTHH:mm:ssZ':undefined}/></label>
    {/each}
    {#if withClaims}
      <p class="claim-note dataset-note">Expected deductions value unpaid income. They are separate from the payment amounts above and must have been available by the first unpaid mark. Estimated income cannot fund a purchase.</p>
      {#each dividendClaimFields as [key,label,max]}
        <label>{label}<input bind:value={row[key]} maxlength={max} required inputmode={isAmount(key)?'decimal':'text'} spellcheck="false" placeholder={key.endsWith('At')?'YYYY-MM-DDTHH:mm:ssZ':undefined}/></label>
      {/each}
    {/if}
    <button type="button" class="secondary small-button" disabled={rows.length===1} onclick={()=>rows=rows.filter((_,i)=>i!==index)}>Remove dividend {index+1}</button>
  </fieldset>
{/each}
<div class="actions"><button type="button" class="secondary small-button" disabled={rows.length>=1000} onclick={()=>rows=[...rows,emptyDividend()]}>Add dividend</button></div>

<style>
  fieldset.dividend {display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px;border:1px solid var(--border);border-radius:8px;padding:16px;min-width:0;margin:0}
  legend {font-weight:600;padding:0 6px}
  .claim-note {grid-column:1/-1;margin:0}
  input {min-width:0;width:100%;box-sizing:border-box}
  @media(max-width:600px){fieldset.dividend{grid-template-columns:minmax(0,1fr)}}
</style>
