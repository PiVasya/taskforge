// src/api/solutions.js
import { api } from './http';

function authHeaders() {
  const t = localStorage.getItem('token');
  return t ? { Authorization: `Bearer ${t}` } : {};
}

/**
 * Сабмит решения на бэкенд.
 * POST /api/solutions/{assignmentId}/submit
 *
 * @param {string} assignmentId
 * @param {{ language: 'cpp'|'csharp'|'python', code: string }} payload
 * @returns {Promise<any>} ожидается { passed, failed, passedAll? ... }
 */
export async function submitSolution(assignmentId, { language, code }) {
  const { data } = await api.post(
    `/api/solutions/${assignmentId}/submit`,
    { language, code },
    { headers: { 'Content-Type': 'application/json', ...authHeaders() } }
  );
  return data;
}
