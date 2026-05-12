import api from './http';


export async function getAllBadges() {
  const { data } = await api.get('/api/badges');
  return Array.isArray(data) ? data : [];
}


export async function createBadge(formData) {
  const { data } = await api.post('/api/badges', formData, {
    headers: { 'Content-Type': 'multipart/form-data' },
  });
  return data;
}


export async function awardBadge(userId, badgeId) {
  await api.post('/api/badges/award', { userId, badgeId });
  return true;
}


export async function getUserBadges(userId) {
  const { data } = await api.get(`/api/badges/user/${userId}`);
  return Array.isArray(data) ? data : [];
}


export async function deleteBadge(badgeId) {
  await api.delete(`/api/badges/${badgeId}`);
  return true;
}


export async function revokeBadge(userId, badgeId) {
  await api.post('/api/badges/revoke', { userId, badgeId });
  return true;
}