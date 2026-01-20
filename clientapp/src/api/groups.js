import api from './http';

// список групп (для редактора курсов). Для Admin/Editor.
export async function getGroups() {
  try {
    const { data } = await api.get('/api/groups');
    return Array.isArray(data) ? data : [];
  } catch (e) {
    // fallback for older servers: admin endpoint
    if (e?.response?.status === 404) {
      const { data } = await api.get('/api/admin/groups');
      return Array.isArray(data) ? data : [];
    }
    throw e;
  }
}

// admin CRUD
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

// membership (admin)
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
