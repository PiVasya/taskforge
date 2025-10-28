import { api } from './http';

function authHeaders() {
  const t = localStorage.getItem('token');
  return t ? { Authorization: `Bearer ${t}` } : {};
}

// список заданий по курсу
export async function getAssignmentsByCourse(courseId) {
  const { data } = await api.get(`/api/courses/${courseId}/assignments`, {
    headers: authHeaders(),
  });
  return data;
}

// создать задание
export async function createAssignment(courseId, payload) {
  const res = await api.post(`/api/courses/${courseId}/assignments`, payload, {
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    transformResponse: [(d) => d], // сырой текст
  });
  const txt = res.data ?? '';
  return txt ? JSON.parse(txt) : null; // сервер может вернуть { id }
}

// детальная инфа по заданию
export async function getAssignment(assignmentId) {
  const { data } = await api.get(`/api/assignments/${assignmentId}`, {
    headers: authHeaders(),
  });
  return data;
}

// отправка решения
export async function submitSolution(assignmentId, { language, code }) {
  const res = await api.post(
    `/api/assignments/${assignmentId}/submit`,
    { language, code },
    {
      headers: { 'Content-Type': 'application/json', ...authHeaders() },
      transformResponse: [(d) => d],
    }
  );
  const txt = res.data ?? '';
  return txt ? JSON.parse(txt) : null; // SubmitSolutionResultDto
}

// обновить задание
export async function updateAssignment(assignmentId, payload) {
  await api.put(`/api/assignments/${assignmentId}`, payload, {
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
  });
  return true;
}

// удалить задание
export async function deleteAssignment(assignmentId) {
  await api.delete(`/api/assignments/${assignmentId}`, {
    headers: authHeaders(),
  });
  return true;
}

export async function updateAssignmentSort(assignmentId, sort) {
  await api.patch(`/api/assignments/${assignmentId}/sort`, { sort }, {
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
  });
  return true;
}

// получить список лучших решений для задания
export async function getTopSolutions(assignmentId, count = 5) {
  const { data } = await api.get(`/api/solutions/${assignmentId}/top`, {
    params: { count },
    headers: authHeaders(),
  });
  return data;
}
