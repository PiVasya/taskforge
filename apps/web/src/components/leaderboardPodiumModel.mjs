export function splitLeaderboardEntries(entries) {
  const list = Array.isArray(entries) ? entries : [];
  const ranks = new Map();
  for (const entry of list) {
    const rank = Number(entry?.rank);
    if ([1, 2, 3].includes(rank) && !ranks.has(rank)) ranks.set(rank, entry);
  }

  // The first-page ranking must contain the entire top three. Search and
  // pagination may yield fewer participants; in that case use ordinary cards.
  if (![1, 2, 3].every((rank) => ranks.has(rank))) {
    return { podium: [], regular: list };
  }

  const podium = [ranks.get(2), ranks.get(1), ranks.get(3)];
  const hiddenFromGrid = new Set(podium);
  return { podium, regular: list.filter((entry) => !hiddenFromGrid.has(entry)) };
}

export function getLeaderboardName(entry) {
  const login = String(entry?.login || entry?.userLogin || entry?.username || '').trim();
  return entry?.displayName || entry?.userName || entry?.fullName || login || entry?.maskedEmail || entry?.email || 'Пользователь';
}

export function getLeaderboardLogin(entry) {
  return String(entry?.login || entry?.userLogin || entry?.username || '').trim();
}

export function getLeaderboardSolved(entry) {
  return entry?.solvedAssignments ?? entry?.solvedCount ?? entry?.solved ?? 0;
}

// The first server page is animated once. Later pages render immediately so
// appended entries never leave empty grid cells waiting for staggered delays.
export function getLeaderboardRowRevealDelay(gridIndex, podiumCount = 0, pageSize = 24) {
  if (!Number.isInteger(gridIndex) || gridIndex < 0 || !Number.isInteger(pageSize) || pageSize < 1) return null;
  const firstPageOffset = Math.max(0, Number(podiumCount) || 0);
  return gridIndex + firstPageOffset < pageSize ? gridIndex * 35 : null;
}

// Rebuilding the GPU scene when more rows are appended causes avoidable gaps
// and lost animation state. Only a change in the actual winners requires it.
export function getLeaderboardPodiumIdentity(entries) {
  if (!Array.isArray(entries)) return '';
  return entries.map((entry) => `${Number(entry?.rank)}:${String(entry?.userId || '')}`).join('|');
}
