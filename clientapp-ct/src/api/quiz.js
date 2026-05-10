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

export async function createQuizTask(payload) {
  const res = await api.post('/api/admin/quiz/tasks', payload);
  return res.data;
}
