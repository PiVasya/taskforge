import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import {
  positionLeaderboardInlinePreview,
  LEADERBOARD_INLINE_AVATAR_INSET,
  LEADERBOARD_INLINE_CARD_HEIGHT,
  LEADERBOARD_INLINE_GAP,
  LEADERBOARD_INLINE_MIN_WIDTH,
} from '../../src/components/leaderboardInlinePreviewPosition.mjs';

const component = await readFile(new URL('../../src/components/LeaderboardPodium.jsx', import.meta.url), 'utf8');
const styles = await readFile(new URL('../../src/components/LeaderboardPodium.css', import.meta.url), 'utf8');
const ordinaryCard = await readFile(new URL('../../src/components/LeaderboardCard.jsx', import.meta.url), 'utf8');

test('the reveal is the SAME leaderboard card component and shows all original profile metadata', () => {
  assert.match(component, /import LeaderboardCard from '\.\/LeaderboardCard';/);
  assert.match(component, /<LeaderboardCard entry=\{selected\} onOpenProfile=\{onOpenProfile\} \/>/);
  assert.match(component, /className="tf-top3-inline-popover"/);
  assert.match(ordinaryCard, /tf-leaderboard-stats/);
  assert.match(ordinaryCard, /tf-leaderboard-meta/);
  assert.match(ordinaryCard, /\{entry\.badges\.map/);
  assert.doesNotMatch(component, /tf-top3-pop-(name|chips|rank|head|foot|link|avatar)/);
});

test('the extension is a whole rounded panel with NO cropped corner or repeated avatar', () => {
  assert.match(styles, /\.tf-top3-inline-popover \.tf-leaderboard-avatar \{\s*display: none;/);
  assert.match(styles, /\.tf-top3-inline-popover \.tf-leaderboard-card \{[^}]*border-radius: var\(--tf-leaderboard-card-radius\);/);
  assert.doesNotMatch(styles, /clip-path: inset\(0 0 0 calc\(var\(--tf-leaderboard-avatar-size\)/);
  assert.match(styles, /\.tf-top3-inline-popover \.tf-leaderboard-card:hover,[\s\S]*?transform: none !important;/);
  assert.match(styles, /html\.neo-brutal \.tf-top3-inline-popover \.tf-leaderboard-card:hover/);
  assert.match(component, /onPointerEnter=\{clearHide\}/);
  assert.match(component, /onPointerLeave=\{scheduleHide\}/);
  assert.match(component, /onPointerEnter=\{\(event\) => .*open\(entry\)/);
});

test('revealing and hiding animate from the correct edge and keep the card corners/shadow intact', () => {
  for (const animation of [
    'tf-top3-inline-in-right',
    'tf-top3-inline-out-right',
    'tf-top3-inline-in-left',
    'tf-top3-inline-out-left',
  ]) assert.match(styles, new RegExp(`@keyframes ${animation} \\{`));
  assert.match(styles, /clip-path: inset\(-14px -14px -14px -14px\)/);
  assert.match(component, /data-closing=\{activeRank !== renderRank \? 'true' : 'false'\}/);
  assert.match(component, /window\.setTimeout\(\(\) => \{\s*setRenderRank/);
  assert.match(component, /\}, 190\);/);
  assert.match(styles, /@media \(prefers-reduced-motion: reduce\)/);
});

test('the information extension is attached to the avatar and tracks its levitation', () => {
  assert.equal(LEADERBOARD_INLINE_AVATAR_INSET, 64 + 4 + 1);
  assert.equal(LEADERBOARD_INLINE_CARD_HEIGHT, 64 + 2 * (4 + 1));
  const second = positionLeaderboardInlinePreview({
    avatar: { left: 462, right: 540, top: 185, height: 78 },
    viewportWidth: 1700, preferredWidth: 550,
  });
  assert.deepEqual(second, { left: 546, top: 187, width: 481, side: 'right' });
  assert.equal(second.left - LEADERBOARD_INLINE_GAP, 540);
  const first = positionLeaderboardInlinePreview({
    avatar: { left: 810, right: 900, top: 116, height: 90 },
    viewportWidth: 1700, preferredWidth: 550,
  });
  assert.deepEqual(first, { left: 906, top: 124, width: 481, side: 'right' });
  assert.match(component, /requestAnimationFrame\(followLevitation\)/);
  assert.match(component, /window\.cancelAnimationFrame\(frame\)/);
});

test('a right-edge portrait reveals the row on its left instead of clipping off the screen', () => {
  const result = positionLeaderboardInlinePreview({
    avatar: { left: 1402, right: 1480, top: 260, height: 78 },
    viewportWidth: 1700, preferredWidth: 550,
  });
  assert.deepEqual(result, { left: 915, top: 262, width: 481, side: 'left' });
  assert.equal(result.left + result.width + LEADERBOARD_INLINE_GAP, 1402);
  assert.ok(result.left >= 12);
  assert.ok(result.left + result.width <= 1700 - 12);
  assert.equal(LEADERBOARD_INLINE_MIN_WIDTH, 295);
  const narrow = positionLeaderboardInlinePreview({
    avatar: { left: 360, right: 450, top: 560, height: 90 },
    viewportWidth: 900, viewportHeight: 600, preferredWidth: 550,
  });
  assert.equal(narrow.top, 514);
  assert.ok(narrow.left >= 12);
  assert.ok(narrow.left + narrow.width <= 888);
});

test('touch uses the complete card, with profile click, while keyboard and modal handling remain intact', () => {
  assert.match(styles, /@media \(max-width: 800px\)/);
  assert.match(styles, /\.tf-top3-inline-popover \.tf-leaderboard-avatar \{ display: block; \}/);
  assert.match(styles, /bottom: max\(16px, env\(safe-area-inset-bottom\)\)/);
  assert.match(component, /onClick=\{\(event\) => handlePortraitClick\(entry, event\.currentTarget\)\}/);
  assert.match(component, /if \(onOpenProfile\) onOpenProfile\(entry, sourceElement\)/);
  assert.match(component, /else navigate\(`\/users\/\$\{entry\.userId\}`\)/);
  assert.match(component, /if \(profileOpen\) close\(\)/);
});
