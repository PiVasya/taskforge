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
