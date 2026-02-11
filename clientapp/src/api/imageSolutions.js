import api from './http';

export async function getMyImageSolutions({ days = 30, skip = 0, take = 50, assignmentId = null } = {}) {
  const params = { days, skip, take };
  if (assignmentId) params.assignmentId = assignmentId;
  const res = await api.get('/api/me/image-solutions', { params });
  return res.data;
}

export async function getMyImageSolutionDetails(id) {
  const res = await api.get(`/api/me/image-solutions/${id}`);
  return res.data;
}
