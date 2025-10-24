import { api } from './http';

// Поиск студентов — вызываем ТОЛЬКО по Enter на странице
export async function searchUsersOnce(q, take = 20) {
  const { data } = await api.get(`/api/admin/users`, { params: { q, take }});
  return data;
}

// Пагинированный список решений (без кода). Для отображения всех решений можно указать большое значение take.
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

// Лидерборд по количеству решённых заданий. days — за сколько дней считать, top — количество пользователей.
export async function getLeaderboard({ courseId, days, top=20 } = {}) {
  const { data } = await api.get(`/api/admin/leaderboard`, { params: { courseId, days, top }});
  return data;
}

// Удаление всех решений пользователя, с возможностью фильтрации по курсу или заданию.
// Параметры courseId и assignmentId необязательны — при их отсутствии удаляются все решения пользователя.
export async function deleteUserSolutions(userId, { courseId, assignmentId } = {}) {
  await api.delete(`/api/admin/users/${userId}/solutions`, { params: { courseId, assignmentId } });
}