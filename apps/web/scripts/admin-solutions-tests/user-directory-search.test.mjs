import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';

const modelCode = await fs.readFile(new URL('../../src/features/admin-solutions/adminSolutionLiveModel.js', import.meta.url), 'utf8');
const modelUrl = `data:text/javascript;base64,${Buffer.from(modelCode).toString('base64')}`;
let handler = () => [];
const requests = [];
globalThis.__taskforgeTestFindUsers = async (query, take) => {
  requests.push({ query, take });
  return handler(query);
};
const lookupCode = (await fs.readFile(new URL('../../src/features/admin-solutions/adminSolutionUserLookup.js', import.meta.url), 'utf8'))
  .replace("import { searchUsersOnce } from '../../api/admin';", 'const searchUsersOnce = globalThis.__taskforgeTestFindUsers;')
  .replace("from './adminSolutionLiveModel';", `from '${modelUrl}';`);
const { searchAdminUsersSmart } = await import(`data:text/javascript;base64,${Buffer.from(lookupCode).toString('base64')}`);
const reset = (nextHandler) => { requests.length = 0; handler = nextHandler; };

test('server is never queried from an empty or one-letter historical search', async () => {
  reset(() => { throw Error('should not call'); });
  assert.deepEqual(await searchAdminUsersSmart(''), []);
  assert.deepEqual(await searchAdminUsersSmart('A'), []);
  assert.equal(requests.length, 0);
});

test('a strong exact server match is sufficient and avoids redundant alternate calls', async () => {
  reset((q) => q === 'Валерий' ? [{ id: '1', firstName: 'Валерий' }] : []);
  assert.deepEqual((await searchAdminUsersSmart('Валерий')).map((x) => x.id), ['1']);
  assert.deepEqual(requests.map((r) => r.query), ['Валерий']);
});

test('wrong keyboard layout is recovered with the corrected name and exact UUID', async () => {
  reset((q) => q === 'валерий' ? [{ id: 'id-1', firstName: 'Валерий' }] : []);
  assert.deepEqual((await searchAdminUsersSmart('Dfkthbq')).map((x) => x.id), ['id-1']);
  assert.deepEqual(requests.map((r) => r.query), ['Dfkthbq', 'валерий']);
});

test('a misspelled surname uses bounded prefix fallback; unrelated users are discarded', async () => {
  reset((q) => q === 'вал' ? [
    { id: '1', firstName: 'Валерий' }, { id: '2', firstName: 'Пётр' },
  ] : []);
  assert.deepEqual((await searchAdminUsersSmart('Валреий')).map((x) => x.id), ['1']);
  assert.ok(requests.length <= 4);
  assert.ok(requests.every((r) => r.take === 30));
});

test('optional fallback errors do not discard users found by a primary query', async () => {
  reset((q) => {
    if (q === 'valeriy') return [{ id: 'user-1', firstName: 'Valery' }];
    throw Error('optional lookup failed');
  });
  assert.deepEqual((await searchAdminUsersSmart('valeriy')).map((x) => x.id), ['user-1']);
});

test('primary API failures are not swallowed', async () => {
  reset(() => { throw Error('API unavailable'); });
  await assert.rejects(searchAdminUsersSmart('Валерий'), /API unavailable/);
});
