import api from './http';

export async function getAdminActivity(params = {}) {
  const { data } = await api.get('/api/admin/activity', { params });
  return data;
}
