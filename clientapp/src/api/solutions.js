import api from './http';

export async function submitSolution(assignmentId, payload) {
  const res = await api.post(`/api/assignments/${assignmentId}/solutions`, payload);
  return res.data;
}

export async function listMySolutions(assignmentId) {
  const res = await api.get(`/api/assignments/${assignmentId}/solutions`);
  return res.data;
}
