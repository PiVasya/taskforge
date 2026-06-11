const PENDING_STATUSES = new Set(['preparing', 'queued', 'running', 'pending']);
const SUCCESS_STATUSES = new Set(['accepted', 'ok', 'success', 'passed']);
const DANGER_STATUSES = new Set([
  'rejected',
  'compileerror',
  'compile_error',
  'compilation_error',
  'policyfailed',
  'policy_failed',
  'notestsconfigured',
  'judgeunavailable',
  'languagenotallowed',
  'failed',
]);

function isObject(value) {
  return value && typeof value === 'object' && !Array.isArray(value);
}

function firstNonEmpty(...values) {
  for (const value of values) {
    if (value === null || value === undefined) continue;
    const text = String(value);
    if (text.trim().length > 0) return text;
  }
  return '';
}

function firstNumber(...values) {
  for (const value of values) {
    if (typeof value === 'number' && Number.isFinite(value)) return value;
    if (typeof value === 'string' && value.trim() !== '' && Number.isFinite(Number(value))) return Number(value);
  }
  return null;
}

function normalizeStatus(value) {
  return String(value || '').replace(/[\s_-]+/g, '').trim().toLowerCase();
}

export function shortId(id) {
  const text = String(id || '').trim();
  return text ? text.slice(0, 8) : '—';
}

export function getSolutionCode(solution) {
  return firstNonEmpty(solution?.submittedCode, solution?.code, solution?.Code, solution?.sourceCode, solution?.source);
}

export function getSolutionResult(solution) {
  return isObject(solution?.result) ? solution.result : null;
}

export function getSolutionStatus(solution) {
  const result = getSolutionResult(solution);
  if (solution?.isPending === true || result?.pending === true) {
    return firstNonEmpty(solution?.status, solution?.verdict, result?.status, result?.verdict, 'Queued');
  }
  if (solution?.passedAllTests === true || solution?.passedAll === true) return 'Accepted';
  if (solution?.compileError === true) return 'CompileError';
  if (solution?.policyFailed === true) return 'PolicyFailed';
  return firstNonEmpty(solution?.status, solution?.verdict, result?.status, result?.verdict, solution?.passed === true ? 'Accepted' : '');
}

export function isPendingSolution(solution) {
  const status = normalizeStatus(getSolutionStatus(solution));
  return solution?.isPending === true || getSolutionResult(solution)?.pending === true || PENDING_STATUSES.has(status);
}

export function isAcceptedSolution(solution) {
  const status = normalizeStatus(getSolutionStatus(solution));
  const cases = getResultCases(solution);
  if (solution?.passedAllTests === true || solution?.passedAll === true) return true;
  if (solution?.passedAllTests === false || solution?.passedAll === false) return false;
  if (cases.length > 0) return cases.every(isResultCasePassed);
  return SUCCESS_STATUSES.has(status);
}

export function getSolutionStatusLabel(solutionOrStatus) {
  const raw = typeof solutionOrStatus === 'string' ? solutionOrStatus : getSolutionStatus(solutionOrStatus);
  const status = normalizeStatus(raw);
  if (!status) return 'Статус неизвестен';
  if (status === 'accepted' || status === 'ok' || status === 'success' || status === 'passed') return 'Принято';
  if (status === 'rejected' || status === 'failed') return 'Не принято';
  if (status === 'compileerror' || status === 'compilationerror') return 'Ошибка компиляции';
  if (status === 'policyfailed') return 'Отклонено анализатором';
  if (status === 'notestsconfigured') return 'Нет тестов';
  if (status === 'judgeunavailable') return 'Проверка недоступна';
  if (status === 'languagenotallowed') return 'Язык не разрешён';
  if (status === 'queued' || status === 'pending') return 'В очереди';
  if (status === 'preparing') return 'Готовится';
  if (status === 'running') return 'Проверяется';
  return raw || 'Статус неизвестен';
}

export function getSolutionStatusIntent(solutionOrStatus) {
  const status = normalizeStatus(typeof solutionOrStatus === 'string' ? solutionOrStatus : getSolutionStatus(solutionOrStatus));
  if (SUCCESS_STATUSES.has(status)) return 'success';
  if (DANGER_STATUSES.has(status)) return 'danger';
  return 'secondary';
}

export function getSolutionScore(solution) {
  return firstNumber(solution?.score, solution?.Score, solution?.result?.score, solution?.result?.scorePercent);
}

export function getSolutionDate(solution) {
  return firstNonEmpty(solution?.submittedAt, solution?.createdAt, solution?.createdAtUtc, solution?.CreatedAt);
}

export function formatDateTime(value) {
  const raw = String(value || '').trim();
  if (!raw) return 'дата неизвестна';
  const date = new Date(raw);
  return Number.isNaN(date.getTime()) ? raw : date.toLocaleString();
}

export function dateMs(value) {
  const date = new Date(value || 0);
  const ms = date.getTime();
  return Number.isNaN(ms) ? 0 : ms;
}

export function getSolutionTitle(solution) {
  const course = firstNonEmpty(solution?.courseTitle, solution?.courseName);
  const assignment = firstNonEmpty(solution?.assignmentTitle, solution?.taskTitle, solution?.title);
  if (course && assignment) return `${course} • ${assignment}`;
  if (assignment) return assignment;
  return `Задание ${shortId(solution?.assignmentId ?? solution?.taskAssignmentId)}`;
}

export function getResultCases(solution) {
  const result = getSolutionResult(solution);
  const candidates = [solution?.cases, solution?.testCases, solution?.results, result?.cases, result?.testCases, result?.results];
  for (const value of candidates) {
    if (Array.isArray(value)) return value;
  }
  return [];
}


export function isResultCasePassed(item) {
  if (!item || typeof item !== 'object') return false;
  if (typeof item.passed === 'boolean') return item.passed;
  if (typeof item.Passed === 'boolean') return item.Passed;
  const status = normalizeStatus(item.status ?? item.Status);
  return status === 'accepted' || status === 'passed' || status === 'success' || status === 'ok';
}

export function getSolutionCounts(solution) {
  const explicitPassed = firstNumber(solution?.passedCount, solution?.passedTests, solution?.result?.passedCount);
  const explicitFailed = firstNumber(solution?.failedCount, solution?.failedTests, solution?.result?.failedCount);
  if (explicitPassed !== null || explicitFailed !== null) {
    return { passed: explicitPassed ?? 0, failed: explicitFailed ?? 0, total: (explicitPassed ?? 0) + (explicitFailed ?? 0) };
  }

  const cases = getResultCases(solution);
  if (!cases.length) return null;

  let passed = 0;
  let failed = 0;
  for (const item of cases) {
    const ok = isResultCasePassed(item);
    if (ok) passed += 1;
    else failed += 1;
  }
  return { passed, failed, total: cases.length };
}

export function getSolutionMessage(solution) {
  return firstNonEmpty(
    solution?.message,
    solution?.error,
    solution?.result?.message,
    solution?.result?.error,
    solution?.result?.detail,
    solution?.detail
  );
}

export function getSolutionOutput(solution, name) {
  const result = getSolutionResult(solution);
  const compileStderr = name === 'stderr' ? firstNonEmpty(solution?.compileStderr, result?.compileStderr) : '';
  return firstNonEmpty(solution?.[name], result?.[name], compileStderr);
}

export function getImageReferenceUrl(solution) {
  return firstNonEmpty(solution?.referenceUrl, solution?.expectedUrl, solution?.result?.referenceUrl, solution?.result?.expectedUrl);
}

export function getImageSubmittedUrl(solution) {
  return firstNonEmpty(
    solution?.submittedUrl,
    solution?.actualUrl,
    solution?.renderedUrl,
    solution?.result?.submittedUrl,
    solution?.result?.actualUrl,
    solution?.result?.renderedUrl
  );
}

function percentFromValue(value) {
  const n = firstNumber(value);
  if (n === null) return null;
  return n > 0 && n <= 1 ? n * 100 : n;
}

export function getImageSimilarityPercent(solution) {
  return percentFromValue(solution?.similarityPercent ?? solution?.similarity ?? solution?.result?.similarityPercent ?? solution?.result?.similarity);
}

export function getImageThresholdPercent(solution) {
  return percentFromValue(solution?.thresholdPercent ?? solution?.threshold ?? solution?.result?.thresholdPercent ?? solution?.result?.threshold);
}
