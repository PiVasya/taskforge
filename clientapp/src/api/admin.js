import { api } from './http';

export async function searchUsers(q, take = 20) {
  const { data } = await api.get(`/api/admin/users`, { params: { q, take }});
  return data;
}

export async function getUserSolutions(userId, { courseId, assignmentId, skip=0, take=50 } = {}) {
  const { data } = await api.get(`/api/admin/users/${userId}/solutions`, {
    params: { courseId, assignmentId, skip, take }
  });
  return data;
}

export async function getSolutionDetails(id) {
  const { data } = await api.get(`/api/admin/solutions/${id}`);
  return data;
}

export async function getLeaderboard({ courseId, days, top=20 } = {}) {
  const { data } = await api.get(`/api/admin/leaderboard`, { params: { courseId, days, top }});
  return data;
}
