import api from './http';

export async function startTaskTest(assignmentId) {
  const { data } = await api.post(`/api/task-tests/${assignmentId}/start`);
  return data;
}

export async function submitTaskTest(assignmentId, payload) {
  const { data } = await api.post(`/api/task-tests/${assignmentId}/submit`, payload);
  return data;
}

export async function getTaskTestEdit(assignmentId) {
  const { data } = await api.get(`/api/task-tests/${assignmentId}/edit`);
  return data;
}

export async function saveTaskTestEdit(assignmentId, payload) {
  await api.put(`/api/task-tests/${assignmentId}/edit`, payload);
}
