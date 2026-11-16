import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../../src/features/task-test/latestTestRevision.js', import.meta.url), 'utf8');
const { currentTestFromConflict } = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

const latest = { attemptId: 'active-1', testRevision: '170000000', questions: [
  { id: 'current', promptContentJson: '{"type":"doc","content":[{"type":"image","attrs":{"src":"/api/files/new.png"}}]}' },
] };

test('a changed test replaces questions in the same attempt and keeps the rich images', () => {
  const error = { response: { status: 409, data: { code: 'TEST_UPDATED_RELOAD_REQUIRED', latestTest: latest } } };
  assert.deepEqual(currentTestFromConflict(error, 'active-1'), latest);
  assert.match(currentTestFromConflict(error, 'active-1').questions[0].promptContentJson, /"type":"image"/);
});

test('unrelated errors, other attempt IDs and incomplete server responses are not treated as updates', () => {
  const fail = (attemptId, data, status = 409) => ({ response: { status, data: { code: 'TEST_UPDATED_RELOAD_REQUIRED', latestTest: data } } });
  assert.equal(currentTestFromConflict(fail('active-1', latest), 'another-id'), null);
  assert.equal(currentTestFromConflict(fail('active-1', { ...latest, questions: undefined }), 'active-1'), null);
  assert.equal(currentTestFromConflict(fail('active-1', latest, 500), 'active-1'), null);
  assert.equal(currentTestFromConflict(null, 'active-1'), null);
});
