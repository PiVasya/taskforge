import { recoverSubmittedAttempt, shouldRecoverSubmittedAttempt } from './recoverSubmittedAttempt';

describe('submitted attempt recovery', () => {
  test('recovery is attempted only for ambiguous submit outcomes', () => {
    expect(shouldRecoverSubmittedAttempt(new Error('network'))).toBe(true);
    expect(shouldRecoverSubmittedAttempt({ response: { status: 503, data: {} } })).toBe(true);
    expect(shouldRecoverSubmittedAttempt({ response: { status: 409, data: { code: 'ATTEMPT_ALREADY_SUBMITTED' } } })).toBe(true);
    expect(shouldRecoverSubmittedAttempt({ response: { status: 400, data: { code: 'INVALID_ANSWER' } } })).toBe(false);
  });

  test('returns an already submitted attempt without retrying the mutation', async () => {
    const submitted = { id: 'attempt-1', submittedAtUtc: '2026-09-17T18:00:00Z', passed: true };
    const loadAttempt = jest.fn().mockResolvedValue(submitted);

    await expect(recoverSubmittedAttempt(loadAttempt)).resolves.toEqual(submitted);
    expect(loadAttempt).toHaveBeenCalledTimes(1);
  });

  test('returns null when the attempt is still unsubmitted after bounded reconciliation', async () => {
    const loadAttempt = jest.fn().mockResolvedValue({ id: 'attempt-1', submittedAtUtc: null });

    await expect(recoverSubmittedAttempt(loadAttempt)).resolves.toBeNull();
    expect(loadAttempt).toHaveBeenCalledTimes(3);
  });
});
