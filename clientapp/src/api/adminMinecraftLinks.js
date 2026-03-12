import api from './http';

export async function getAdminMinecraftLinks(params) {
  const { data } = await api.get('/api/admin/minecraft-links', { params });
  return Array.isArray(data) ? data : [];
}
