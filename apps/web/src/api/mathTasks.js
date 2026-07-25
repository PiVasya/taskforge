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

export async function getMathTaskEdit(assignmentId) {
  const { data } = await api.get(`/api/math-tasks/${assignmentId}/edit`);
  return data;
}

export async function saveMathTaskEdit(assignmentId, payload) {
  await api.put(`/api/math-tasks/${assignmentId}/edit`, payload);
}
