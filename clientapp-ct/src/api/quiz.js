import api from './http';

export async function getQuizTasks(params = {}) {
  const res = await api.get('/api/quiz/tasks', { params });
  return res.data;
}

export async function getQuizTask(idOrSlug) {
  const res = await api.get(`/api/quiz/tasks/${encodeURIComponent(idOrSlug)}`);
  return res.data;
}

export async function submitQuizAttempt(taskId, answer, { clientAttemptId, timeSpentSeconds } = {}) {
  const res = await api.post(`/api/quiz/tasks/${taskId}/attempts`, {
    clientAttemptId,
    answer,
    timeSpentSeconds,
  });
  return res.data;
}

export async function getMyQuizProgress(params = {}) {
  const res = await api.get('/api/quiz/me/progress', { params });
  return res.data;
}

export async function getMyQuizSolutions(params = {}) {
  const res = await api.get('/api/quiz/me/solutions', { params });
  return res.data;
}

export async function getAdminQuizTasks(params = {}) {
  const res = await api.get('/api/admin/quiz/tasks', { params });
  return res.data;
}

export async function getAdminQuizTask(id) {
  const res = await api.get(`/api/admin/quiz/tasks/${encodeURIComponent(id)}`);
  return res.data;
}

export async function createQuizTask(payload) {
  const res = await api.post('/api/admin/quiz/tasks', payload);
  return res.data;
}

export async function updateQuizTask(taskId, payload) {
  const res = await api.put(`/api/admin/quiz/tasks/${encodeURIComponent(taskId)}`, payload);
  return res.data;
}

export async function deleteQuizTask(taskId) {
  await api.delete(`/api/admin/quiz/tasks/${encodeURIComponent(taskId)}`);
}
