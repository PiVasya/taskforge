import api from './http';

function emitQuotaChanged() {
  try { window.dispatchEvent(new Event('quota:changed')); } catch {}
}

export async function startMathTask(assignmentId) {
  const { data } = await api.post(`/api/math-tasks/${assignmentId}/start`);
  emitQuotaChanged();
  return data;
}

export async function submitMathTask(assignmentId, payload) {
  const { data } = await api.post(`/api/math-tasks/${assignmentId}/submit`, payload);
  return data;
}

export async function getMyMathAttempt(attemptId) {
  const { data } = await api.get(`/api/me/math-attempts/${attemptId}`);
  return data;
}
