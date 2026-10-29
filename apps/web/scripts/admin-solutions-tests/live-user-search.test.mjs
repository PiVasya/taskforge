import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';

const source = await fs.readFile(new URL('../../src/features/admin-solutions/adminSolutionLiveModel.js', import.meta.url), 'utf8');
const search = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

const now = Date.parse('2026-10-29T01:00:00Z');
const live = [
  { key: 'code:1', userId: 'val1', kind: 'code', userLabel: 'Валерий Иванов', user: { id: 'val1', firstName: 'Валерий', lastName: 'Иванов', login: 'vanya', email: 'valeri@example.test' }, occurredAtUtc: '2026-10-29T00:59:00Z' },
  { key: 'sql:2', userId: 'val2', kind: 'sql', userLabel: 'Валентина', user: { firstName: 'Валентина', login: 'val2' }, occurredAtUtc: '2026-10-29T00:58:00Z' },
  { key: 'code:3', userId: 'petr', kind: 'code', userLabel: 'Пётр Смирнов', user: { id: 'petr', firstName: 'Пётр', lastName: 'Смирнов', email: 'petr@example.test' }, occurredAtUtc: '2026-10-29T00:57:00Z' },
  { key: 'test:4', userId: 'val1', kind: 'test', userLabel: 'Валерий Иванов', user: { id: 'val1', firstName: 'Валерий', lastName: 'Иванов' }, occurredAtUtc: '2026-10-28T00:00:00Z' },
];

test('typing Вале immediately finds all matching realtime users without selecting a UUID', () => {
  assert.deepEqual(search.filterLiveItems(live, { searchText: 'Вале', now }).map(item => item.key), ['code:1', 'sql:2', 'test:4']);
});

test('search is case insensitive, includes account login/email and accepts е instead of ё', () => {
  assert.deepEqual(search.filterLiveItems(live, { searchText: 'ПЕТР', now }).map(item => item.key), ['code:3']);
  assert.deepEqual(search.filterLiveItems(live, { searchText: 'example.test', now }).map(item => item.key), ['code:1', 'code:3', 'test:4']);
  assert.deepEqual(search.filterLiveItems(live, { searchText: 'VAL2', now }).map(item => item.key), ['sql:2']);
  assert.equal(search.normalizeUserSearch('  ПёТР   Смирнов  '), 'петр смирнов');
});

test('the search does not match assignment titles or unrelated event text', () => {
  const enriched = live.map(item => ({ ...item, assignmentTitle: 'Вале и Петя' }));
  assert.deepEqual(search.filterLiveItems(enriched, { searchText: 'Женя', now }), []);
});

test('selected user overrides free text and composes with group and period', () => {
  assert.deepEqual(search.filterLiveItems(live, { userId: 'petr', searchText: 'Вале', now }).map(item => item.key), ['code:3']);
  assert.deepEqual(search.filterLiveItems(live, { searchText: 'Вале', groupId: 'g', groupUserIds: ['val2'], now }).map(item => item.key), ['sql:2']);
  assert.deepEqual(search.filterLiveItems(live, { searchText: 'Вале', filterDays: 1, now }).map(item => item.key), ['code:1', 'sql:2']);
});

test('dropdown suggestions are deduplicated by real IDs and keep identity on incomplete user DTOs', () => {
  const suggestions = search.liveUserSuggestions(live, 'Вале');
  assert.deepEqual(suggestions.map(user => user.id), ['val1', 'val2']);
  assert.equal(suggestions[1].label, 'Валентина');
  assert.equal(suggestions[1].user.id, 'val2');
  assert.deepEqual(search.liveUserSuggestions(live, ''), []);
});

test('server candidates filter and do not accidentally include an unrelated cached user', () => {
  const rows = live.map(item => item.user).filter(user => user?.id);
  assert.deepEqual(search.searchedUserSuggestions(rows, 'вале').map(user => user.id), ['val1']);
  assert.deepEqual(search.searchedUserSuggestions(rows, 'not-found'), []);
});

test('fallback name from SSE is searchable even when the user API is temporarily unavailable', () => {
  const event = { userId: 'fallback', userLabel: 'Валерия Н.', occurredAtUtc: '2026-10-29T00:54:00Z' };
  assert.deepEqual(search.filterLiveItems([event], { searchText: 'ВАЛЕ', now }), [event]);
  assert.deepEqual(search.liveUserSuggestions([event], 'ВАЛЕ').map(item => item.id), ['fallback']);
});


test('the Cyrillic й survives normalization; ё and Latin accent marks still fold', () => {
  assert.equal(search.normalizeUserSearch('  ВАЛЕРИЙ  '), 'валерий');
  assert.equal(search.normalizeUserSearch('  ПЁТР  '), 'петр');
  assert.equal(search.normalizeUserSearch('Jóse'), 'jose');
});

test('adjacent swap, one missing letter and one wrong letter use bounded edit distance', () => {
  assert.equal(search.boundedUserEditDistance('валреий', 'валерий', 1), 1);
  assert.equal(search.boundedUserEditDistance('валрий', 'валерий', 1), 1);
  assert.equal(search.boundedUserEditDistance('валетий', 'валерий', 1), 1);
  assert.equal(search.boundedUserEditDistance('саша', 'валерий', 2), 3);
  assert.equal(search.userMatchesSearch(live[0].user, 'Валреий'), true);
  assert.equal(search.userMatchesSearch(live[0].user, 'Валрий'), true);
  assert.equal(search.userMatchesSearch(live[0].user, 'Валетий'), true);
});

test('Russian QWERTY and English transliteration resolve to the same identity', () => {
  for (const text of ['Dfkthbq', 'Valeriy', 'Вaлерий']) {
    assert.equal(search.userMatchesSearch(live[0].user, text), true, text);
    assert.equal(search.filterLiveItems(live, { searchText: text, now }).length, 2);
  }
  assert.equal(search.rankUserMatch(live[0].user, 'Dfkthbq').kind, 'layout');
  assert.equal(search.rankUserMatch(live[0].user, 'Valeriy').kind, 'translit');
  assert.equal(search.rankUserMatch(live[0].user, 'Вaлерий').kind, 'mixed');
});

test('reversed name order and several incomplete words must all match a user', () => {
  const user = { firstName: 'Валерий', middleName: 'Петрович', lastName: 'Иванов', login: 'valera' };
  for (const text of ['Вал Ив', 'Иванов Вал', 'Вал Петр Иван', 'Ivanov Valeriy']) {
    assert.equal(search.userMatchesSearch(user, text), true, text);
  }
  assert.equal(search.userMatchesSearch(user, 'Вал Сидор'), false);
});

test('exact and prefix matches rank above typo matches; suggestions ignore feed order', () => {
  const users = [
    { id: '1', firstName: 'Валреий' },
    { id: '2', firstName: 'Валерий' },
    { id: '3', firstName: 'Валентина' },
    { id: '4', firstName: 'Пётр' },
  ];
  const byName = search.searchedUserSuggestions(users, 'Валерий');
  assert.equal(byName[0].id, '2');
  assert.equal(byName.at(-1).id, '1');
  assert.equal(search.searchedUserSuggestions(users, 'Вале', 1).length, 1);
  assert.deepEqual(search.searchedUserSuggestions(users, '').map(user => user.id), []);
});

test('one- and two-letter queries do not produce fuzzy noise', () => {
  const users = [
    { id: '1', firstName: 'Валерий' },
    { id: '2', firstName: 'Ваня' },
    { id: '3', firstName: 'Саша' },
  ];
  assert.deepEqual(search.searchedUserSuggestions(users, 'ва').map(user => user.id), ['2', '1']);
  assert.deepEqual(search.searchedUserSuggestions(users, 'сл').map(user => user.id), []);
});

test('an email is searchable but its misspelling is not fuzzy-matched to another identity', () => {
  const account = { id: 'user-1', firstName: 'Валерий', email: 'valeriy@example.test' };
  assert.equal(search.userMatchesSearch(account, 'example.test'), true);
  assert.equal(search.userMatchesSearch(account, 'valeriy@example.test'), true);
  assert.equal(search.userMatchesSearch(account, 'example.tets'), false);
});

test('UUID searches are exact or prefix-only, not fuzzy', () => {
  const user = { id: '12af3456-1234-5678-9abc-123456789abc', firstName: 'Валерий' };
  assert.ok(search.rankUserMatch(user, '12af3456-1234').score >= 900);
  assert.equal(search.rankUserMatch(user, '12af3456-1234-5678-9abc-123456789abc').kind, 'exact');
  assert.equal(search.userMatchesSearch(user, '12af3456-1235'), false);
});

test('alternate server lookup plans stay bounded and do not request an entire directory', () => {
  assert.ok(search.userSearchServerQueries('Dfkthbq').includes('валерий'));
  assert.ok(search.userSearchServerQueries('Valeriy').includes('валерий'));
  assert.ok(search.userSearchServerQueries('Валреий').includes('вал'));
  assert.deepEqual(search.userSearchServerQueries('example.test'), ['example.test']);
  assert.deepEqual(search.userSearchServerQueries('a'), []);
  assert.ok(search.userSearchServerQueries('longname').length <= 4);
});

test('120 live events remain searchable without a backend request', () => {
  const items = Array.from({ length: 120 }, (_, i) => ({
    key: `code:${i}`, userId: `u${i}`, user: { firstName: `Валерий${i}`, lastName: 'Иванов' },
  }));
  assert.equal(search.filterLiveItems(items, { searchText: 'Dfkthbq' }).length, 120);
  assert.equal(search.liveUserSuggestions(items, 'Dfkthbq', 10).length, 10);
});
