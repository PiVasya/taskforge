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
