import api from './http';



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
