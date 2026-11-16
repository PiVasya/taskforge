import test from 'node:test';
import assert from 'node:assert/strict';
import { splitLeaderboardEntries, getLeaderboardName, getLeaderboardLogin, getLeaderboardSolved, getLeaderboardRowRevealDelay, getLeaderboardPodiumIdentity } from '../../src/components/leaderboardPodiumModel.mjs';

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


test('each added page appears without the three long empty slots left by the top podium', () => {
  const entries = Array.from({ length: 48 }, (_, i) => ({ userId: String(i + 1), rank: i + 1 }));
  const { podium, regular } = splitLeaderboardEntries(entries);
  assert.equal(podium.length, 3);
  assert.equal(regular.length, 45);
  assert.equal(getLeaderboardRowRevealDelay(0, podium.length), 0);
  assert.equal(getLeaderboardRowRevealDelay(20, podium.length), 700);
  for (let index = 21; index < regular.length; index++) {
    assert.equal(getLeaderboardRowRevealDelay(index, podium.length), null, `new row at index ${index} must not be hidden`);
  }
  assert.equal(getLeaderboardRowRevealDelay(0, 0), 0);
  assert.equal(getLeaderboardRowRevealDelay(23, 0), 805);
  assert.equal(getLeaderboardRowRevealDelay(24, 0), null);
  assert.equal(getLeaderboardRowRevealDelay(-1, 3), null);
});

test('existing winners keep their GPU particle scene when another leaderboard page arrives', () => {
  const initial = [
    { rank: 2, userId: 'b' },
    { rank: 1, userId: 'a' },
    { rank: 3, userId: 'c' },
  ];
  const afterLoadingMore = initial.map((entry) => ({ ...entry }));
  assert.equal(getLeaderboardPodiumIdentity(initial), getLeaderboardPodiumIdentity(afterLoadingMore));
  assert.notEqual(getLeaderboardPodiumIdentity(initial), getLeaderboardPodiumIdentity([
    { rank: 2, userId: 'b' },
    { rank: 1, userId: 'other' },
    { rank: 3, userId: 'c' },
  ]));
});
