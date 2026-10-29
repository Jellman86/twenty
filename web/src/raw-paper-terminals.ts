import {validateDecimal} from './raw-paper-dividends.ts';

export const terminalCommonFields = [
  ['listingId','Retiring listing ID',128], ['actionId','Terminal action ID',128], ['observationId','Terminal observation ID',128],
  ['entitlementAt','Terminal cutoff time (UTC)',40], ['cutoffAvailableAt','Terminal cutoff proof available (UTC)',40],
  ['cutoffSource','Terminal cutoff source reference',200], ['cutoffSha256','Terminal cutoff content SHA-256',64],
] as const;
export const terminalDeliveryFields = [
  ['sourceSettlementId','Source settlement ID',128], ['settlementSource','Settlement source reference',200],
  ['settlementSha256','Settlement content SHA-256',64], ['settledAt','Settled time (UTC)',40],
  ['settlementAvailableAt','Settlement available (UTC)',40], ['fixedFeeGbp','Settlement fee per entitled account (GBP)',58],
] as const;
export const terminalCashFields = [
  ['currency','Settlement currency',3], ['grossPerShare','Terminal gross per cutoff share',58],
  ['withheldPerShare','Terminal withholding per cutoff share',58], ['fxObservationId','Settlement FX observation ID',128],
] as const;
export const terminalStockFields = [
  ['successorListingId','Delivered successor listing ID',128], ['successorSharesPerShare','Successor shares per cutoff share',58],
] as const;
export const terminalClaimFields = [
  ['expectedFeeGbp','Expected terminal fee per entitled account (GBP)',58],
  ['expectedAvailableAt','Expected terminal policy available (UTC)',40],
  ['expectedSource','Expected terminal policy source reference',200], ['expectedSha256','Expected terminal policy content SHA-256',64],
] as const;
export const terminalCashClaimFields = [['expectedWithheldPerShare','Expected terminal withholding per cutoff share',58]] as const;
export type TerminalField = typeof terminalCommonFields[number][0] | typeof terminalDeliveryFields[number][0] | typeof terminalCashFields[number][0] | typeof terminalStockFields[number][0] | typeof terminalClaimFields[number][0] | typeof terminalCashClaimFields[number][0];
export type TerminalDraft = Record<TerminalField,string> & {outcome:'cash'|'stock'|'zero'};
export const emptyTerminal = ():TerminalDraft => ({...Object.fromEntries([...terminalCommonFields,...terminalDeliveryFields,...terminalCashFields,...terminalStockFields,...terminalClaimFields,...terminalCashClaimFields].map(([key])=>[key,''])),outcome:'cash'}) as TerminalDraft;
export const terminalAmount = (key:TerminalField) => ['grossPerShare','withheldPerShare','fixedFeeGbp','successorSharesPerShare','expectedWithheldPerShare','expectedFeeGbp'].includes(key);
const hash=/^[0-9a-f]{64}$/;
const utc=/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/;

export function terminalCatalogJson(drafts:TerminalDraft[],datasetSha256:string,withClaims=false):string {
  if(!hash.test(datasetSha256))throw new Error('Dataset SHA-256 must contain 64 lowercase hexadecimal characters.');
  if(drafts.length<1||drafts.length>1000)throw new Error('Supply at least one terminal action, up to 1,000.');
  const actions=new Set<string>(),settlements=new Set<string>();
  return '['+drafts.map((draft,index)=>{
    if(!['zero','cash','stock'].includes(draft.outcome))throw new Error('Choose a supported terminal outcome.');
    const row={...draft},delivers=row.outcome!=='zero';
    const fields=[...terminalCommonFields,...(delivers?terminalDeliveryFields:[]),...(row.outcome==='cash'?terminalCashFields:row.outcome==='stock'?terminalStockFields:[]),
      ...(withClaims&&delivers?terminalClaimFields:[]),...(withClaims&&row.outcome==='cash'?terminalCashClaimFields:[])];
    for(const [key,label,max] of fields){
      row[key]=row[key].trim();
      if((!row[key]&&key!=='fxObservationId')||row[key].length>max)throw new Error(`${label} is required and must fit its length limit.`);
      if(key.endsWith('Sha256')&&!hash.test(row[key]))throw new Error('Terminal evidence SHA-256 must contain 64 lowercase hexadecimal characters.');
      if(key.endsWith('At')&&(!utc.test(row[key])||!Number.isFinite(Date.parse(row[key]))))throw new Error('Terminal times must include seconds and an explicit UTC Z or +00:00 suffix.');
    }
    const key=JSON.stringify([row.listingId,row.actionId]);
    if(actions.has(key)||delivers&&settlements.has(row.sourceSettlementId))throw new Error('Duplicate terminal action or source settlement ID.');
    actions.add(key);if(delivers)settlements.add(row.sourceSettlementId);
    const header=JSON.stringify({listingId:row.listingId,actionId:row.actionId,observationId:row.observationId,evidence:{entitlementAt:row.entitlementAt,
      availableAt:row.cutoffAvailableAt,sourceReference:row.cutoffSource,contentSha256:row.cutoffSha256}}).slice(0,-1);
    if(!delivers)return header+',"delivery":null}';
    validateDecimal(row.fixedFeeGbp,'Settlement fee');
    let amounts:string;
    if(row.outcome==='cash'){
      if(!/^[A-Z]{3}$/.test(row.currency))throw new Error('Settlement currency must be a three-letter uppercase code.');
      const gross=validateDecimal(row.grossPerShare,'Terminal gross per cutoff share',true),withheld=validateDecimal(row.withheldPerShare,'Terminal withholding per cutoff share');
      if(withheld.coefficient*10n**BigInt(gross.scale)>gross.coefficient*10n**BigInt(withheld.scale))throw new Error('Terminal withholding cannot exceed gross.');
      amounts=`"currency":${JSON.stringify(row.currency)},"grossPerShare":${row.grossPerShare},"withheldPerShare":${row.withheldPerShare},"fixedFeeGbp":${row.fixedFeeGbp},`+
        '"fxObservationId":'+JSON.stringify(row.fxObservationId||null)+',"successorListingId":null,"successorSharesPerShare":0';
    }else{
      if(row.successorListingId===row.listingId)throw new Error('A stock delivery needs a distinct successor listing.');
      validateDecimal(row.successorSharesPerShare,'Successor shares per cutoff share',true);
      amounts=`"currency":null,"grossPerShare":0,"withheldPerShare":0,"fixedFeeGbp":${row.fixedFeeGbp},"fxObservationId":null,`+
        `"successorListingId":${JSON.stringify(row.successorListingId)},"successorSharesPerShare":${row.successorSharesPerShare}`;
    }
    let markPolicy='';
    if(withClaims){
      validateDecimal(row.expectedFeeGbp,'Expected terminal fee');
      if(row.outcome==='cash'){
        const gross=validateDecimal(row.grossPerShare,'Terminal gross per cutoff share',true),expected=validateDecimal(row.expectedWithheldPerShare,'Expected terminal withholding');
        if(expected.coefficient*10n**BigInt(gross.scale)>gross.coefficient*10n**BigInt(expected.scale))throw new Error('Expected terminal withholding cannot exceed gross.');
      }
      markPolicy=',"markPolicy":{"currency":'+JSON.stringify(row.outcome==='cash'?row.currency:null)+
        `,"expectedWithheldPerShare":${row.outcome==='cash'?row.expectedWithheldPerShare:'0'},"expectedFeeGbp":${row.expectedFeeGbp},`+
        JSON.stringify({availableAt:row.expectedAvailableAt,sourceReference:row.expectedSource,contentSha256:row.expectedSha256,
          allocationRule:'raw-study-claim-mark-per-share-fixed-gbp-fee-v1'}).slice(1);
    }
    const delivery={booking:{id:`paper-terminal-settlement-${index+1}`,tieOrder:index,datasetSha256},sourceSettlementId:row.sourceSettlementId,
      sourceReference:row.settlementSource,contentSha256:row.settlementSha256,settledAt:row.settledAt,availableAt:row.settlementAvailableAt};
    return header+',"delivery":'+JSON.stringify(delivery).slice(0,-1)+','+amounts+',"allocationRule":"exact-decimal-per-cutoff-share-fixed-gbp-fee-v1"}'+markPolicy+'}';
  }).join(',')+']';
}
