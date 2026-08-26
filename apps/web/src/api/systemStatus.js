import api from './http';

export async function getSystemStatus() {
  const { data } = await api.get('/api/admin/cluster');
  return data;
}
