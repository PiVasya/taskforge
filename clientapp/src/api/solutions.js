import api from './http';

function emitQuotaChanged() {
  try { window.dispatchEvent(new Event('quota:changed')); } catch {}
}



export async function submitSolution(assignmentId, payload) {
  const res = await api.post(`/api/assignments/${assignmentId}/submit`, payload);
  emitQuotaChanged();
  return res.data;
}



export async function listMySolutions(assignmentId) {
  const res = await api.get(`/api/me/solutions`, { params: { assignmentId } });
  return res.data;
}




export async function getMySolutions(opts = {}) {
  
  const params = {};

  if (opts && typeof opts === 'object' && !Array.isArray(opts)) {
    if (opts.courseId) params.courseId = opts.courseId;
    if (opts.assignmentId) params.assignmentId = opts.assignmentId;
    if (Number.isFinite(opts.skip)) params.skip = opts.skip;
    if (Number.isFinite(opts.take)) params.take = opts.take;
    if (Number.isFinite(opts.days) || opts.days === null) params.days = opts.days;
  } else if (opts) {
    params.assignmentId = opts;
  }

  const res = await api.get(`/api/me/solutions`, { params });
  return res.data;
}



export async function getMySolutionDetails(id) {
  const res = await api.get(`/api/me/solutions/${id}`);
  return res.data;
}
