import { api } from './http';

const BASE = '/api/compiler';

// компиляция и запуск
export async function compileRun({ language, code, input }) {
  const { data } = await api.post(`${BASE}/compile-run`, { language, code, input }, {
    headers: { 'Content-Type': 'application/json' },
  });
  return data; // { output?, error? }
}

// запуск набора тестов
export async function runTests({ language, code, testCases }) {
  const { data } = await api.post(`${BASE}/run-tests`, { language, code, testCases }, {
    headers: { 'Content-Type': 'application/json' },
  });
  return data; // { results: [...] }
}
