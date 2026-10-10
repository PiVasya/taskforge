// The five histories are independently paged and each sorted newest-first.
// Fetching N entries from *each* non-exhausted history is enough to display
// the true newest N entries of their union, without silently skipping a kind.
export const SOLUTION_TIMELINE_KINDS = ['code', 'sql', 'tests', 'images', 'math'];

export function solutionTimelineDate(kind, item) {
  const source = kind === 'tests' || kind === 'math'
    ? (item?.submittedAt ?? item?.createdAt ?? item?.createdAtUtc)
    : (item?.submittedAt ?? item?.createdAt ?? item?.createdAtUtc ?? item?.CreatedAt);
  const time = new Date(source || 0).getTime();
  return Number.isFinite(time) ? time : 0;
}

export function mergeSolutionTimeline(histories) {
  const seen = new Set();
  const entries = [];
  for (const kind of SOLUTION_TIMELINE_KINDS) {
    for (const item of Array.isArray(histories?.[kind]) ? histories[kind] : []) {
      const id = String(item?.id ?? item?.Id ?? item?.attemptId ?? item?.AttemptId ?? '');
      if (!id) continue;
      const key = `${kind}:${id}`;
      if (seen.has(key)) continue;
      seen.add(key);
      entries.push({ key, id, kind, item, timestamp: solutionTimelineDate(kind, item) });
    }
  }
  entries.sort((a, b) => b.timestamp - a.timestamp || a.key.localeCompare(b.key));
  return entries;
}

export function timelineHasMore(entries, visibleCount, hasMoreByKind) {
  return entries.length > visibleCount || SOLUTION_TIMELINE_KINDS.some((kind) => !!hasMoreByKind?.[kind]);
}

export function timelineNextPageRequests(histories, hasMoreByKind, visibleCount) {
  return SOLUTION_TIMELINE_KINDS.filter((kind) => {
    const rows = histories?.[kind];
    return !!hasMoreByKind?.[kind] && (!Array.isArray(rows) || rows.length < visibleCount);
  });
}
