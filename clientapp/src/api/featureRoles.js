import api from './http';

export async function getFeatureRoles() {
  const { data } = await api.get('/api/admin/feature-roles');
  return Array.isArray(data) ? data : [];
}

export async function createFeatureRole(payload) {
  const { data } = await api.post('/api/admin/feature-roles', payload);
  return data;
}

export async function updateFeatureRole(id, payload) {
  await api.put(`/api/admin/feature-roles/${id}`, payload);
}

export async function deleteFeatureRole(id) {
  await api.delete(`/api/admin/feature-roles/${id}`);
}

export async function searchFeatureRoleUsers(query) {
  const { data } = await api.get('/api/admin/feature-roles/users', { params: { query } });
  return Array.isArray(data) ? data : [];
}

export async function assignFeatureRole(userId, code) {
  await api.post(`/api/admin/feature-roles/users/${userId}/roles`, { code });
}

export async function removeFeatureRole(userId, code) {
  await api.delete(`/api/admin/feature-roles/users/${userId}/roles/${encodeURIComponent(code)}`);
}
