import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import {
  positionLeaderboardInlinePreview,
  LEADERBOARD_INLINE_AVATAR_INSET,
  LEADERBOARD_INLINE_CARD_HEIGHT,
} from '../../src/components/leaderboardInlinePreviewPosition.mjs';

const component = await readFile(new URL('../../src/components/LeaderboardPodium.jsx', import.meta.url), 'utf8');
const styles = await readFile(new URL('../../src/components/LeaderboardPodium.css', import.meta.url), 'utf8');
const ordinaryCard = await readFile(new URL('../../src/components/LeaderboardCard.jsx', import.meta.url), 'utf8');

test('the hover preview is the EXACT leaderboard row component, not a second profile-card implementation', () => {
  assert.match(component, /import LeaderboardCard from '\.\/LeaderboardCard';/);
  assert.match(component, /<LeaderboardCard entry=\{selected\} onOpenProfile=\{onOpenProfile\} \/>/);
  assert.match(component, /className="tf-top3-inline-popover"/);
  assert.doesNotMatch(component, /tf-top3-pop-(name|chips|rank|head|foot|link|avatar)/);
  assert.match(ordinaryCard, /tf-leaderboard-stats/);
  assert.match(ordinaryCard, /tf-leaderboard-meta/);
  assert.match(ordinaryCard, /\{entry\.badges\.map/);
});

test('hover is restricted to the existing avatar; the preview has no hover highlights or extra visible avatar', () => {
  const wrap = component.slice(component.indexOf('<span\n                    className="tf-top3-avatar-wrap"'));
  assert.match(wrap, /onPointerEnter=\{\(event\) => .*open\(entry\)/);
  assert.match(wrap, /onPointerLeave=\{\(event\) => .*scheduleHide\(\)/);
  assert.match(component, /onPointerEnter=\{clearHide\}/);
  assert.match(component, /onPointerLeave=\{scheduleHide\}/);
  assert.match(styles, /clip-path: inset\(0 0 0 calc\(var\(--tf-leaderboard-avatar-size\)/);
  assert.match(styles, /\.tf-top3-inline-popover \.tf-leaderboard-card:hover,[\s\S]*?transform: none !important;/);
  assert.match(styles, /html\.neo-brutal \.tf-top3-inline-popover \.tf-leaderboard-card:hover/);
});

test('hover row begins at the original avatar right edge and follows its continuous levitation', () => {
  assert.equal(LEADERBOARD_INLINE_AVATAR_INSET, 64 + 4 + 1);
  assert.equal(LEADERBOARD_INLINE_CARD_HEIGHT, 64 + 2 * (4 + 1));
  const second = positionLeaderboardInlinePreview({
    avatar: { right: 540, top: 185, height: 78 }, viewportWidth: 1700, preferredWidth: 550,
  });
  assert.deepEqual(second, { left: 471, top: 187, width: 550 });
  assert.equal(second.left + LEADERBOARD_INLINE_AVATAR_INSET, 540);
  const first = positionLeaderboardInlinePreview({
    avatar: { right: 900, top: 116, height: 90 }, viewportWidth: 1700, preferredWidth: 550,
  });
  assert.equal(first.top, 124);
  const third = positionLeaderboardInlinePreview({
    avatar: { right: 1480, top: 260, height: 78 }, viewportWidth: 1700, preferredWidth: 550,
  });
  assert.equal(third.left + LEADERBOARD_INLINE_AVATAR_INSET, 1480);
  assert.equal(third.left + third.width, 1688);
  assert.match(component, /requestAnimationFrame\(followLevitation\)/);
  assert.match(component, /window\.cancelAnimationFrame\(frame\)/);
});

test('the same compact preview works on small screens while clicking a portrait opens the full profile', () => {
  assert.match(styles, /@media \(max-width: 800px\)/);
  assert.match(styles, /clip-path: none;/);
  assert.match(styles, /bottom: max\(16px, env\(safe-area-inset-bottom\)\)/);
  assert.match(component, /onClick=\{\(event\) => handlePortraitClick\(entry, event\.currentTarget\)\}/);
  assert.match(component, /if \(onOpenProfile\) onOpenProfile\(entry, sourceElement\)/);
  assert.match(component, /else navigate\(`\/users\/\$\{entry\.userId\}`\)/);
  assert.match(component, /if \(profileOpen\) close\(\)/);
  assert.doesNotMatch(component, /if \(touch\) open\(entry\)/);
});
