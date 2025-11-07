// src/api/solutions.js
import { api } from './http';

function authHeaders() {
  const t = localStorage.getItem('token');
  return t ? { Authorization: `Bearer ${t}` } : {};
}

/**
 * (опционально) единичный онлайн-запуск — оставляю, вдруг нужен ещё где-то
 * POST /api/compiler/compile-run
 */
export async function compileRun({ language, code, input, timeLimitMs, memoryLimitMb }) {
  const { data } = await api.post(
    `/api/compiler/compile-run`,
    { language, code, input, timeLimitMs, memoryLimitMb },
    { headers: { 'Content-Type': 'application/json', ...authHeaders() } }
  );
  return data; // { status, exitCode, stdout, stderr, ... }
}

/**
 * Сабмит решения: бэк прогоняет тесты и возвращает агрегированный результат.
 * POST /api/solutions/{assignmentId}/submit
 */
export async function submitSolution(assignmentId, { language, code }) {
  const { data } = await api.post(
    `/api/solutions/${assignmentId}/submit`,
    { language, code },
    { headers: { 'Content-Type': 'application/json', ...authHeaders() } }
  );
  return data; // ожидается: { passedAll, passedCount, failedCount, testCases|cases:[...] }
}
