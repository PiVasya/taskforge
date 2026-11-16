export function currentTestFromConflict(error, attemptId) {
  if (error?.response?.status !== 409 || error?.response?.data?.code !== 'TEST_UPDATED_RELOAD_REQUIRED') return null;
  const latest = error.response.data.latestTest;
  return latest?.attemptId === attemptId && typeof latest?.testRevision === 'string'
    && Array.isArray(latest?.questions) ? latest : null;
}
