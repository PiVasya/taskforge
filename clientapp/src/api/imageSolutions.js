import api from './http';

export async function getMyImageSolutions({ days } = {}) {
  const res = await api.get('/api/me/image-solutions', { params: days ? { days } : {} });
  return res.data;
}

export async function getMyImageSolutionDetails(id) {
  const res = await api.get(`/api/me/image-solutions/${id}`);
  return res.data;
}
