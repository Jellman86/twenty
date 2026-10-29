import assert from 'node:assert/strict';
import {test} from 'node:test';
import {dividendCatalogJson, emptyDividend} from '../src/raw-paper-dividends.ts';

const sha = 'a'.repeat(64);
const draft = () => ({...emptyDividend(), listingId:'listing', actionId:'action', observationId:'observation',
  entitlementAt:'2026-01-02T12:00:00Z', cutoffAvailableAt:'2026-01-02T13:00:00Z', cutoffSource:'urn:cutoff', cutoffSha256:sha,
  sourcePaymentId:'payment', paymentSource:'urn:payment', paymentSha256:sha, paidAt:'2026-01-03T12:00:00Z', paymentAvailableAt:'2026-01-03T12:01:00Z',
  currency:'USD', grossPerShare:'1.1234567890123456789012345678', withheldPerShare:'0.0000000000000000000000000001', fixedFeeGbp:'0.01', fxObservationId:'fx'});

test('serializes exact decimal tokens without a binary-number round trip', () => {
  const json = dividendCatalogJson([draft()], sha);
  assert.match(json, /"grossPerShare":1\.1234567890123456789012345678/);
  assert.match(json, /"withheldPerShare":0\.0000000000000000000000000001/);
  const row = JSON.parse(json)[0];
  assert.equal(row.delivery.booking.datasetSha256, sha);
  assert.equal(row.delivery.allocationRule, 'exact-decimal-per-cutoff-share-fixed-gbp-fee-v1');
  assert.equal(row.evidence.entitlementAt, draft().entitlementAt);
});

test('quotes identifiers safely and allows an explicit absent FX pin', () => {
  const row = JSON.parse(dividendCatalogJson([{...draft(), actionId:'quoted " action', fxObservationId:''}], sha))[0];
  assert.equal(row.actionId, 'quoted " action');
  assert.equal(row.delivery.fxObservationId, null);
});

test('rejects lossy, negative, unbounded and injected decimal inputs', () => {
  for (const grossPerShare of ['1e-5','NaN','Infinity','-1','0','0.00000000000000000000000000001','79228162514264337593543950336','1,"other":1','01'])
    assert.throws(() => dividendCatalogJson([{...draft(), grossPerShare}], sha), /Gross/);
  assert.throws(() => dividendCatalogJson([{...draft(), withheldPerShare:'2'}], sha), /Withheld/);
  assert.throws(() => dividendCatalogJson([{...draft(), fixedFeeGbp:'-0.01'}], sha), /Fixed fee/);
});

test('refuses incomplete, duplicate and non-UTC evidence before submission', () => {
  for (const changes of [{listingId:''},{cutoffSha256:'wrong'},{entitlementAt:'2026-01-02T12:00:00'}, {paidAt:'not a date'}, {currency:'usd'}])
    assert.throws(() => dividendCatalogJson([{...draft(), ...changes}], sha));
  assert.throws(() => dividendCatalogJson([draft(),draft()], sha), /Duplicate/);
  assert.throws(() => dividendCatalogJson([draft()], 'wrong'), /Dataset/);
  assert.throws(() => dividendCatalogJson([], sha), /at least one/);
});
