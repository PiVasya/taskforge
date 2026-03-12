import api from './http';

export async function getAdminUsers(params) {
  const { data } = await api.get('/api/admin/users', { params });
  return {
    items: Array.isArray(data) ? data : (Array.isArray(data?.items) ? data.items : []),
    stats: data?.stats || null,
  };
}

export async function updateAdminUser(userId, payload) {
  await api.put(`/api/admin/users/${userId}`, payload);
}

export async function deleteAdminUser(userId) {
  await api.delete(`/api/admin/users/${userId}`);
}
