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

test('tab filters preserve code plus SQL and isolate other task types', () => {
  assert.deepEqual(model.filterLiveItemsByTab(items, 'code').map(x => x.itemId), ['1', '2']);
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
