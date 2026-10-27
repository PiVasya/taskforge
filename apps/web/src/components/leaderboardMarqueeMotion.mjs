const GAP_PX = 28;
const PIXELS_PER_SECOND = 38;

export function measureLeaderboardMarquee(requiredWidth, availableWidth) {
  if (!Number.isFinite(requiredWidth) || !Number.isFinite(availableWidth) || availableWidth <= 0) {
    return { overflow: false, distance: 0, duration: 0 };
  }

  const width = Math.ceil(Math.max(0, requiredWidth));
  if (width <= availableWidth + 1) {
    return { overflow: false, distance: 0, duration: 0 };
  }

  const distance = width + GAP_PX;
  return {
    overflow: true,
    distance,
    duration: Math.max(9, Math.min(40, distance / PIXELS_PER_SECOND)),
  };
}
