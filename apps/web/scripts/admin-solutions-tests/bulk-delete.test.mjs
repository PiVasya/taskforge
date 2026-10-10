import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';

const source = await fs.readFile(new URL('../../src/features/admin-solutions/adminSolutionBulkDeleteModel.js', import.meta.url), 'utf8');
const model = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

const userId = '11111111-1111-4111-8111-111111111111';

test('all five solution histories support scoped deletion without sharing endpoints', () => {
  const expected = [
    ['code', 'code-history'],
    ['sql', 'sql-history'],
    ['tests', 'tests'],
    ['images', 'images'],
    ['math', 'math'],
  ];
  for (const [tab, query] of expected) {
    assert.deepEqual(model.bulkDeleteTarget(userId, tab), {
      userId, tab, ...model.BULK_DELETE_TABS[tab],
    });
    assert.equal(model.BULK_DELETE_TABS[tab].query, query);
  }
});

test('deletion is impossible without a selected user or on live/groups tabs', () => {
  for (const tab of ['code', 'sql', 'tests', 'images', 'math']) {
    assert.equal(model.bulkDeleteTarget('', tab), null);
    assert.equal(model.bulkDeleteTarget('   ', tab), null);
  }
  assert.equal(model.bulkDeleteTarget(userId, 'live'), null);
  assert.equal(model.bulkDeleteTarget(userId, 'groups'), null);
  assert.equal(model.bulkDeleteTarget(userId, '__proto__'), null);
});

test('confirmation identifies the exact account, type, and full history scope', () => {
  for (const tab of ['code', 'sql', 'tests', 'images', 'math']) {
    const target = model.bulkDeleteTarget(userId, tab);
    const message = model.bulkDeleteMessage(target, { email: 'student@example.test' });
    assert.match(message, /student@example\.test/);
    assert.ok(message.includes(userId));
    assert.ok(message.includes(target.label));
    assert.match(message, /за всё время/);
    assert.match(message, /необратимо/);
  }
  assert.equal(model.bulkDeleteMessage(null, {}), '');
});
