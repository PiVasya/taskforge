import api from './http';

// ===== Me =====

export async function getMyTaskTestAttempts({ courseId = null, assignmentId = null, days = null } = {}) {
  const { data } = await api.get('/api/me/test-attempts', {
    params: { courseId, assignmentId, days },
  });
  return Array.isArray(data) ? data : [];
}

export async function getMyTaskTestAttemptReview(attemptId) {
  const { data } = await api.get(`/api/me/test-attempts/${attemptId}`);
  return data;
}

// ===== Admin =====

export async function getUserTaskTestAttempts(userId, { courseId = null, assignmentId = null, days = null } = {}) {
  const { data } = await api.get(`/api/admin/users/${userId}/test-attempts`, {
    params: { courseId, assignmentId, days },
  });
  return Array.isArray(data) ? data : [];
}

export async function getAdminTaskTestAttemptReview(attemptId) {
  const { data } = await api.get(`/api/admin/test-attempts/${attemptId}`);
  return data;
}
