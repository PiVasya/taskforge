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
 * Лайт-список решений пользователя.
 * GET /api/admin/users/{userId}/solutions?skip=&take=&days=
 */
export async function getUserSolutions(userId, { courseId, assignmentId, skip = 0, take = 50, days = null } = {}) {
  const { data } = await api.get(`/api/admin/users/${userId}/solutions`, {
    params: { courseId, assignmentId, skip, take, days },
  });
  return Array.isArray(data) ? data : (data?.items ?? []);
}

/**
 * Детали одного решения.
 * GET /api/admin/solutions/{id}
 */
export async function getSolutionDetails(solutionId) {
  const { data } = await api.get(`/api/admin/solutions/${solutionId}`);
  return data;
}

/**
 * Bulk-детали (если сервер поддерживает), иначе — аккуратный фоллбэк
 * с ограничением параллелизма (по умолчанию 8).
 */
export async function getSolutionsDetailsBulkOrFallback(ids, { concurrency = 8 } = {}) {
  if (!ids?.length) return [];

  try {
    const { data } = await api.post('/api/admin/solutions/bulk', ids, {
      headers: { 'Content-Type': 'application/json' },
    });
    if (Array.isArray(data)) return data;
  } catch (e) {
    // bulk недоступен — молча уходим на фоллбэк
    console.warn('bulk not available, fallback to per-id:', e?.response?.status, e?.message);
  }

  // Фоллбэк: ограниченный параллелизм
  const mapLimit = async (items, limit, fn) => {
    const res = new Array(items.length);
    let i = 0;
    const workers = Array.from({ length: Math.max(1, limit | 0) }, async () => {
      while (true) {
        const idx = i++;
        if (idx >= items.length) break;
        try {
          res[idx] = await fn(items[idx]);
        } catch {
          res[idx] = null;
        }
      }
    });
    await Promise.all(workers);
    return res.filter(Boolean);
  };

  return mapLimit(ids, concurrency, (id) => getSolutionDetails(id));
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
