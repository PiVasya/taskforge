import api, { authHeaders } from './base';

/** Поиск пользователей (разово) */
export async function searchUsersOnce(q = '', take = 20) {
  const { data } = await api.get('/api/admin/users', {
    params: { q, take },
    headers: { ...authHeaders() },
  });
  return data;
}

/** Список решений пользователя (лайт) */
export async function getUserSolutions(userId, { skip = 0, take = 50, days = null } = {}) {
  // новый роут
  try {
    const { data } = await api.get(`/api/admin/users/${userId}/solutions`, {
      params: { skip, take, days },
      headers: { ...authHeaders() },
    });
    return data;
  } catch (e) {
    // совместимость со старым роутом
    if (e?.response?.status === 404) {
      const { data } = await api.get('/api/admin/solutions', {
        params: { userId, skip, take, days },
        headers: { ...authHeaders() },
      });
      return data;
    }
    throw e;
  }
}

/** Детали одного решения */
export async function getSolutionDetails(solutionId) {
  const { data } = await api.get(`/api/admin/solutions/${solutionId}`, {
    headers: { ...authHeaders() },
  });
  return data;
}

/** Bulk-детали (с фоллбэком на поштучную загрузку с ограничением параллельности) */
export async function getSolutionsDetailsBulkOrFallback(ids, { concurrency = 8 } = {}) {
  if (!ids?.length) return [];
  try {
    const { data } = await api.post(
      '/api/admin/solutions/bulk',
      ids,
      { headers: { 'Content-Type': 'application/json', ...authHeaders() } }
    );
    if (Array.isArray(data)) return data;
  } catch (e) {
    // если ручки нет — уйдём на фоллбэк
  }

  const mapLimit = async (items, limit, fn) => {
    const res = new Array(items.length);
    let i = 0;
    const workers = Array.from({ length: Math.max(1, limit | 0) }, async () => {
      while (true) {
        const idx = i++;
        if (idx >= items.length) break;
        try { res[idx] = await fn(items[idx]); } catch { res[idx] = null; }
      }
    });
    await Promise.all(workers);
    return res;
  };

  const details = await mapLimit(ids, concurrency, (id) => getSolutionDetails(id));
  return details.filter(Boolean);
}

/** Удаление всех решений пользователя */
export async function deleteAllUserSolutions(userId, { courseId = null, assignmentId = null } = {}) {
  try {
    await api.delete(`/api/admin/users/${userId}/solutions`, {
      params: { courseId, assignmentId },
      headers: { ...authHeaders() },
    });
  } catch (e) {
    if (e?.response?.status === 404) {
      await api.delete('/api/admin/solutions', {
        params: { userId, courseId, assignmentId },
        headers: { ...authHeaders() },
      });
      return;
    }
    throw e;
  }
}
