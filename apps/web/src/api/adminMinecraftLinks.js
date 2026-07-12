import api from './http';

export async function getAdminMinecraftLinks(params) {
  const { data } = await api.get('/api/admin/minecraft-links', { params });
  return Array.isArray(data) ? data : [];
}

export async function getAdminMinecraftUserRating(userId) {
  const { data } = await api.get(`/api/admin/minecraft-links/users/${userId}/rating`);
  return data;
}

export async function restoreAdminMinecraftUserRating(userId, payload) {
  const { data } = await api.post(`/api/admin/minecraft-links/users/${userId}/rating/restore`, payload);
  return data;
}
