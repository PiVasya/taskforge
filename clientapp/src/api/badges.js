import api from './http';

/**
 * Получить список всех бейджей.
 * GET /api/badges
 */
export async function getAllBadges() {
  const { data } = await api.get('/api/badges');
  return Array.isArray(data) ? data : [];
}

/**
 * Создать новый бейдж. Принимает FormData с полями name, description и file (svg).
 * POST /api/badges
 * @param {FormData} formData
 */
export async function createBadge(formData) {
  const { data } = await api.post('/api/badges', formData, {
    headers: { 'Content-Type': 'multipart/form-data' },
  });
  return data;
}

/**
 * Выдать бейдж пользователю.
 * POST /api/badges/award
 * @param {string} userId
 * @param {string} badgeId
 */
export async function awardBadge(userId, badgeId) {
  await api.post('/api/badges/award', { userId, badgeId });
  return true;
}

/**
 * Получить список бейджей пользователя.
 * GET /api/badges/user/{userId}
 */
export async function getUserBadges(userId) {
  const { data } = await api.get(`/api/badges/user/${userId}`);
  return Array.isArray(data) ? data : [];
}