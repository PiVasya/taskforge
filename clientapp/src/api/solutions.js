// src/api/solutions.js
import { api } from './http';

/**
 * Единичный «онлайн-запуск» (оставил на всякий случай — вдруг нужен в других местах UI)
 * POST /api/compiler/compile-run
 */
export async function compileRun({ language, code, input, timeLimitMs, memoryLimitMb }) {
  const { data } = await api.post(
    `/api/compiler/compile-run`,
    { language, code, input, timeLimitMs, memoryLimitMb },
    { headers: { 'Content-Type': 'application/json' } }
  );
  return data; // { status, exitCode, stdout, stderr, ... }
}

/**
 * Прогон тестов через бэкендовый TestsController
 * POST /api/tests/run/tests
 *
 * Ожидает:
 * {
 *   language: 'cpp'|'csharp'|'python',
 *   code: '...',
 *   testCases: [{ input, expectedOutput }],
 *   timeLimitMs?: number,
 *   memoryLimitMb?: number
 * }
 *
 * Возвращает:
 * { results: [{ input, expected, actual, passed }] }
 */
export async function runTestsForAssignment({
  language,
  source,          // код решения
  testCases,       // массив кейсов { input, expectedOutput }
  timeLimitMs,
  memoryLimitMb,
}) {
  const { data } = await api.post(
    `/api/tests/run/tests`,
    {
      language,
      code: source,
      testCases,
      timeLimitMs,
      memoryLimitMb,
    },
    { headers: { 'Content-Type': 'application/json' } }
  );
  return data; // { results: [...] }
}
