// clientapp/src/api/solutions.js
import { api } from './http';

/**
 * Технический онлайн-запуск (если ещё где-то нужен).
 * Использует сервис компиляции/запуска без сохранения решения в БД.
 */
export async function compileRun({ language, code, input, timeLimitMs, memoryLimitMb }) {
  const { data } = await api.post(
    '/api/compiler/compile-run',
    { language, code, input, timeLimitMs, memoryLimitMb },
    { headers: { 'Content-Type': 'application/json' } }
  );
  return data;
}

/**
 * Сабмит решения на существующий бэкенд-эндпоинт.
 * ВАЖНО: путь именно /api/assignments/{assignmentId}/submit (как было раньше).
 *
 * Возвращает:
 * {
 *   passedAllTests: boolean,
 *   passedCount: number,
 *   failedCount: number,
 *   results: [...],
 *   compileError?: string
 * }
 */
export async function submitSolution(assignmentId, { language, code }) {
  const token = localStorage.getItem('token');

  const { data } = await api.post(
    `/api/assignments/${assignmentId}/submit`,
    { language, code },
    {
      headers: {
        'Content-Type': 'application/json',
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
      },
    }
  );

  return data;
}

/**
 * Список решений текущего пользователя ("Мои решения").
 *
 * GET /api/me/solutions?courseId=&assignmentId=&skip=&take=&days=
 *
 * Параметры:
 *  - courseId?: Guid
 *  - assignmentId?: Guid
 *  - skip?: number (по умолчанию 0)
 *  - take?: number (по умолчанию 50)
 *  - days?: number | null (если указан — фильтр "за период")
 *
 * Бэкенд возвращает массив SolutionListItemDto.
 */
export async function getMySolutions({
  courseId,
  assignmentId,
  skip = 0,
  take = 50,
  days = null,
} = {}) {
  const { data } = await api.get('/api/me/solutions', {
    params: { courseId, assignmentId, skip, take, days },
  });

  // На бэке это просто массив, но подстрахуемся
  if (Array.isArray(data)) return data;
  if (data && Array.isArray(data.items)) return data.items;
  return [];
}

/**
 * Детали одного решения текущего пользователя (включая код).
 *
 * GET /api/me/solutions/{id}
 *
 * Возвращает SolutionDetailsDto:
 * {
 *   id, userId, taskAssignmentId,
 *   language, submittedCode,
 *   passedAllTests, passedCount, failedCount,
 *   submittedAt, courseTitle, assignmentTitle, cases?
 * }
 */
export async function getMySolutionDetails(solutionId) {
  const { data } = await api.get(`/api/me/solutions/${solutionId}`);
  return data;
}
