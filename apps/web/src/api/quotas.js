import api from './http';



export async function getMyQuotas() {
  const { data } = await api.get('/api/me/quotas');
  return data;
}
