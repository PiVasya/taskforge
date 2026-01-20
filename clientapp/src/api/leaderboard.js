import api from './http';

export async function getLeaderboard(params = {}) {
  const res = await api.get('/api/leaderboard', { params });
  return res.data;
}
