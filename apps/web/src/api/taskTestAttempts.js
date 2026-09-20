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


export async function getMyTaskTestAttempts({ courseId = null, assignmentId = null, days = null, skip = 0, take = 50 } = {}) {
  const { data } = await api.get('/api/me/test-attempts', {
    params: { courseId, assignmentId, days, skip, take },
  });
  return Array.isArray(data) ? data : [];
}

export async function getMyTaskTestAttemptReview(attemptId) {
  const { data } = await api.get(`/api/me/test-attempts/${attemptId}`);
  return data;
}



export async function getUserTaskTestAttemptsPage(userId, { courseId = null, assignmentId = null, days = null, skip = 0, take = 50 } = {}) {
  const response = await api.get(`/api/admin/users/${userId}/test-attempts`, {
    params: { courseId, assignmentId, days, skip, take },
  });
  return pageResult(response, userId);
}

export async function getUserTaskTestAttempts(userId, options = {}) {
  const page = await getUserTaskTestAttemptsPage(userId, options);
  return page.items;
}

export async function getAdminTaskTestAttemptReview(attemptId) {
  const { data } = await api.get(`/api/admin/test-attempts/${attemptId}`);
  return data;
}

export async function deleteAdminTaskTestAttempt(attemptId) {
  await api.delete(`/api/admin/test-attempts/${attemptId}`);
}
