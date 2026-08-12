export function shouldRecoverSubmittedAttempt(error) {
  const status = Number(error?.response?.status || 0);
  const code = String(error?.response?.data?.code || '').toUpperCase();
  return !error?.response || status >= 500 || code === 'ATTEMPT_ALREADY_SUBMITTED';
}

function delay(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

export async function recoverSubmittedAttempt(loadAttempt) {
  const delays = [0, 300, 900];
  for (const waitMs of delays) {
    if (waitMs > 0) await delay(waitMs);
    try {
      const attempt = await loadAttempt();
      if (attempt?.submittedAt || attempt?.submittedAtUtc) return attempt;
    } catch {
      // Recovery is best-effort. The original submit error remains authoritative
      // when no submitted attempt can be observed.
    }
  }
  return null;
}
