import { api } from './http';

// Поиск студентов — вызываем ТОЛЬКО по Enter на странице
export async function searchUsersOnce(q, take = 20) {
  const { data } = await api.get(`/api/admin/users`, { params: { q, take }});
  return data;
}

// Пагинированный список решений (без кода)
export async function getUserSolutions(userId, { courseId, assignmentId, skip=0, take=20 } = {}) {
  const { data } = await api.get(`/api/admin/users/${userId}/solutions`, {
    params: { courseId, assignmentId, skip, take }
  });
  return data;
}

// Детали одного решения (с кодом и подсчётами)
export async function getSolutionDetails(id) {
  const { data } = await api.get(`/api/admin/solutions/${id}`);
  return data;
}

export async function getLeaderboard({ courseId, days, top=20 } = {}) {
  const { data } = await api.get(`/api/admin/leaderboard`, { params: { courseId, days, top }});
  return data;
}
