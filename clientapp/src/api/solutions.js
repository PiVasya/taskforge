// src/api/solutions.js
import { api } from './http';

const BASE = '/api/compiler';

// унифицированный «богатый» запуск: компиляция + возможный stdin, лимиты (опц.)
export async function runSolutionRich({
  assignmentId,        // можно игнорить здесь — бэку не нужен
  language,
  source,              // имя поля у тебя в странице — source
  stdin,
  timeLimitMs,
  memoryLimitMb,
}) {
  const { data } = await api.post(
    `${BASE}/compile-run`,
    {
      language,
      code: source,     // <— БЭК ждёт code
      input: stdin,
      timeLimitMs,
      memoryLimitMb,
    },
    { headers: { 'Content-Type': 'application/json' } }
  );
  return data; // { status, exitCode, stdout/stderr … }
}

// запуск набора тестов (если используешь)
export async function runTestsRich({ language, source, testCases, timeLimitMs, memoryLimitMb }) {
  const { data } = await api.post(
    `${BASE}/run-tests`,
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
