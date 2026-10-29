import test from 'node:test';
import assert from 'node:assert/strict';
import {emptyTerminal,terminalCatalogJson} from '../src/raw-paper-terminals.ts';
const sha='a'.repeat(64);
const cash=()=>({...emptyTerminal(),listingId:'old',actionId:'exit',observationId:'observed-exit',
  entitlementAt:'2026-01-05T12:00:00Z',cutoffAvailableAt:'2026-01-05T12:00:00Z',cutoffSource:'urn:authored:cutoff',cutoffSha256:sha,
  sourceSettlementId:'settlement-1',settlementSource:'urn:authored:settlement',settlementSha256:sha,settledAt:'2026-01-10T12:00:00Z',settlementAvailableAt:'2026-01-10T12:00:00Z',
  fixedFeeGbp:'0.0100000000000000000000000001',currency:'USD',grossPerShare:'110',withheldPerShare:'10.000000000000000000000000001',fxObservationId:'fx-1'});
test('cash terms preserve exact decimal tokens and explicit actual deductions',()=>{
  const text=terminalCatalogJson([cash()],sha);assert.match(text,/"withheldPerShare":10\.000000000000000000000000001/);
  assert.match(text,/"fixedFeeGbp":0\.0100000000000000000000000001/);
  const term=JSON.parse(text)[0];assert.equal(term.delivery.successorListingId,null);assert.equal(term.delivery.booking.datasetSha256,sha);
});
test('stock terms preserve the ratio while omitting incompatible cash and FX',()=>{
  const row={...cash(),outcome:'stock',successorListingId:'next',successorSharesPerShare:'1.0000000000000000000000000001',currency:'invalid',withheldPerShare:'invalid'};
  const text=terminalCatalogJson([row],sha);assert.match(text,/"successorSharesPerShare":1\.0000000000000000000000000001/);
  const term=JSON.parse(text)[0];assert.equal(term.delivery.currency,null);assert.equal(term.delivery.grossPerShare,0);assert.equal(term.delivery.fxObservationId,null);
});
test('confirmed zero has no delivery and ignores previously edited settlement fields',()=>{
  const term=JSON.parse(terminalCatalogJson([{...cash(),outcome:'zero',fixedFeeGbp:'invalid',settledAt:'invalid'}],sha))[0];assert.equal(term.delivery,null);
});
test('no withholding, fee or delivery time is inferred',()=>{
  for(const field of ['withheldPerShare','fixedFeeGbp','settledAt','sourceSettlementId'])assert.throws(()=>terminalCatalogJson([{...cash(),[field]:''}],sha));
});
test('rejects negative, lossy, overflowing and injected amounts',()=>{
  for(const value of ['-1','1e-3','0.12345678901234567890123456789','79228162514264337593543950336','0,"other":1'])
    assert.throws(()=>terminalCatalogJson([{...cash(),fixedFeeGbp:value}],sha));
  assert.throws(()=>terminalCatalogJson([{...cash(),withheldPerShare:'111'}],sha));
});
test('requires bounded evidence, explicit UTC and currency',()=>{
  for(const [key,value] of [['settlementSha256','bad'],['cutoffSource','x'.repeat(201)],['settledAt','2026-01-10T12:00:00'],['currency','usd']])
    assert.throws(()=>terminalCatalogJson([{...cash(),[key]:value}],sha));
});
test('refuses duplicate actions and source settlement identifiers',()=>{
  assert.throws(()=>terminalCatalogJson([cash(),cash()],sha));
  assert.throws(()=>terminalCatalogJson([cash(),{...cash(),actionId:'other'}],sha));
});
test('stock needs a positive ratio and distinct successor; identifiers are JSON escaped',()=>{
  for(const patch of [{successorSharesPerShare:'0'},{successorListingId:'old'}])assert.throws(()=>terminalCatalogJson([{...cash(),outcome:'stock',successorListingId:'next',successorSharesPerShare:'2',...patch}],sha));
  const source='urn:authored:"quoted"\\source';assert.equal(JSON.parse(terminalCatalogJson([{...cash(),settlementSource:source}],sha))[0].delivery.sourceReference,source);
});
