import api from './http';

function emitQuotaChanged() {
  try { window.dispatchEvent(new Event('quota:changed')); } catch {}
}

export async function submitCodeSolution(assignmentId, payload) {
  const res = await api.post('/api/code-solutions', { assignmentId, ...payload });
  emitQuotaChanged();
  return res.data;
}

export const listCodeSubmissions = async (assignmentId) =>
  (await api.get('/api/code-solutions', { params: { assignmentId } })).data;

export const getCodeSubmission = async (_assignmentId, submissionId) =>
  (await api.get(`/api/code-solutions/${submissionId}`)).data;

export const getCodeTopSolutions = async (assignmentId, top = 20) =>
  (await api.get('/api/code-solutions/top', { params: { assignmentId, top } })).data;
