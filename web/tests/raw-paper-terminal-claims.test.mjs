import test from 'node:test';
import assert from 'node:assert/strict';
import {emptyTerminal,terminalCatalogJson} from '../src/raw-paper-terminals.ts';
const sha='a'.repeat(64);
const cash=()=>({...emptyTerminal(),listingId:'old',actionId:'exit',observationId:'exit-observation',outcome:'cash',
  entitlementAt:'2026-01-05T12:00:00Z',cutoffAvailableAt:'2026-01-05T12:00:00Z',cutoffSource:'urn:authored:cutoff',cutoffSha256:sha,
  sourceSettlementId:'settlement',settlementSource:'urn:authored:settlement',settlementSha256:sha,settledAt:'2026-02-05T12:00:00Z',settlementAvailableAt:'2026-02-05T12:00:00Z',
  currency:'USD',grossPerShare:'110',withheldPerShare:'10',fixedFeeGbp:'0.01',fxObservationId:'fx',expectedWithheldPerShare:'9.000000000000000000000000001',
  expectedFeeGbp:'0.0200000000000000000000000001',expectedAvailableAt:'2026-01-05T12:00:00Z',expectedSource:'urn:authored:expected',expectedSha256:'c'.repeat(64)});
test('cash expectations serialize exact independent decimal tokens',()=>{
  const text=terminalCatalogJson([cash()],sha,true);assert.match(text,/"expectedWithheldPerShare":9\.000000000000000000000000001/);
  assert.match(text,/"expectedFeeGbp":0\.0200000000000000000000000001/);
  const term=JSON.parse(text)[0];assert.equal(term.markPolicy.currency,'USD');assert.equal(term.delivery.withheldPerShare,10);
  assert.equal(term.markPolicy.sourceReference,'urn:authored:expected');
});
test('stock expectation carries only explicit fee policy and inapplicable null cash fields',()=>{
  const term=JSON.parse(terminalCatalogJson([{...cash(),outcome:'stock',successorListingId:'next',successorSharesPerShare:'2',expectedWithheldPerShare:'invalid'}],sha,true))[0];
  assert.equal(term.markPolicy.currency,null);assert.equal(term.markPolicy.expectedWithheldPerShare,0);assert.equal(term.markPolicy.allocationRule,'raw-study-claim-mark-per-share-fixed-gbp-fee-v1');
});
test('confirmed zero has no receivable policy even after editing invalid expectations',()=>{
  const term=JSON.parse(terminalCatalogJson([{...cash(),outcome:'zero',expectedFeeGbp:'invalid',expectedSha256:'invalid'}],sha,true))[0];
  assert.equal(term.delivery,null);assert.equal('markPolicy' in term,false);
});
test('expected fees, withholding and evidence are never inferred',()=>{
  for(const key of ['expectedWithheldPerShare','expectedFeeGbp','expectedAvailableAt','expectedSource','expectedSha256'])
    assert.throws(()=>terminalCatalogJson([{...cash(),[key]:''}],sha,true));
});
test('expected deductions and availability reject impossible or unbound inputs',()=>{
  for(const [key,value] of [['expectedWithheldPerShare','111'],['expectedFeeGbp','-0.01'],['expectedFeeGbp','1e-3'],['expectedAvailableAt','2026-01-05T12:00:00'],['expectedSha256','bad']])
    assert.throws(()=>terminalCatalogJson([{...cash(),[key]:value}],sha,true));
});
test('settled mode omits even invalid previously edited expectation fields',()=>{
  const baseline=terminalCatalogJson([cash()],sha);
  assert.equal(terminalCatalogJson([{...cash(),expectedWithheldPerShare:'bad',expectedFeeGbp:'bad',expectedAvailableAt:'bad'}],sha,false),baseline);
  assert.equal('markPolicy' in JSON.parse(baseline)[0],false);
});
