// The extension shares the normal leaderboard row's content geometry, but
// uses the existing floating portrait instead of clipping a second avatar.
export const LEADERBOARD_INLINE_AVATAR_INSET = 69;
export const LEADERBOARD_INLINE_CARD_HEIGHT = 74;
export const LEADERBOARD_INLINE_GAP = 6;
export const LEADERBOARD_INLINE_MIN_WIDTH = 295;

export function positionLeaderboardInlinePreview({
  avatar,
  viewportWidth,
  viewportHeight = Number.POSITIVE_INFINITY,
  preferredWidth = 520,
}) {
  const margin = 12;
  const viewport = Math.max(0, Number.isFinite(viewportWidth) ? viewportWidth : 0);
  const avatarLeft = Number.isFinite(avatar?.left) ? avatar.left : 0;
  const avatarRight = Number.isFinite(avatar?.right) ? avatar.right : 0;
  const avatarTop = Number.isFinite(avatar?.top) ? avatar.top : 0;
  const avatarHeight = Number.isFinite(avatar?.height) ? avatar.height : 0;

  // This is the width of the original row minus its hidden avatar and frame.
  // Unlike the old cropped row, the extension has its own complete corners.
  const desiredWidth = Math.max(LEADERBOARD_INLINE_MIN_WIDTH, preferredWidth - LEADERBOARD_INLINE_AVATAR_INSET);
  const rightRoom = Math.max(0, viewport - margin - avatarRight - LEADERBOARD_INLINE_GAP);
  const leftRoom = Math.max(0, avatarLeft - margin - LEADERBOARD_INLINE_GAP);
  const side = rightRoom >= Math.min(desiredWidth, LEADERBOARD_INLINE_MIN_WIDTH) || rightRoom >= leftRoom
    ? 'right'
    : 'left';
  const available = side === 'right' ? rightRoom : leftRoom;
  const width = Math.min(desiredWidth, available);
  const left = side === 'right'
    ? avatarRight + LEADERBOARD_INLINE_GAP
    : avatarLeft - LEADERBOARD_INLINE_GAP - width;

  const naturalTop = avatarTop + (avatarHeight - LEADERBOARD_INLINE_CARD_HEIGHT) / 2;
  const maxTop = Number.isFinite(viewportHeight)
    ? Math.max(margin, viewportHeight - margin - LEADERBOARD_INLINE_CARD_HEIGHT)
    : Number.POSITIVE_INFINITY;
  const top = Math.max(margin, Math.min(naturalTop, maxTop));

  return {
    left: Math.round(left),
    top: Math.round(top),
    width: Math.round(Math.max(0, width)),
    side,
  };
}
