import api from './http';

function emitQuotaChanged() {
  try { window.dispatchEvent(new Event('quota:changed')); } catch {}
}

export const getCodeAssignment = async (id) => (await api.get(`/api/code-assignments/${id}`)).data;
export const getCodeSolveShell = async (id) => (await api.get(`/api/code-assignments/${id}/solve-shell`)).data;
export const getCodeStatement = async (id) => (await api.get(`/api/code-assignments/${id}/statement`)).data;
export const getCodeTests = async (id) => (await api.get(`/api/code-assignments/${id}/tests`)).data;
export const getCodeAssignmentForEdit = async (id) => (await api.get(`/api/code-assignments/${id}/edit`)).data;
export const createCodeAssignment = async (courseId, payload) => (await api.post(`/api/courses/${courseId}/code-assignments`, payload)).data;
export const updateCodeAssignment = async (id, payload) => (await api.put(`/api/code-assignments/${id}`, payload)).data;
