import { applyLiveEventToItems, isPendingLiveStatus, mergeLiveEvent, reconcileLiveDetail } from './adminSolutionLiveModel';

describe('admin live verdict reconciliation', () => {
  test('only unresolved verdicts need polling', () => {
    expect(isPendingLiveStatus('Queued')).toBe(true);
    expect(isPendingLiveStatus('Preparing')).toBe(true);
    expect(isPendingLiveStatus('Accepted')).toBe(false);
    expect(isPendingLiveStatus('JudgeUnavailable')).toBe(false);
  });

  test('restores terminal result and score after a missed SSE event', () => {
    const pending = { key: 'code:42', status: 'Queued', score: 0, detail: { status: 'Queued' } };
    const detail = { status: 'Accepted', score: 100, submittedCode: 'print(2)' };
    expect(reconcileLiveDetail(pending, detail)).toEqual({ ...pending, status: 'Accepted', score: 100, detail });
  });

  test('cannot downgrade a resolved card with a delayed pending SSE event', () => {
    const accepted = { key: 'code:42', status: 'Accepted', score: 100, detail: { status: 'Accepted' } };
    const stale = { key: 'code:42', status: 'Queued', score: 0, detail: { status: 'Queued' } };
    expect(mergeLiveEvent(accepted, stale)).toEqual({ ...stale, status: 'Accepted', score: 100, detail: accepted.detail });
  });

  test('durable deleted event removes the solution instead of rendering a ghost', () => {
    const current = [
      { key: 'code:42', status: 'Accepted', occurredAtUtc: '2026-11-19T12:00:00Z' },
      { key: 'math:20', status: 'Rejected', occurredAtUtc: '2026-11-19T12:00:01Z' },
    ];
    const next = applyLiveEventToItems(current, {
      key: 'code:42', status: 'Deleted', occurredAtUtc: '2026-11-19T12:00:02Z',
    });
    expect(next).toEqual([current[1]]);
  });

  test('cannot downgrade a terminal verdict with a stale HTTP response', () => {
    const accepted = { key: 'code:42', status: 'Accepted', score: 100 };
    expect(reconcileLiveDetail(accepted, { status: 'Queued', score: 0 })).toBe(accepted);
  });
});
