import { api } from './client';

export async function getSystemStatus() {
  const { data } = await api.get('/api/admin/system-status');
  return data;
}
