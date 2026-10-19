import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
const source = await readFile(new URL('../../src/features/cluster/clusterMetrics.js', import.meta.url), 'utf8');
const { metricHistory, metricsFresh } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
const now = Date.parse('2026-10-17T21:00:00Z');
const at = delta => new Date(now + delta * 1000).toISOString();

test('real zeros are shown; absent sensors and impossible values are not fabricated', () => {
  const result = metricHistory({ history: [{ at: at(-5), cpu_percent: 0, memory_percent: 52 }, { at: at(0), cpu_percent: 105, temperature_c: 572 }] }, now);
  assert.equal(result[0].cpu, 0);
  assert.equal(result[0].temperature, null);
  assert.equal(result[1].cpu, null);
  assert.equal(result[1].temperature, null);
});
test('missing time intervals break lines instead of inventing activity', () => {
  const result = metricHistory({ sample_interval_seconds: 5, history: [{ at: at(-60), cpu_percent: 25 }, { at: at(0), cpu_percent: 50 }] }, now);
  assert.equal(result.length, 3);
  assert.equal(result[1].cpu, null);
  assert.equal(result[1].time, now - 55000);
});
test('retention, ordering, deduplication and future timestamps are bounded', () => {
  const history = [{ at: at(-10000), cpu_percent: 1 }, { at: at(30), cpu_percent: 10 }, { at: at(0), cpu_percent: 70 }, { at: at(-5), cpu_percent: 20 }, { at: at(0), cpu_percent: 30 }];
  const points = metricHistory({ history }, now);
  assert.equal(points.length, 2);
  assert.equal(points[0].time, now - 5000);
  assert.equal(points[1].cpu, 30);
});
test('history is limited to one hour independently of page visibility', () => {
  const history = Array.from({ length: 1000 }, (_, i) => ({ at: at(-5000 + i * 5), cpu_percent: 5 }));
  assert.ok(metricHistory({ history }, now).length <= 720);
  assert.ok(metricHistory({ history }, now, 15).every(point => point.time >= now - 900000));
});
test('offline or stale sources never claim live measurements', () => {
  assert.equal(metricsFresh({ online: true, telemetryFresh: true, host: { sampled_at: at(-5) } }, now), true);
  assert.equal(metricsFresh({ online: true, host: { sampled_at: at(-100) } }, now), false);
  assert.equal(metricsFresh({ online: false, host: { sampled_at: at(0) } }, now), false);
  assert.equal(metricsFresh({ online: true, telemetryFresh: false, host: { sampled_at: at(0) } }, now), false);
  assert.equal(metricsFresh({ online: true }, now), false);
});
