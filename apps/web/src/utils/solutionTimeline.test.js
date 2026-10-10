import { mergeSolutionTimeline, timelineHasMore, timelineNextPageRequests } from './solutionTimeline';

describe('my solutions unified timeline', () => {
  test('merges code, SQL, tests, images and math by actual submission timestamp', () => {
    const entries = mergeSolutionTimeline({
      code: [{ id: 'c1', submittedAt: '2026-10-10T11:00:00Z' }],
      sql: [{ id: 's1', submittedAt: '2026-10-10T16:00:00Z' }],
      tests: [{ attemptId: 't1', submittedAt: '2026-10-10T13:00:00Z' }],
      images: [{ id: 'i1', createdAt: '2026-10-10T14:00:00Z' }],
      math: [{ attemptId: 'm1', submittedAt: '2026-10-10T12:00:00Z' }],
    });
    expect(entries.map((entry) => entry.key)).toEqual(['sql:s1', 'images:i1', 'tests:t1', 'math:m1', 'code:c1']);
  });

  test('does not duplicate a solution after reopening a cached page', () => {
    const entries = mergeSolutionTimeline({ code: [
      { id: 'c1', submittedAt: '2026-10-10T11:00:00Z' },
      { id: 'c1', submittedAt: '2026-10-10T11:00:00Z' },
    ] });
    expect(entries).toHaveLength(1);
  });

  test('requests the next page only for kinds that lack the next visible window', () => {
    const twenty = Array.from({ length: 20 }, (_, n) => ({ id: `c${n}` }));
    const forty = Array.from({ length: 40 }, (_, n) => ({ id: `s${n}` }));
    expect(timelineNextPageRequests({ code: twenty, sql: forty, tests: [], images: [], math: [] },
      { code: true, sql: true, tests: false, images: false, math: false }, 40)).toEqual(['code']);
    expect(timelineHasMore([], 20, { sql: true })).toBe(true);
    expect(timelineHasMore(twenty, 20, { code: false })).toBe(false);
  });

  test('uses a deterministic order for same-millisecond submissions', () => {
    const first = mergeSolutionTimeline({ sql: [{ id: 'b', submittedAt: '2026-10-10T11:00:00Z' }], code: [{ id: 'a', submittedAt: '2026-10-10T11:00:00Z' }] });
    expect(first.map((entry) => entry.key)).toEqual(['code:a', 'sql:b']);
  });
});
