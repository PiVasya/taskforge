import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';

const source = await fs.readFile(new URL('../../src/features/admin-solutions/adminSolutionLiveModel.js', import.meta.url), 'utf8');
const model = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

const now = Date.parse('2026-09-14T20:00:00Z');
const items = [
  { itemId: '1', userId: 'u1', kind: 'code', occurredAtUtc: '2026-09-14T19:59:00Z' },
  { itemId: '2', userId: 'u2', kind: 'sql', occurredAtUtc: '2026-09-14T19:58:00Z' },
  { itemId: '3', userId: 'u3', kind: 'test', occurredAtUtc: '2026-09-10T12:00:00Z' },
  { itemId: '4', userId: 'u2', kind: 'math', occurredAtUtc: '2026-09-14T19:57:00Z' },
];

test('live feed user filter returns only the selected user', () => {
  assert.deepEqual(model.filterLiveItems(items, { userId: 'u2', now }).map(x => x.itemId), ['2', '4']);
});

test('live feed group filter uses the selected group membership', () => {
  assert.deepEqual(model.filterLiveItems(items, { groupId: 'g1', groupUserIds: ['u1', 'u3'], now }).map(x => x.itemId), ['1', '3']);
});

test('user and group filters compose instead of widening the feed', () => {
  assert.deepEqual(model.filterLiveItems(items, { userId: 'u2', groupId: 'g1', groupUserIds: ['u1', 'u2'], now }).map(x => x.itemId), ['2', '4']);
  assert.deepEqual(model.filterLiveItems(items, { userId: 'u3', groupId: 'g1', groupUserIds: ['u1', 'u2'], now }), []);
});

test('period filter excludes old live events', () => {
  assert.deepEqual(model.filterLiveItems(items, { filterDays: 1, now }).map(x => x.itemId), ['1', '2', '4']);
});

test('tab filters show code and SQL separately without hiding either from the live feed', () => {
  assert.deepEqual(model.filterLiveItemsByTab(items, 'live').map(x => x.itemId), ['1', '2', '3', '4']);
  assert.deepEqual(model.filterLiveItemsByTab(items, 'code').map(x => x.itemId), ['1']);
  assert.deepEqual(model.filterLiveItemsByTab(items, 'sql').map(x => x.itemId), ['2']);
  assert.deepEqual(model.filterLiveItemsByTab(items, 'tests').map(x => x.itemId), ['3']);
  assert.deepEqual(model.filterLiveItemsByTab(items, 'math').map(x => x.itemId), ['4']);
});

test('live labels are user-facing and stable', () => {
  assert.equal(model.solutionLiveStateLabel('live'), 'В эфире');
  assert.equal(model.solutionLiveStateLabel('reconnecting'), 'Переподключение');
  assert.equal(model.solutionKindLabel('sql'), 'SQL');
  assert.equal(model.solutionStatusLabel('Accepted'), 'Принято');
  assert.equal(model.solutionStatusLabel('RuntimeError'), 'Ошибка выполнения');
});

test('live feed recognizes only unresolved code verdicts as pending', () => {
  assert.equal(model.isPendingLiveStatus('Preparing'), true);
  assert.equal(model.isPendingLiveStatus('Queued'), true);
  assert.equal(model.isPendingLiveStatus('Running'), true);
  assert.equal(model.isPendingLiveStatus('Accepted'), false);
  assert.equal(model.isPendingLiveStatus('JudgeUnavailable'), false);
});

test('a missed terminal SSE event can be reconciled from persisted solution detail', () => {
  const old = { key: 'code:abc', status: 'Queued', score: 0 };
  const detail = { status: 'Accepted', score: 100, submittedCode: 'print(3)' };
  assert.deepEqual(model.reconcileLiveDetail(old, detail), { ...old, detail, status: 'Accepted', score: 100 });
});

test('late pending responses cannot downgrade an accepted card', () => {
  const accepted = { key: 'code:abc', status: 'Accepted', score: 100 };
  assert.strictEqual(model.reconcileLiveDetail(accepted, { status: 'Queued', score: 0 }), accepted);
});

test('pending SSE after a resolved card preserves the persisted verdict', () => {
  const final = { key: 'code:abc', status: 'Accepted', score: 100, detail: { status: 'Accepted' } };
  const delayed = { key: 'code:abc', status: 'Queued', score: 0, detail: { status: 'Queued' } };
  assert.deepEqual(model.mergeLiveEvent(final, delayed), { ...delayed, status: 'Accepted', score: 100, detail: final.detail });
});

test('a delayed infrastructure failure does not downgrade a real code verdict', () => {
  const final = { kind: 'code', key: 'code:abc', status: 'Accepted', score: 100, detail: { status: 'Accepted' } };
  const stale = { kind: 'code', key: 'code:abc', status: 'JudgeUnavailable', score: 0 };
  assert.deepEqual(model.mergeLiveEvent(final, stale), { ...stale, status: 'Accepted', score: 100, detail: final.detail });
});

test('a real verdict can upgrade an infrastructure failure', () => {
  const weak = { kind: 'code', key: 'code:abc', status: 'JudgeUnavailable', score: 0 };
  const final = { kind: 'code', key: 'code:abc', status: 'Accepted', score: 100 };
  assert.deepEqual(model.mergeLiveEvent(weak, final), final);
});

test('a code terminal verdict cannot be overwritten by a stale conflicting terminal event', () => {
  const final = { kind: 'code', key: 'code:abc', status: 'Accepted', score: 100 };
  const stale = { kind: 'code', key: 'code:abc', status: 'CompileError', score: 0 };
  assert.deepEqual(model.mergeLiveEvent(final, stale), { ...stale, status: 'Accepted', score: 100, detail: undefined });
});
