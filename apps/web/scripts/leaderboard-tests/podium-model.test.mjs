import test from 'node:test';
import assert from 'node:assert/strict';
import { splitLeaderboardEntries, getLeaderboardName, getLeaderboardLogin, getLeaderboardSolved } from '../../src/components/leaderboardPodiumModel.mjs';

test('three ranked users are separated and shown in levitation display in 2-1-3 order', () => {
  const entries = Array.from({ length: 24 }, (_, i) => ({ userId: String(i + 1), rank: i + 1 }));
  const { podium, regular } = splitLeaderboardEntries(entries);
  assert.deepEqual(podium.map((entry) => entry.rank), [2, 1, 3]);
  assert.deepEqual(regular.map((entry) => entry.rank), Array.from({ length: 21 }, (_, i) => i + 4));
  assert.equal(new Set([...podium, ...regular]).size, entries.length);
});

test('missing top three or short search results stay in the normal leaderboard', () => {
  const inputs = [[], [{rank: 1}], [{rank: 1}, {rank: 2}], [{rank: 2}, {rank: 3}, {rank: 4}], [{rank: 10}, {rank: 11}, {rank: 12}]];
  for (const entries of inputs) {
    const result = splitLeaderboardEntries(entries);
    assert.deepEqual(result.podium, []);
    assert.equal(result.regular, entries);
  }
});

test('leaderboard data uses server names, logins, and solved counts including zero', () => {
  assert.equal(getLeaderboardName({ displayName: 'София', login: 'sofia' }), 'София');
  assert.equal(getLeaderboardName({ login: 'sophia' }), 'sophia');
  assert.equal(getLeaderboardLogin({ userLogin: 'Maria' }), 'Maria');
  assert.equal(getLeaderboardSolved({ solvedAssignments: 0, solvedCount: 100 }), 0);
  assert.equal(getLeaderboardSolved({ solvedCount: 12 }), 12);
});
