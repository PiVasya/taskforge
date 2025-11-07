// src/api/solutions.js
import { api } from './http';

function authHeaders() {
  const t = localStorage.getItem('token');
  return t ? { Authorization: `Bearer ${t}` } : {};
}

/**
 * Единичный «онлайн-запуск» (если пригодится где-то ещё)
 * POST /api/compiler/compile-run
 */
export async function compileRun({ language, code, input, timeLimitMs, memoryLimitMb }) {
  const { data } = await api.post(
    `/api/compiler/compile-run`,
    { language, code, input, timeLimitMs, memoryLimitMb },
    { headers: { 'Content-Type': 'application/json', ...authHeaders() } }
  );
  return data;
}

/**
 * Сабмит решения (если нужен workflow «сохранить решение»)
 * POST /api/solutions/:assignmentId/submit
 */
export async function submitSolution(assignmentId, { language, code }) {
  const { data } = await api.post(
    `/api/solutions/${assignmentId}/submit`,
    { language, code },
    { headers: { 'Content-Type': 'application/json', ...authHeaders() } }
  );
  return data; // { passed, failed, passedAll, ... }
}
