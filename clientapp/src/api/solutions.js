// src/api/solutions.js
import { api } from './http';

/** Технический онлайн-запуск (если ещё где-то нужен) */
export async function compileRun({ language, code, input, timeLimitMs, memoryLimitMb }) {
  const { data } = await api.post(
    `/api/compiler/compile-run`,
    { language, code, input, timeLimitMs, memoryLimitMb },
    { headers: { 'Content-Type': 'application/json' } }
  );
  return data;
}

/**
 * Сабмит решения на СУЩЕСТВУЮЩИЙ бэкенд-эндпоинт.
 * ВАЖНО: путь именно /api/assignments/{assignmentId}/submit (как было раньше).
 * Бэк внутри сам делает: компиляция → смок (1-й публичный тест) → при успехе все тесты.
 */
export async function submitSolution(assignmentId, { language, code }) {
  const token = localStorage.getItem('token');
  const { data } = await api.post(
    `/api/assignments/${assignmentId}/submit`,
    { language, code },
    {
      headers: {
        'Content-Type': 'application/json',
        ...(token ? { Authorization: `Bearer ${token}` } : {})
      }
    }
  );
  return data; // { passedAllTests, passedCount, failedCount, results, compileError? }
}
