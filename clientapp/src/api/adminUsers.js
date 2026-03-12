import api from './http';

export async function getAdminUsers(params) {
  const { data } = await api.get('/api/admin/users', { params });
  return Array.isArray(data) ? data : [];
}

export async function updateAdminUser(userId, payload) {
  await api.put(`/api/admin/users/${userId}`, payload);
}
