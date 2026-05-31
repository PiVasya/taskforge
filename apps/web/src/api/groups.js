import api from './http';


export async function getGroups() {
  try {
    const { data } = await api.get('/api/groups');
    return Array.isArray(data) ? data : [];
  } catch (e) {
    
    if (e?.response?.status === 404) {
      const { data } = await api.get('/api/admin/groups');
      return Array.isArray(data) ? data : [];
    }
    throw e;
  }
}


export async function createGroup(payload) {
  const { data } = await api.post('/api/admin/groups', payload);
  return data;
}

export async function updateGroup(id, payload) {
  await api.put(`/api/admin/groups/${id}`, payload);
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


export async function getAdminGroups() {
  const { data } = await api.get('/api/admin/groups');
  return Array.isArray(data) ? data : [];
}
