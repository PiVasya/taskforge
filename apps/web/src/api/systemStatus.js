import api from './http';

export async function getSystemStatus({ signal } = {}) {
  const { data } = await api.get('/api/admin/cluster', { signal });
  return data;
}
