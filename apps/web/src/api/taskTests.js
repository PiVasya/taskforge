import api from './http';

function emitQuotaChanged() {
  try { window.dispatchEvent(new Event('quota:changed')); } catch {}
}

export async function startTaskTest(assignmentId) {
  const { data } = await api.post(`/api/task-tests/${assignmentId}/start`);
  emitQuotaChanged();
  return data;
}

export async function submitTaskTest(assignmentId, payload) {
  const { data } = await api.post(`/api/task-tests/${assignmentId}/submit`, payload);
  return data;
}

export async function getMyTaskTestAttempt(attemptId) {
  const { data } = await api.get(`/api/me/test-attempts/${attemptId}`);
  return data;
}

export async function getTaskTestEdit(assignmentId) {
  const { data } = await api.get(`/api/task-tests/${assignmentId}/edit`);
  return data;
}

export async function saveTaskTestEdit(assignmentId, payload) {
  await api.put(`/api/task-tests/${assignmentId}/edit`, payload);
}
