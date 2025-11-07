// src/api/solutions.js
import { api } from './http';

function authHeaders() {
  const t = localStorage.getItem('token');
  return t ? { Authorization: `Bearer ${t}` } : {};
}

/**
 * (опционально) единичный онлайн-запуск
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
 * Сабмит решения: бэк прогоняет тесты и возвращает результат.
 * POST /api/solutions/{assignmentId}/submit
 */
export async function submitSolution(assignmentId, { language, code }) {
  const { data } = await api.post(
    `/api/solutions/${assignmentId}/submit`,
    { language, code },
    { headers: { 'Content-Type': 'application/json', ...authHeaders() } }
  );
  return data; // { passedAll|passedAllTests, passedCount, failedCount, testCases|cases:[...] }
}
