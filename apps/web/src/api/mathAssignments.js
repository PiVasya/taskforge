import api from './http';

function emitQuotaChanged() {
  try { window.dispatchEvent(new Event('quota:changed')); } catch {}
}

export const getMathAssignment = async (id) => (await api.get(`/api/math-assignments/${id}`)).data;
export const getMathSolveShell = async (id) => (await api.get(`/api/math-assignments/${id}/solve-shell`)).data;
export const getMathStatement = async (id) => (await api.get(`/api/math-assignments/${id}/statement`)).data;
export const getMathTests = async (id) => (await api.get(`/api/math-assignments/${id}/tests`)).data;
export const getMathAssignmentForEdit = async (id) => (await api.get(`/api/math-assignments/${id}/edit`)).data;
export const createMathAssignment = async (courseId, payload) => (await api.post(`/api/courses/${courseId}/math-assignments`, payload)).data;
export const updateMathAssignment = async (id, payload) => (await api.put(`/api/math-assignments/${id}`, payload)).data;

export async function startMathAttempt(id) {
  const res = await api.post(`/api/math-assignments/${id}/attempts`);
  emitQuotaChanged();
  return res.data;
}

export const submitMathAttempt = async (id, attemptId, payload) => (await api.post(`/api/math-assignments/${id}/attempts/${attemptId}/submit`, payload)).data;
export const getMathAttempt = async (id, attemptId) => (await api.get(`/api/math-assignments/${id}/attempts/${attemptId}`)).data;
