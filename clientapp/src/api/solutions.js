import api from './http';

function emitQuotaChanged() {
  try { window.dispatchEvent(new Event('quota:changed')); } catch {}
}

// Отправка решения задания на проверку
// Backend: POST /api/assignments/{assignmentId}/submit
export async function submitSolution(assignmentId, payload) {
  const res = await api.post(`/api/assignments/${assignmentId}/submit`, payload);
  emitQuotaChanged();
  return res.data;
}

// История решений текущего пользователя (опционально фильтруем по assignmentId)
// Backend: GET /api/me/solutions?assignmentId=...
export async function listMySolutions(assignmentId) {
  const res = await api.get(`/api/me/solutions`, { params: { assignmentId } });
  return res.data;
}

// История решений текущего пользователя (расширенная):
// Backend: GET /api/me/solutions?courseId=&assignmentId=&skip=&take=&days=
// В проекте часть страниц зовёт getMySolutions({ days: ... })
export async function getMySolutions(opts = {}) {
  // Поддержка старого вызова: getMySolutions(assignmentId)
  const params = {};

  if (opts && typeof opts === 'object' && !Array.isArray(opts)) {
    if (opts.courseId) params.courseId = opts.courseId;
    if (opts.assignmentId) params.assignmentId = opts.assignmentId;
    if (Number.isFinite(opts.skip)) params.skip = opts.skip;
    if (Number.isFinite(opts.take)) params.take = opts.take;
    if (Number.isFinite(opts.days) || opts.days === null) params.days = opts.days;
  } else if (opts) {
    params.assignmentId = opts;
  }

  const res = await api.get(`/api/me/solutions`, { params });
  return res.data;
}

// Детали решения текущего пользователя
// Backend: GET /api/me/solutions/{id}
export async function getMySolutionDetails(id) {
  const res = await api.get(`/api/me/solutions/${id}`);
  return res.data;
}
