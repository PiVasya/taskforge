import api from './http';

export async function getAdminUsers(params = {}) {
  const { data } = await api.get('/api/admin/users', { params });
  return {
    items: Array.isArray(data) ? data : (Array.isArray(data?.items) ? data.items : []),
    stats: data?.stats || null,
  };
}

export async function getAdminUser(userId) {
  const { data } = await api.get(`/api/admin/users/${userId}`);
  return data;
}

export async function updateAdminUser(userId, payload) {
  const { data } = await api.put(`/api/admin/users/${userId}`, payload);
  return data;
}

export async function unlinkAdminTelegram(userId) {
  const { data } = await api.delete(`/api/admin/users/${userId}/telegram-link`);
  return data;
}

export async function deleteAdminUser(userId) {
  await api.delete(`/api/admin/users/${userId}`);
}
