import api from './http';

export async function getMyMathAttempts({ courseId, assignmentId, days, skip = 0, take = 50 } = {}) {
  const { data } = await api.get('/api/me/math-attempts', {
    params: { courseId, assignmentId, days, skip, take },
  });
  return Array.isArray(data) ? data : [];
}

export async function getMyMathAttemptReview(attemptId) {
  const { data } = await api.get(`/api/me/math-attempts/${attemptId}`);
  return data;
}

export async function getUserMathAttempts(userId, { courseId, assignmentId, days, skip = 0, take = 50 } = {}) {
  const { data } = await api.get(`/api/admin/users/${userId}/math-attempts`, {
    params: { courseId, assignmentId, days, skip, take },
  });
  return Array.isArray(data) ? data : [];
}

export async function getAdminMathAttemptReview(attemptId) {
  const { data } = await api.get(`/api/admin/math-attempts/${attemptId}`);
  return data;
}

export async function deleteAdminMathAttempt(attemptId) {
  await api.delete(`/api/admin/math-attempts/${attemptId}`);
}
