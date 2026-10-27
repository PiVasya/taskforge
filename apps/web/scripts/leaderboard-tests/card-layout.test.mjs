import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { measureLeaderboardMarquee } from '../../src/components/leaderboardMarqueeMotion.mjs';

const card = await readFile(new URL('../../src/components/LeaderboardCard.jsx', import.meta.url), 'utf8');
const page = await readFile(new URL('../../src/pages/admin/LeaderboardPage.jsx', import.meta.url), 'utf8');
const marquee = await readFile(new URL('../../src/components/LeaderboardMarquee.jsx', import.meta.url), 'utf8');
const marqueeCss = await readFile(new URL('../../src/components/LeaderboardMarquee.css', import.meta.url), 'utf8');

test('entire leaderboard card opens the profile without a redundant statistics link', () => {
  assert.match(card, /<button[\s\S]*?onClick=\{handleOpenProfile\}/);
  assert.match(card, /nav\(`\/users\/\$\{entry\.userId\}`\)/);
  assert.doesNotMatch(card, /Смотреть статистику|Открыть статистику/);
});

test('all leaderboard cards are exactly 128px tall independent of text, city, education and badges', () => {
  assert.match(page, /grid auto-rows-\[128px\] items-stretch gap-3 md:grid-cols-2/);
  assert.match(page, /className="tf-reveal-item h-\[128px\] min-w-0"/);
  assert.match(card, /className="group relative w-full h-\[128px\]/);
  assert.doesNotMatch(card, /min-h-\[|h-full|min-h-full|mt-auto/);
  assert.match(card, /h-6 min-w-0 font-semibold/);
  assert.match(card, /h-\[18px\] min-w-0 text-xs/);
  assert.match(card, /h-\[26px\] min-w-0 text-xs/);
});

test('name and badges, login/location/education and stats are three nonwrapping marquee lines', () => {
  assert.equal((card.match(/<LeaderboardMarquee/g) || []).length, 3);
  assert.match(card, /\{name\}[\s\S]*\{hasBadges &&/);
  assert.match(card, /\{showLogin &&[\s\S]*\{entry\.location &&[\s\S]*\{entry\.education &&/);
  assert.match(card, /gap-3 whitespace-nowrap/);
  assert.match(card, /\{solved\}[\s\S]*\{entry\.totalAttempts\}[\s\S]*\{entry\.lastSubmitAt &&/);
  assert.doesNotMatch(card, /flex-wrap|break-words/);
});

test('marquee stays still when text fits and animates only for an actual overflow', () => {
  assert.deepEqual(measureLeaderboardMarquee(150, 180), { overflow: false, distance: 0, duration: 0 });
  assert.deepEqual(measureLeaderboardMarquee(180, 180), { overflow: false, distance: 0, duration: 0 });
  assert.deepEqual(measureLeaderboardMarquee(181, 180), { overflow: false, distance: 0, duration: 0 });
  const animated = measureLeaderboardMarquee(480, 180);
  assert.equal(animated.overflow, true);
  assert.equal(animated.distance, 508);
  assert.ok(animated.duration > 9 && animated.duration < 40);
  assert.equal(measureLeaderboardMarquee(50000, 120).duration, 40);
});

test('marquee handles hidden/invalid viewport measurements without starting an animation', () => {
  assert.equal(measureLeaderboardMarquee(500, 0).overflow, false);
  assert.equal(measureLeaderboardMarquee(Number.NaN, 180).overflow, false);
  assert.equal(measureLeaderboardMarquee(300, Number.POSITIVE_INFINITY).overflow, false);
});

test('marquee tracks resizing, keeps the duplicated copy hidden from screen readers and pauses on hover', () => {
  assert.match(marquee, /new ResizeObserver\(measure\)/);
  assert.match(marquee, /observer\.observe\(viewport\)/);
  assert.match(marquee, /observer\.observe\(content\)/);
  assert.match(marquee, /window\.addEventListener\('resize', measure\)/);
  assert.match(marquee, /metrics\.overflow &&[\s\S]*aria-hidden="true"/);
  assert.match(marqueeCss, /data-overflow="true"\] \.tf-leaderboard-marquee__track/);
  assert.match(marqueeCss, /\.tf-leaderboard-marquee:hover \.tf-leaderboard-marquee__track/);
  assert.match(marqueeCss, /@media \(prefers-reduced-motion: reduce\)/);
  assert.match(marqueeCss, /overflow-x: auto;/);
});
