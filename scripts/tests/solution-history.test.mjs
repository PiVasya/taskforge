import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import test from 'node:test';

const current = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(resolve(current, '../../apps/web/src/utils/solutionTimeline.js'), 'utf8');
const { mergeSolutionTimeline, timelineHasMore, timelineNextPageRequests } =
  await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

const liveSource = readFileSync(resolve(current, '../../apps/web/src/features/admin-solutions/adminSolutionLiveModel.js'), 'utf8');
const { filterLiveItemsByTab } = await import(`data:text/javascript;base64,${Buffer.from(liveSource).toString('base64')}`);

function attempt(id, time) { return { id, submittedAt: time }; }

test('all five histories merge newest first (including SQL)', () => {
  const merged = mergeSolutionTimeline({
    code: [attempt('c', '2026-10-10T09:00:00Z')],
    sql: [attempt('s', '2026-10-10T13:00:00Z')],
    tests: [{ attemptId: 't', submittedAt: '2026-10-10T12:00:00Z' }],
    images: [attempt('i', '2026-10-10T10:00:00Z')],
    math: [{ attemptId: 'm', submittedAt: '2026-10-10T11:00:00Z' }],
  });
  assert.deepEqual(merged.map(x => x.key), ['sql:s', 'tests:t', 'math:m', 'images:i', 'code:c']);
});

test('duplicates cannot appear when returning to cached history', () => {
  const merged = mergeSolutionTimeline({ code: [attempt('same', '2026-10-10'), attempt('same', '2026-10-10')] });
  assert.equal(merged.length, 1);
});

test('empty / missing dates never override dated entries', () => {
  const merged = mergeSolutionTimeline({ code: [attempt('missing', ''), attempt('recent', '2026-10-10')] });
  assert.deepEqual(merged.map(x => x.id), ['recent', 'missing']);
});

test('next 20-page fetches every type which could hide a more recent solution', () => {
  const twenty = Array.from({ length: 20 }, (_, i) => attempt(`c${i}`, '2026-10-10'));
  const forty = Array.from({ length: 40 }, (_, i) => attempt(`s${i}`, '2026-10-10'));
  const need = timelineNextPageRequests(
    { code: twenty, sql: forty, tests: [], images: [], math: [] },
    { code: true, sql: true, tests: false, images: false, math: false }, 40,
  );
  assert.deepEqual(need, ['code']);
  assert.equal(timelineHasMore(twenty.map((row) => ({ ...row })), 20, { code: true }), true);
  assert.equal(timelineHasMore(twenty, 20, { code: false }), false);
});

test('stable order when timestamps match', () => {
  const merged = mergeSolutionTimeline({ sql: [attempt('b', '2026-10-10')], code: [attempt('a', '2026-10-10')] });
  assert.deepEqual(merged.map(x => x.key), ['code:a', 'sql:b']);
});

test('admin real-time feed keeps SQL separate from code', () => {
  const items = [{ kind: 'sql', id: 's1' }, { kind: 'code', id: 'c1' }, { kind: 'test', id: 't1' }];
  assert.deepEqual(filterLiveItemsByTab(items, 'sql').map(x => x.id), ['s1']);
  assert.deepEqual(filterLiveItemsByTab(items, 'code').map(x => x.id), ['c1']);
  assert.equal(filterLiveItemsByTab(items, 'live').length, 3);
});
