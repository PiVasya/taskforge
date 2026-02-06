import api from './http';

export async function getMyImageSolutions({ days, assignmentId } = {}) {
  const params = {};
  if (days) params.days = days;
  if (assignmentId) params.assignmentId = assignmentId;
  const res = await api.get('/api/me/image-solutions', { params });
  return res.data;
}

export async function getMyImageSolutionDetails(id) {
  const res = await api.get(`/api/me/image-solutions/${id}`);
  return res.data;
}
