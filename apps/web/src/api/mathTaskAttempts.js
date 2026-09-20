import api from './http';

function pageResult(response, requestedUserId) {
  const items = Array.isArray(response?.data) ? response.data : [];
  const parsedTotal = Number(response?.headers?.['x-total-count']);
  return {
    items,
    total: Number.isFinite(parsedTotal) && parsedTotal >= 0 ? parsedTotal : items.length,
    resultUserId: String(response?.headers?.['x-result-user-id'] || requestedUserId || ''),
  };
}

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

export async function getUserMathAttemptsPage(userId, { courseId, assignmentId, days, skip = 0, take = 50 } = {}) {
  const response = await api.get(`/api/admin/users/${userId}/math-attempts`, {
    params: { courseId, assignmentId, days, skip, take },
  });
  return pageResult(response, userId);
}

export async function getUserMathAttempts(userId, options = {}) {
  const page = await getUserMathAttemptsPage(userId, options);
  return page.items;
}

export async function getAdminMathAttemptReview(attemptId) {
  const { data } = await api.get(`/api/admin/math-attempts/${attemptId}`);
  return data;
}

export async function deleteAdminMathAttempt(attemptId) {
  await api.delete(`/api/admin/math-attempts/${attemptId}`);
}
