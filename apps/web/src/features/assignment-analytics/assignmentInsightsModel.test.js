import {
  attemptSummary, dailyChartRows, normalizeHistoricalAttempts,
  mergeAttemptFeed, typeLabel, statusLabel, behaviorLabel,
} from './assignmentInsightsModel';

test('totals use all attempts rather than capped recent activity', () => {
  const summary = attemptSummary({
    codeAttempts: 229, passedCodeAttempts: 123,
    testAttempts: 10, passedTests: 4,
  });
  expect(summary.total).toBe(239);
  expect(summary.passed).toBe(127);
  expect(summary.failed).toBe(112);
  expect(summary.pending).toBe(0);
  expect(summary.successRate).toBe(53.1);
});

test('running submissions are not counted as failed', () => {
  const summary = attemptSummary({ codeAttempts: 10, passedCodeAttempts: 4, pendingCodeAttempts: 3 });
  expect(summary.passed).toBe(4);
  expect(summary.failed).toBe(3);
  expect(summary.pending).toBe(3);
});

test('daily graph fills empty UTC dates but does not invent attempts', () => {
  const rows = dailyChartRows([
    { dayUtc: '2026-10-23', total: 5, passed: 3, failed: 2 },
    { dayUtc: '2026-10-24', total: 2, passed: 0, failed: 2 },
  ], 3, new Date('2026-10-24T23:59:00Z'));
  expect(rows.map(({ passed, failed }) => [passed, failed])).toEqual([[0, 0], [3, 2], [0, 2]]);
});

test('SSE live update replaces matching history, never leaks another assignment', () => {
  const id = '5e635191-8e4a-47db-9b42-216fa18ac618';
  const historical = normalizeHistoricalAttempts([{
    attemptId: 'attempt-a', sourceKind: 'code', fullName: 'Мария', status: 'running',
    createdAtUtc: '2026-10-24T12:00:00Z',
  }], id, 'Код');
  const merged = mergeAttemptFeed(historical, [
    { key: 'code:attempt-a', assignmentId: id, status: 'Accepted', occurredAtUtc: '2026-10-24T12:00:01Z' },
    { key: 'code:attempt-b', assignmentId: 'other', status: 'Accepted', occurredAtUtc: '2026-10-24T12:00:02Z' },
  ], id);
  expect(merged).toHaveLength(1);
  expect(merged[0].status).toBe('Accepted');
  expect(merged[0].userLabel).toBe('Мария');
});

test('public UI translates event/status labels', () => {
  expect(typeLabel('math')).toBe('Математика');
  expect(statusLabel('CompileError')).toBe('Ошибка компиляции');
  expect(behaviorLabel('visibility_hidden')).toBe('Скрыл вкладку');
});
