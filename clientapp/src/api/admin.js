// ЕДИНАЯ точка входа для admin-роутов фронта
import api from './http';

/**
 * Поиск пользователей по email/имени/фамилии.
 * GET /api/admin/users?q=...&take=...
 */
export async function searchUsersOnce(q = '', take = 20) {
  const { data } = await api.get('/api/admin/users', {
    params: { q, take },
  });
  return Array.isArray(data) ? data : [];
}

/**
 * Список решений пользователя.
 * GET /api/admin/users/{userId}/solutions
 */
export async function getUserSolutions(userId, { courseId, assignmentId, skip = 0, take = 50, days = null } = {}) {
  const { data } = await api.get(`/api/admin/users/${userId}/solutions`, {
    params: { courseId, assignmentId, skip, take, days },
  });
  return Array.isArray(data) ? data : [];
}

/**
 * Детали одного решения.
 * GET /api/admin/solutions/{id}
 */
export async function getSolutionDetails(id) {
  const { data } = await api.get(`/api/admin/solutions/${id}`);
  return data;
}

/**
 * Bulk-детали нескольких решений (с ограничением по количеству).
 * POST /api/admin/solutions/bulk
 */
export async function getSolutionsDetailsBulkOrFallback(ids, { concurrency = 4 } = {}) {
  if (!Array.isArray(ids) || ids.length === 0) return [];

  const safeIds = ids.slice(0, 200);

  if (safeIds.length <= 8) {
    const results = [];
    for (const id of safeIds) {
      const dto = await getSolutionDetails(id);
      if (dto) results.push(dto);
    }
    return results;
  }

  const { default: pLimit } = await import('p-limit');
  const limit = pLimit(concurrency);

  const results = await Promise.all(
    safeIds.map((id) =>
      limit(async () => {
        const dto = await getSolutionDetails(id);
        return dto || null;
      })
    )
  );

  return results.filter(Boolean);
}

/**
 * Удаление всех решений пользователя (с фильтрами при необходимости).
 * DELETE /api/admin/users/{userId}/solutions?courseId=&assignmentId=
 */
export async function deleteUserSolutions(userId, { courseId, assignmentId } = {}) {
  await api.delete(`/api/admin/users/${userId}/solutions`, {
    params: { courseId, assignmentId },
  });
}

/**
 * Удаление пользователя целиком (аккаунт + все его решения).
 * DELETE /api/admin/users/{userId}
 */
export async function deleteUser(userId) {
  await api.delete(`/api/admin/users/${userId}`);
}
