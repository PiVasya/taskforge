import api from './http';

export async function getSystemStatus({ signal } = {}) {
  const { data } = await api.get('/api/admin/cluster', { signal });
  return data;
}

export async function switchClusterPrimary(target) {
  const { data } = await api.post('/api/admin/cluster/primary', { target });
  return data;
}
