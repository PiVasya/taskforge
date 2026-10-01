import api from './http';

function emitQuotaChanged() {
  try { window.dispatchEvent(new Event('quota:changed')); } catch {}
}

export const getTestAssignment = async (id) => (await api.get(`/api/test-assignments/${id}`)).data;
export const getTestSolveShell = async (id) => (await api.get(`/api/test-assignments/${id}/solve-shell`)).data;
export const getTestStatement = async (id) => (await api.get(`/api/test-assignments/${id}/statement`)).data;
export const getTestTests = async (id) => (await api.get(`/api/test-assignments/${id}/tests`)).data;
export const getTestAssignmentForEdit = async (id) => (await api.get(`/api/test-assignments/${id}/edit`)).data;
export const createTestAssignment = async (courseId, payload) => (await api.post(`/api/courses/${courseId}/test-assignments`, payload)).data;
export const updateTestAssignment = async (id, payload) => (await api.put(`/api/test-assignments/${id}`, payload)).data;

export async function startTestAttempt(id) {
  const res = await api.post(`/api/test-assignments/${id}/attempts`);
  emitQuotaChanged();
  return res.data;
}

export const submitTestAttempt = async (id, attemptId, payload) => (await api.post(`/api/test-assignments/${id}/attempts/${attemptId}/submit`, payload)).data;
export const getTestAttempt = async (id, attemptId) => (await api.get(`/api/test-assignments/${id}/attempts/${attemptId}`)).data;
