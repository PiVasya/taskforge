import api from './http';

// Отправка решения задания на проверку
// Backend: POST /api/assignments/{assignmentId}/submit
export async function submitSolution(assignmentId, payload) {
  const res = await api.post(`/api/assignments/${assignmentId}/submit`, payload);
  return res.data;
}

// История решений текущего пользователя (опционально фильтруем по assignmentId)
// Backend: GET /api/me/solutions?assignmentId=...
export async function listMySolutions(assignmentId) {
  const res = await api.get(`/api/me/solutions`, { params: { assignmentId } });
  return res.data;
}
