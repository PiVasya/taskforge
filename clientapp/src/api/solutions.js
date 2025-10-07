import { api } from './http';

// запуск "богатого" джаджа
export async function runSolutionRich({ assignmentId, language, source, stdin, timeLimitMs, memoryLimitMb }) {
  const { data } = await api.post('/api/judge/run', {
    assignmentId, language, source, stdin, timeLimitMs, memoryLimitMb
  });
  return data; // { status, message, compile, run, tests, metrics, version }
}
