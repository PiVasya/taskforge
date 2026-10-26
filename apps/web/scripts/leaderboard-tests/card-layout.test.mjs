import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const card = await readFile(new URL('../../src/components/LeaderboardCard.jsx', import.meta.url), 'utf8');
const page = await readFile(new URL('../../src/pages/admin/LeaderboardPage.jsx', import.meta.url), 'utf8');

test('leaderboard cards remain fully clickable but do not contain a redundant statistics link', () => {
  assert.match(card, /<button[\s\S]*?onClick=\{handleOpenProfile\}/);
  assert.match(card, /nav\(`\/users\/\$\{entry\.userId\}`\)/);
  assert.doesNotMatch(card, /Смотреть статистику|Открыть статистику/);
});

test('all leaderboard rows and cards stretch to equal height without truncating profile details', () => {
  assert.match(page, /grid auto-rows-fr items-stretch gap-4 md:grid-cols-2/);
  assert.match(page, /key=\{e\.userId \|\| index\} className="tf-reveal-item h-full"/);
  assert.match(card, /className="group relative w-full h-full min-h-\[160px\]/);
  assert.match(card, /className="flex-1 min-w-0 flex flex-col gap-1"/);
  assert.match(card, /className="mt-auto pt-2 flex flex-wrap gap-2 text-xs"/);
  assert.match(card, /\{entry\.education\}/);
  assert.match(card, /\{entry\.location\}/);
  assert.match(card, /\{name\}/);
});
