import api from './http';

export async function getMyImageSolutions({ days = null, skip = 0, take = 50, assignmentId = null } = {}) {
  const params = { skip, take };
  if (days !== null && days !== undefined) params.days = days;
  if (assignmentId) params.assignmentId = assignmentId;
  const res = await api.get('/api/me/image-solutions', { params });
  return res.data;
}

export async function getMyImageSolutionDetails(id) {
  const res = await api.get(`/api/me/image-solutions/${id}`);
  return res.data;
}

function emitQuotaChanged() {
  try { window.dispatchEvent(new Event('quota:changed')); } catch {}
}

export async function submitImageSolution(assignmentId, language, code, input = '', timeoutSeconds = null) {
  const payload = { assignmentId, language, code, input };
  if (timeoutSeconds != null) payload.timeoutSeconds = timeoutSeconds;
  const res = await api.post('/api/image-solutions', payload, {
    timeout: 90000,
    validateStatus: (status) => (status >= 200 && status < 300) || status === 400,
  });
  emitQuotaChanged();
  return res.data;
}
