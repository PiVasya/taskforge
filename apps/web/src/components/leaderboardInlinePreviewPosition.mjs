// The ordinary leaderboard row reserves 5px framing on both sides of a
// 64px avatar. Hide exactly that part of its cloned row; only the rectangular
// information panel becomes visible to the right of the original portrait.
export const LEADERBOARD_INLINE_AVATAR_INSET = 69;
export const LEADERBOARD_INLINE_CARD_HEIGHT = 74;

export function positionLeaderboardInlinePreview({ avatar, viewportWidth, preferredWidth = 520 }) {
  const margin = 12;
  const inset = LEADERBOARD_INLINE_AVATAR_INSET;
  const viewport = Number.isFinite(viewportWidth) ? viewportWidth : 0;
  const avatarRight = Number.isFinite(avatar?.right) ? avatar.right : 0;
  const avatarTop = Number.isFinite(avatar?.top) ? avatar.top : 0;
  const avatarHeight = Number.isFinite(avatar?.height) ? avatar.height : 0;
  const width = Math.max(inset + 8, Math.min(preferredWidth, viewport - margin - avatarRight + inset));
  return {
    left: Math.round(avatarRight - inset),
    top: Math.round(avatarTop + (avatarHeight - LEADERBOARD_INLINE_CARD_HEIGHT) / 2),
    width: Math.round(width),
  };
}
