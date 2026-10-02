import api from './http';

export async function getGroups() {
  const { data } = await api.get('/api/groups');
  return Array.isArray(data) ? data : [];
}

export async function createGroup(payload) {
  const { data } = await api.post('/api/admin/groups', payload);
  return data;
}

export async function updateGroup(id, payload) {
  const { data } = await api.put(`/api/admin/groups/${id}`, payload);
  return data;
}

export async function deleteGroup(id) {
  await api.delete(`/api/admin/groups/${id}`);
}

export async function addGroupMember(groupId, userId) {
  await api.post(`/api/admin/groups/${groupId}/members`, { userId });
}

export async function removeGroupMember(groupId, userId) {
  await api.delete(`/api/admin/groups/${groupId}/members/${userId}`);
}

export async function getAdminGroups({ mineOnly = false } = {}) {
  const { data } = await api.get('/api/admin/groups', { params: mineOnly ? { mineOnly: true } : undefined });
  return Array.isArray(data) ? data : [];
}

export async function getAdminGroupMemberIds(groupId) {
  const { data } = await api.get(`/api/admin/groups/${groupId}/members`);
  return Array.isArray(data?.userIds) ? data.userIds : [];
}
