import api from './http';

const BASE = '/api/compiler';


export async function compileRun({ language, code, input }) {
  const { data } = await api.post(`${BASE}/compile-run`, { language, code, input }, {
    headers: { 'Content-Type': 'application/json' },
  });
  return data; 
}


export async function runTests({ language, code, testCases }) {
  const { data } = await api.post(`${BASE}/run-tests`, { language, code, testCases }, {
    headers: { 'Content-Type': 'application/json' },
  });
  return data; 
}


export async function createInteractiveCompilerSession({
  language,
  code,
  columns,
  rows,
  timeLimitMs,
  memoryLimitMb,
}) {
  const { data } = await api.post(`${BASE}/sessions`, {
    language,
    code,
    columns,
    rows,
    timeLimitMs,
    memoryLimitMb,
  }, {
    headers: { 'Content-Type': 'application/json' },
  });
  return data;
}
