// clientapp/src/api/admin.js
import { api } from './http';

/**
 * Поиск пользователей (email/имя/фамилия)
 * backend: GET /api/admin/users?q=...&take=...
 */
export async function searchUsersOnce(q = '', take = 20) {
  const { data } = await api.get('/api/admin/users', {
    params: { q, take },
  });
  return Array.isArray(data) ? data : [];
}

/**
 * Список решений пользователя (лайт, без кода).
 * backend: GET /api/admin/users/{userId}/solutions?skip=&take=&courseId=&assignmentId=&days=
 */
export async function getUserSolutions(userId, { skip = 0, take = 50, courseId, assignmentId, days } = {}) {
  const { data } = await api.get(`/api/admin/users/${userId}/solutions`, {
    params: { skip, take, courseId, assignmentId, days },
  });
  return Array.isArray(data) ? data : (data?.items ?? []);
}

/**
 * Детали одного решения (с кодом).
 * backend: GET /api/admin/solutions/{id}
 */
export async function getSolutionDetails(solutionId) {
  const { data } = await api.get(`/api/admin/solutions/${solutionId}`);
  return data;
}

/**
 * Удаление всех решений пользователя (можно дать фильтры через контроллер — при необходимости).
 * backend: DELETE /api/admin/users/{userId}/solutions
 */
export async function deleteUserSolutions(userId, { courseId, assignmentId } = {}) {
  await api.delete(`/api/admin/users/${userId}/solutions`, {
    params: { courseId, assignmentId },
  });
}

/**
 * Лидерборд
 * backend: GET /api/admin/leaderboard?courseId=&days=&top=
 */
export async function getLeaderboard({ courseId, days, top = 20 } = {}) {
  const { data } = await api.get('/api/admin/leaderboard', {
    params: { courseId, days, top },
  });
  return Array.isArray(data) ? data : [];
}
