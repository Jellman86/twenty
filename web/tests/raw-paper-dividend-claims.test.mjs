import assert from 'node:assert/strict';
import {test} from 'node:test';
import {dividendCatalogJson,emptyDividend} from '../src/raw-paper-dividends.ts';

const sha='a'.repeat(64);
const draft=()=>({...emptyDividend(),listingId:'listing',actionId:'action',observationId:'observation',
  entitlementAt:'2026-01-02T12:00:00Z',cutoffAvailableAt:'2026-01-02T13:00:00Z',cutoffSource:'urn:cutoff',cutoffSha256:sha,
  sourcePaymentId:'payment',paymentSource:'urn:payment',paymentSha256:sha,paidAt:'2026-01-03T12:00:00Z',paymentAvailableAt:'2026-01-03T12:01:00Z',
  currency:'USD',grossPerShare:'2',withheldPerShare:'0.2',fixedFeeGbp:'0.01',fxObservationId:'fx',
  expectedWithheldPerShare:'0.5000000000000000000000000001',expectedFeeGbp:'0.0100000000000000000000000001',
  expectedAvailableAt:'2026-01-01T12:00:00Z',expectedSource:'urn:expected',expectedSha256:sha});

test('claims opt-in emits exact expected amounts and independent evidence',()=>{
  const text=dividendCatalogJson([draft()],sha,true);
  assert.match(text,/"expectedWithheldPerShare":0\.5000000000000000000000000001/);
  assert.match(text,/"expectedFeeGbp":0\.0100000000000000000000000001/);
  const row=JSON.parse(text);
  assert.equal(row[0].markPolicy.sourceReference,'urn:expected');
  assert.equal(row[0].markPolicy.availableAt,'2026-01-01T12:00:00Z');
  assert.equal(row[0].markPolicy.currency,'USD');
  assert.equal(row[0].markPolicy.allocationRule,'raw-study-claim-mark-per-share-fixed-gbp-fee-v1');
  assert.equal(row[0].delivery.withheldPerShare,0.2);
});

test('claims have no inferred withholding, fee or policy availability',()=>{
  for(const key of ['expectedWithheldPerShare','expectedFeeGbp','expectedAvailableAt','expectedSource','expectedSha256'])
    assert.throws(()=>dividendCatalogJson([{...draft(),[key]:''}],sha,true),/Expected/);
});

test('claims reject impossible deductions, invalid evidence and non-UTC time',()=>{
  for(const changes of [{expectedWithheldPerShare:'3'},{expectedWithheldPerShare:'-1'},{expectedFeeGbp:'1e-5'},
    {expectedFeeGbp:'-0.01'},{expectedSha256:'wrong'},{expectedAvailableAt:'2026-01-01T12:00:00'}])
    assert.throws(()=>dividendCatalogJson([{...draft(),...changes}],sha,true),/Expected/);
});

test('paid-within-month mode omits even previously edited invalid claim fields',()=>{
  const row={...draft(),expectedWithheldPerShare:'-1',expectedSha256:'invalid'};
  assert.equal(JSON.parse(dividendCatalogJson([row],sha))[0].markPolicy,undefined);
  assert.equal(JSON.parse(dividendCatalogJson([row],sha,false))[0].markPolicy,undefined);
});
