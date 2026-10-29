// Keep authored monetary inputs as text until they reach System.Decimal on the server.
export const dividendFields = [
  ['listingId','Listing ID',128], ['actionId','Action ID',128], ['observationId','Observation ID',128],
  ['entitlementAt','Entitlement time (UTC)',40], ['cutoffAvailableAt','Cutoff proof available (UTC)',40],
  ['cutoffSource','Cutoff source reference',200], ['cutoffSha256','Cutoff content SHA-256',64],
  ['sourcePaymentId','Source payment ID',128], ['paymentSource','Payment source reference',200], ['paymentSha256','Payment content SHA-256',64],
  ['paidAt','Paid time (UTC)',40], ['paymentAvailableAt','Payment available (UTC)',40], ['currency','Payment currency',3],
  ['grossPerShare','Gross per cutoff share',58], ['withheldPerShare','Withheld per cutoff share',58],
  ['fixedFeeGbp','Fixed fee per entitled account (GBP)',58], ['fxObservationId','Payment FX observation ID',128],
] as const;
export type DividendField = typeof dividendFields[number][0];
export type DividendDraft = Record<DividendField,string>;
export const emptyDividend = ():DividendDraft => Object.fromEntries(dividendFields.map(([key])=>[key,''])) as DividendDraft;
export const isAmount = (key:DividendField) => ['grossPerShare','withheldPerShare','fixedFeeGbp'].includes(key);
const hash = /^[0-9a-f]{64}$/;
const utc = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/;
const maximumDecimal = 79228162514264337593543950335n;

function decimal(text:string,label:string,positive=false) {
  if(!/^(?:0|[1-9]\d{0,28})(?:\.\d{1,28})?$/.test(text))throw new Error(`${label} must be an exact nonnegative decimal, without exponents.`);
  const [whole,fraction='']=text.split('.');
  const coefficient=BigInt(whole+fraction);
  if(coefficient>maximumDecimal||(positive&&coefficient===0n))throw new Error(`${label} is outside the supported decimal range.`);
  return {coefficient,scale:fraction.length};
}

export function dividendCatalogJson(rows:DividendDraft[],datasetSha256:string):string {
  if(!hash.test(datasetSha256))throw new Error('Dataset SHA-256 must contain 64 lowercase hexadecimal characters.');
  if(rows.length<1||rows.length>1000)throw new Error('Supply at least one dividend, up to 1,000.');
  const actions=new Set<string>(),payments=new Set<string>();
  return '['+rows.map((draft,index)=>{
    const row={...draft};
    for(const [key,label,max] of dividendFields){
      row[key]=row[key].trim();
      if((!row[key]&&key!=='fxObservationId')||row[key].length>max)throw new Error(`${label} is required and must fit its length limit.`);
    }
    for(const key of ['cutoffSha256','paymentSha256'] as const)
      if(!hash.test(row[key]))throw new Error('Evidence SHA-256 must contain 64 lowercase hexadecimal characters.');
    for(const key of ['entitlementAt','cutoffAvailableAt','paidAt','paymentAvailableAt'] as const)
      if(!utc.test(row[key])||!Number.isFinite(Date.parse(row[key])))throw new Error('Dividend times must include seconds and an explicit UTC Z or +00:00 suffix.');
    if(!/^[A-Z]{3}$/.test(row.currency))throw new Error('Payment currency must be a three-letter uppercase code.');
    const gross=decimal(row.grossPerShare,'Gross per cutoff share',true);
    const withheld=decimal(row.withheldPerShare,'Withheld per cutoff share');
    decimal(row.fixedFeeGbp,'Fixed fee');
    if(withheld.coefficient*10n**BigInt(gross.scale)>gross.coefficient*10n**BigInt(withheld.scale))throw new Error('Withheld per cutoff share cannot exceed gross.');
    const action=JSON.stringify([row.listingId,row.actionId]);
    if(actions.has(action)||payments.has(row.sourcePaymentId))throw new Error('Duplicate action or source payment ID.');
    actions.add(action);payments.add(row.sourcePaymentId);
    const evidence={entitlementAt:row.entitlementAt,availableAt:row.cutoffAvailableAt,sourceReference:row.cutoffSource,contentSha256:row.cutoffSha256};
    const delivery={booking:{id:`paper-income-payment-${index+1}`,tieOrder:index,datasetSha256},sourcePaymentId:row.sourcePaymentId,
      sourceReference:row.paymentSource,contentSha256:row.paymentSha256,paidAt:row.paidAt,availableAt:row.paymentAvailableAt,currency:row.currency};
    // Only the validated decimal grammar above is emitted unquoted. All other input is JSON-escaped.
    return JSON.stringify({listingId:row.listingId,actionId:row.actionId,observationId:row.observationId,evidence}).slice(0,-1)+
      ',"delivery":'+JSON.stringify(delivery).slice(0,-1)+`,"grossPerShare":${row.grossPerShare},"withheldPerShare":${row.withheldPerShare},"fixedFeeGbp":${row.fixedFeeGbp},`+
      '"fxObservationId":'+JSON.stringify(row.fxObservationId||null)+',"allocationRule":"exact-decimal-per-cutoff-share-fixed-gbp-fee-v1"}}';
  }).join(',')+']';
}
