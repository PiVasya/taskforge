import api from './http';

// Текущий статус квот (token buckets) для пользователя.
// Backend: GET /api/me/quotas
export async function getMyQuotas() {
  const { data } = await api.get('/api/me/quotas');
  return data;
}
