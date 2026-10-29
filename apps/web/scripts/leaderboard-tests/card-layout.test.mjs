import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { measureLeaderboardMarquee } from '../../src/components/leaderboardMarqueeMotion.mjs';

const card = await readFile(new URL('../../src/components/LeaderboardCard.jsx', import.meta.url), 'utf8');
const page = await readFile(new URL('../../src/pages/admin/LeaderboardPage.jsx', import.meta.url), 'utf8');
const marquee = await readFile(new URL('../../src/components/LeaderboardMarquee.jsx', import.meta.url), 'utf8');
const marqueeCss = await readFile(new URL('../../src/components/LeaderboardMarquee.css', import.meta.url), 'utf8');
const layoutCss = await readFile(new URL('../../src/components/LeaderboardCard.css', import.meta.url), 'utf8');

test('entire leaderboard card opens the profile without a redundant statistics link', () => {
  assert.match(card, /<button[\s\S]*?onClick=\{handleOpenProfile\}/);
  assert.match(card, /nav\(`\/users\/\$\{entry\.userId\}`\)/);
  assert.doesNotMatch(card, /Смотреть статистику|Открыть статистику/);
});

test('card height is avatar + exactly 5px at top and bottom, in every row and skeleton', () => {
  const getPixel = (name) => {
    const match = layoutCss.match(new RegExp(`--tf-leaderboard-${name}:\\s*(\\d+)px;`));
    assert.ok(match, `missing fixed pixel token ${name}`);
    return Number(match[1]);
  };
  const avatar = getPixel('avatar-size');
  const padding = getPixel('card-padding');
  const stroke = getPixel('card-stroke');
  const corner = getPixel('card-radius');
  assert.equal(avatar, 64);
  assert.equal(padding + stroke, 5);
  assert.equal(avatar + 2 * (padding + stroke), 74);
  assert.equal(corner - (padding + stroke), 11);
  assert.match(layoutCss, /--tf-leaderboard-card-height: calc\(/);
  assert.match(layoutCss, /--tf-leaderboard-avatar-radius: calc\(/);
  assert.match(layoutCss, /grid-auto-rows: var\(--tf-leaderboard-card-height\)/);
  assert.match(layoutCss, /height: var\(--tf-leaderboard-card-height\)/);
  assert.match(layoutCss, /height: var\(--tf-leaderboard-avatar-size\)/);
  assert.match(page, /className="tf-leaderboard-grid grid items-stretch gap-3 md:grid-cols-2 xl:grid-cols-3"/);
  assert.match(page, /className="tf-leaderboard-grid grid gap-3 md:grid-cols-2 xl:grid-cols-3"/);
  assert.match(page, /className="tf-leaderboard-skeleton tf-reveal-item/);
  assert.match(card, /className="tf-leaderboard-card group relative w-full/);
  assert.doesNotMatch(card + page, /128px|auto-rows-fr|mt-auto|min-h-\[/);
});

test('all three text lines fit inside 64px without wrapping, enlarging or clipping the card', () => {
  assert.match(layoutCss, /\.tf-leaderboard-info \{ gap: 2px; \}/);
  assert.match(layoutCss, /\.tf-leaderboard-name \{ height: 22px; line-height: 22px; \}/);
  assert.match(layoutCss, /\.tf-leaderboard-meta \{ height: 16px; line-height: 16px; \}/);
  assert.match(layoutCss, /\.tf-leaderboard-stats \{ height: 22px; line-height: 16px; \}/);
  assert.equal(22 + 16 + 22 + 2 * 2, 64);
  assert.match(card, /px-2 py-\[2px\]/);
  assert.match(card, /tf-leaderboard-info flex-1 min-w-0 overflow-hidden/);
});

test('rank is moved to top-right inside the card so it cannot cover a tightly inset avatar', () => {
  assert.match(card, /absolute right-1\.5 top-1\.5 z-10/);
  assert.match(card, /tf-leaderboard-name min-w-0 pr-\[70px\]/);
  assert.doesNotMatch(card, /absolute -top-3 left-0/);
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

test('avatar photos, initials and loading state share the inset rounded-square geometry', () => {
  assert.match(card, /className="tf-leaderboard-avatar shrink-0 overflow-hidden/);
  assert.match(card, /\{entry\.avatarUrl \? \(/);
  assert.match(card, /className="block h-full w-full object-cover"/);
  assert.match(card, /className="grid h-full w-full place-items-center bg-gradient-to-br/);
  assert.match(layoutCss, /border-radius: var\(--tf-leaderboard-avatar-radius\)/);
  assert.match(layoutCss, /flex: 0 0 var\(--tf-leaderboard-avatar-size\)/);
  assert.match(page, /className="tf-leaderboard-avatar tf-skeleton"/);
  const avatarMarkup = card.slice(card.indexOf('tf-leaderboard-avatar shrink-0'), card.indexOf('{/* Three fixed, non-wrapping lines')) ;
  assert.doesNotMatch(avatarMarkup, /rounded-full|rounded-xl border|h-12|w-12/);
  assert.doesNotMatch(page, /tf-skeleton-card|tf-skeleton-avatar/);
});


test('leaderboard is three columns on wide screens, two on medium and one on mobile with no redundant prompt', () => {
  const gridClass = 'md:grid-cols-2 xl:grid-cols-3';
  assert.equal((page.match(/md:grid-cols-2 xl:grid-cols-3/g) || []).length, 2);
  assert.ok(page.includes(gridClass));
  assert.doesNotMatch(page, /Нажми на участника, чтобы открыть его профиль/);
  assert.match(page, /<h1 className="text-2xl font-semibold">Топ студентов<\/h1>/);
});

test('long display names and usernames use an overflow-driven marquee inside a fixed-height card', () => {
  assert.match(card, /tf-leaderboard-name[^\n]*?pr-\[70px\]/);
  assert.match(card, /<LeaderboardMarquee title=\{\[name,/);
  assert.match(card, /<span>\{name\}<\/span>/);
  assert.match(card, /\{showLogin &&[\s\S]*?<span>\{login\}<\/span>/);
  assert.equal((card.match(/<LeaderboardMarquee/g) || []).length, 3);
  assert.match(marquee, /const available = viewport.clientWidth;/);
  assert.match(marquee, /measureLeaderboardMarquee\(required, available\)/);
  assert.match(marqueeCss, /animation: tf-leaderboard-marquee-scroll/);
  assert.equal(measureLeaderboardMarquee(800, 240).overflow, true);
});


test('leaderboard requests 24 students initially and 24 more per click', () => {
  assert.match(page, /const LEADERBOARD_PAGE_SIZE = 24;/);
  assert.match(page, /params\.pageSize = LEADERBOARD_PAGE_SIZE;/);
  assert.match(page, /const nextPage = reset \? 1 : currentPage \+ 1;/);
  assert.match(page, /setEntries\(\(prev\) => reset \? parsed\.items : \[\.\.\.prev, \.\.\.parsed\.items\]\)/);
  assert.match(page, /index % LEADERBOARD_PAGE_SIZE/);
});
