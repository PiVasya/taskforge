import api from './http';

export async function getAdminAnalyticsOverview(days = 30) {
  const { data } = await api.get('/api/admin/analytics/overview', { params: { days } });
  return data;
}

export async function getAdminAnalyticsUser(userId, days = 30) {
  const { data } = await api.get(`/api/admin/analytics/users/${userId}`, { params: { days } });
  return data;
}

export async function searchAdminAnalyticsUsers(q, limit = 8) {
  const { data } = await api.get('/api/admin/analytics/users/search', { params: { q, limit } });
  return data;
}
