const STATUS_LABELS = {
  preparing: 'Готовится',
  queued: 'В очереди',
  running: 'Проверяется',
  pending: 'В очереди',
  accepted: 'Принято',
  ok: 'Принято',
  passed: 'Принято',
  success: 'Принято',
  rejected: 'Не принято',
  failed: 'Не принято',
  wronganswer: 'Неверный ответ',
  runtimeerror: 'Ошибка выполнения',
  timeexceeded: 'Превышено время',
  memoryexceeded: 'Превышена память',
  compileerror: 'Ошибка компиляции',
  compilationerror: 'Ошибка компиляции',
  policyfailed: 'Отклонено анализатором',
  notestsconfigured: 'Нет тестов',
  judgeunavailable: 'Проверка недоступна',
  languagenotallowed: 'Язык не разрешён',
  error: 'Ошибка',
};

const PENDING_STATUSES = new Set(['preparing', 'queued', 'running', 'pending']);
const ACCEPTED_STATUSES = new Set(['accepted', 'passed', 'success']);
const DANGER_STATUSES = new Set([
  'wronganswer',
  'runtimeerror',
  'timeexceeded',
  'memoryexceeded',
  'compileerror',
  'compilationerror',
  'policyfailed',
  'notestsconfigured',
  'judgeunavailable',
  'languagenotallowed',
  'rejected',
  'failed',
  'error',
]);

function isObject(value) {
  return value && typeof value === 'object' && !Array.isArray(value);
}

function parseMaybeJson(value) {
  if (!value) return value;
  if (typeof value !== 'string') return value;
  try {
    return JSON.parse(value);
  } catch {
    return { raw: value };
  }
}

function firstPresent(...values) {
  for (const value of values) {
    if (value !== undefined && value !== null && value !== '') return value;
  }
  return undefined;
}

function firstNumber(...values) {
  for (const value of values) {
    if (typeof value === 'number' && Number.isFinite(value)) return value;
    if (typeof value === 'string' && value.trim() !== '' && Number.isFinite(Number(value))) return Number(value);
  }
  return null;
}

function toStatusKey(value) {
  return String(value || '').trim().replace(/[\s_-]+/g, '').toLowerCase();
}

function asArray(value) {
  return Array.isArray(value) ? value : [];
}

function capitalized(key) {
  if (!key) return key;
  return key[0].toUpperCase() + key.slice(1);
}

function lookup(solution, keys) {
  const result = getSolutionResult(solution);
  const nested = isObject(result?.result) ? result.result : null;
  const rawValue = parseMaybeJson(result?.raw ?? result?.Raw);
  const raw = isObject(rawValue) ? rawValue : null;
  const rawNested = isObject(raw?.result) ? raw.result : null;
  const cases = asArray(firstPresent(result?.cases, result?.results, result?.testCases));
  const firstCase = isObject(cases[0]) ? cases[0] : null;
  for (const key of keys) {
    const alt = capitalized(key);
    const value = firstPresent(
      solution?.[key],
      solution?.[alt],
      result?.[key],
      result?.[alt],
      nested?.[key],
      nested?.[alt],
      raw?.[key],
      raw?.[alt],
      rawNested?.[key],
      rawNested?.[alt],
      firstCase?.[key],
      firstCase?.[alt]
    );
    if (value !== undefined) return value;
  }
  return undefined;
}

function normalizePercent(value) {
  const n = firstNumber(value);
  if (n === null) return null;
  const percent = n >= 0 && n <= 1 ? n * 100 : n;
  return Math.round(percent * 10) / 10;
}

function dataUrlFromBase64(base64, contentType = 'image/png') {
  if (!base64 || typeof base64 !== 'string') return undefined;
  if (base64.startsWith('data:')) return base64;
  const ct = String(contentType || 'image/png').trim() || 'image/png';
  return `data:${ct};base64,${base64}`;
}

export function getSolutionResult(solution) {
  const direct = parseMaybeJson(firstPresent(solution?.result, solution?.Result, solution?.resultJson, solution?.ResultJson));
  return isObject(direct) ? direct : direct || null;
}

export function getSolutionCases(solution) {
  const result = getSolutionResult(solution);
  return asArray(firstPresent(
    solution?.cases,
    solution?.testCases,
    solution?.results,
    result?.cases,
    result?.testCases,
    result?.results,
    result?.Tests,
    result?.Runs,
    result?.runResults
  ));
}

export function getSolutionCode(solution) {
  return String(lookup(solution, ['submittedCode', 'code', 'sourceCode', 'source']) || '');
}

export function getSolutionStatus(solution) {
  if (solution?.isPending === true || getSolutionResult(solution)?.pending === true) {
    return String(lookup(solution, ['status', 'verdict']) || 'Queued').trim();
  }
  if (solution?.passedAllTests === true || solution?.passedAll === true) return 'Accepted';
  if (solution?.compileError === true || solution?.CompileError === true) return 'CompileError';
  if (solution?.policyFailed === true || solution?.PolicyFailed === true) return 'PolicyFailed';
  return String(lookup(solution, ['status', 'verdict']) || (solution?.passed === true || solution?.Passed === true ? 'Accepted' : '')).trim();
}

export function getSolutionStatusKey(solution) {
  return toStatusKey(getSolutionStatus(solution));
}

export function getSolutionStatusLabel(solution) {
  const status = getSolutionStatus(solution);
  const key = toStatusKey(status);
  return STATUS_LABELS[key] || status || 'Статус неизвестен';
}

export function isSolutionPending(solution) {
  const key = getSolutionStatusKey(solution);
  const result = getSolutionResult(solution);
  return solution?.isPending === true || solution?.IsPending === true || result?.pending === true || PENDING_STATUSES.has(key);
}


export function isResultCasePassed(c) {
  if (!c || typeof c !== 'object') return false;
  if (typeof c.passed === 'boolean') return c.passed;
  if (typeof c.Passed === 'boolean') return c.Passed;
  const status = toStatusKey(c.status ?? c.Status);
  return status === 'accepted' || status === 'passed' || status === 'success';
}

export function isSolutionAccepted(solution) {
  const key = getSolutionStatusKey(solution);
  const cases = getSolutionCases(solution);
  if (solution?.passedAllTests === true || solution?.passedAll === true || solution?.PassedAllTests === true || solution?.PassedAll === true) return true;
  if (solution?.passedAllTests === false || solution?.passedAll === false || solution?.PassedAllTests === false || solution?.PassedAll === false) return false;
  if (cases.length > 0) return cases.every(isResultCasePassed);
  return ACCEPTED_STATUSES.has(key);
}

export function getSolutionBadgeIntent(solution) {
  if (isSolutionAccepted(solution)) return 'success';
  if (isSolutionPending(solution)) return 'secondary';
  return DANGER_STATUSES.has(getSolutionStatusKey(solution)) ? 'danger' : 'secondary';
}

export function getSolutionScore(solution) {
  return firstNumber(lookup(solution, ['score', 'scorePercent']));
}

export function getSolutionDate(solution) {
  return firstPresent(
    solution?.submittedAt,
    solution?.SubmittedAt,
    solution?.createdAt,
    solution?.CreatedAt,
    solution?.createdAtUtc,
    solution?.CreatedAtUtc,
    solution?.timestamp,
    solution?.date
  );
}

export function formatDateTime(value) {
  if (!value) return 'дата неизвестна';
  const d = new Date(value);
  return Number.isNaN(d.getTime()) ? String(value) : d.toLocaleString();
}


export function getAssignmentId(solution) {
  const value = firstPresent(
    solution?.assignmentId,
    solution?.AssignmentId,
    solution?.taskAssignmentId,
    solution?.TaskAssignmentId,
    solution?.assignment?.id,
    solution?.assignment?.Id
  );
  return value ? String(value) : '';
}

export function getAssignmentLabel(solution, fallback = 'Задание') {
  const title = String(firstPresent(
    solution?.assignmentTitle,
    solution?.AssignmentTitle,
    solution?.title,
    solution?.Title,
    solution?.assignmentName,
    solution?.AssignmentName,
    lookup(solution, ['assignmentTitle', 'title', 'assignmentName'])
  ) || '').trim();
  if (title) return title;
  const id = firstPresent(solution?.assignmentId, solution?.AssignmentId, solution?.taskAssignmentId);
  return id ? (fallback || 'Задание без названия') : fallback;
}

export function getCourseAssignmentLabel(solution) {
  const course = String(firstPresent(solution?.courseTitle, solution?.CourseTitle, solution?.courseName, solution?.CourseName) || '').trim();
  const assignment = getAssignmentLabel(solution);
  return course ? `${course} • ${assignment}` : assignment;
}

export function getPassedCount(solution) {
  const value = firstNumber(solution?.passedCount, solution?.PassedCount, lookup(solution, ['passedCount', 'passedTests']));
  if (value !== null) return value;
  const cases = getSolutionCases(solution);
  return cases.length ? cases.filter(isResultCasePassed).length : null;
}

export function getFailedCount(solution) {
  const value = firstNumber(solution?.failedCount, solution?.FailedCount, lookup(solution, ['failedCount', 'failedTests']));
  if (value !== null) return value;
  const cases = getSolutionCases(solution);
  const passed = getPassedCount(solution);
  return cases.length && passed !== null ? cases.length - passed : null;
}

export function getImageSolutionCode(solution) {
  return getSolutionCode(solution);
}

export function getImageSolutionDate(solution) {
  return getSolutionDate(solution);
}

export function getImageUrl(solution, kind) {
  if (kind === 'reference') {
    const direct = firstPresent(
      lookup(solution, ['referenceUrl', 'expectedUrl', 'referenceDataUrl', 'expectedDataUrl']),
      solution?.referenceUrl,
      solution?.expectedUrl
    );
    if (direct) return direct;
    return dataUrlFromBase64(lookup(solution, ['referenceBase64', 'expectedBase64']), lookup(solution, ['referenceContentType', 'expectedContentType']));
  }
  const direct = firstPresent(
    lookup(solution, ['submittedUrl', 'actualUrl', 'renderedUrl', 'submissionUrl', 'actualDataUrl', 'submittedDataUrl']),
    solution?.submittedUrl,
    solution?.actualUrl,
    solution?.renderedUrl
  );
  if (direct) return direct;
  return dataUrlFromBase64(lookup(solution, ['pngBase64', 'actualBase64', 'submittedBase64', 'imageBase64']), lookup(solution, ['actualContentType', 'submittedContentType']));
}

export function getImageSimilarity(solution) {
  return normalizePercent(lookup(solution, ['similarityPercent', 'similarity', 'combined_similarity', 'percent', 'score']));
}

export function getImageThreshold(solution) {
  return normalizePercent(lookup(solution, ['thresholdPercent', 'threshold']));
}

export function getTextOutput(solution, key) {
  return String(lookup(solution, [key]) || '');
}

export function getRunnerError(solution) {
  return String(lookup(solution, [
    'runnerError',
    'compileErrorText',
    'compileStderr',
    'compilerStderr',
    'stderr',
    'diagnostic',
    'error',
    'message',
    'detail',
  ]) || '');
}

function compilerSourceLine(solution, lineNumber) {
  const line = Number(lineNumber);
  if (!Number.isFinite(line) || line <= 0) return '';
  const source = getSolutionCode(solution);
  if (!source) return '';
  return String(source).split(/\r?\n/)[line - 1] || '';
}

function parseCompilerDiagnosticLine(line) {
  const text = String(line || '').trim();
  if (!text) return null;

  // C#/MSVC/FPC-style: source.cs(7,15): error CS1002: ; expected
  let match = text.match(/^(.+?)\((\d+),(\d+)\)\s*:?\s*(?:(fatal error|fatal|error|warning|note)\s*)?(?:([A-Za-z]+\d+)\s*:\s*)?(.*)$/i);
  if (match) {
    const [, file, lineNumber, columnNumber, severity, code, message] = match;
    return {
      file: file.trim(),
      line: Number(lineNumber),
      column: Number(columnNumber),
      severity: String(severity || 'error').toLowerCase(),
      code: String(code || ''),
      message: String(message || '').trim(),
      raw: text,
    };
  }

  // GCC/Clang/javac-style: source.cpp:7:15: error: expected ';'
  // or Main.java:7: error: cannot find symbol
  match = text.match(/^(.+?):(\d+)(?::(\d+))?:\s*(?:(fatal error|fatal|error|warning|note)\s*:?\s*)?(?:([A-Za-z]+\d+)\s*:\s*)?(.*)$/i);
  if (match) {
    const [, file, lineNumber, columnNumber, severity, code, message] = match;
    return {
      file: file.trim(),
      line: Number(lineNumber),
      column: columnNumber ? Number(columnNumber) : null,
      severity: String(severity || 'error').toLowerCase(),
      code: String(code || ''),
      message: String(message || '').trim(),
      raw: text,
    };
  }

  return null;
}

export function getCompilerDiagnostics(solution) {
  if (getSolutionFailureCategory(solution) !== 'Компиляция') return [];
  const text = getRunnerError(solution);
  if (!text) return [];

  const seen = new Set();
  const diagnostics = [];
  for (const rawLine of String(text).split(/\r?\n/)) {
    const parsed = parseCompilerDiagnosticLine(rawLine);
    if (!parsed || !parsed.message) continue;
    const key = `${parsed.line}:${parsed.column ?? ''}:${parsed.code}:${parsed.message}`;
    if (seen.has(key)) continue;
    seen.add(key);
    diagnostics.push({
      ...parsed,
      key,
      preview: compilerSourceLine(solution, parsed.line),
    });
    if (diagnostics.length >= 20) break;
  }
  return diagnostics;
}

export function getSolutionFailureCategory(solution) {
  const key = getSolutionStatusKey(solution);
  const result = getSolutionResult(solution);
  const raw = isObject(result?.raw) ? result.raw : parseMaybeJson(result?.raw);
  const policyKind = String(raw?.policyKind || raw?.PolicyKind || '').toLowerCase();

  if (key === 'compileerror' || key === 'compilationerror') return 'Компиляция';
  if (key === 'policyfailed') {
    if (policyKind === 'task') return 'Учебное ограничение';
    if (policyKind === 'platform') return 'Безопасность';
    if (policyKind === 'mixed') return 'Учебное ограничение + безопасность';
    return 'Анализатор';
  }
  if (key === 'judgeunavailable') return 'Инфраструктура проверки';
  if (key === 'runtimeerror') return 'Выполнение';
  if (key === 'timeexceeded') return 'Лимит времени';
  if (key === 'memoryexceeded') return 'Лимит памяти';
  if (key === 'notestsconfigured') return 'Настройка задания';
  if (key === 'languagenotallowed') return 'Настройка языка';
  if (key === 'wronganswer' || key === 'rejected' || key === 'failed') return 'Тесты';
  return null;
}

function diagnosticNumber(value) {
  const number = Number(value);
  return Number.isFinite(number) && number >= 0 ? number : null;
}

export function getPolicyDiagnostics(solution) {
  const result = getSolutionResult(solution);
  const rawValue = parseMaybeJson(result?.raw ?? result?.Raw);
  const raw = isObject(rawValue) ? rawValue : null;
  const errors = asArray(raw?.errors ?? raw?.Errors);
  const hits = asArray(raw?.hits ?? raw?.Hits);

  return errors.map((error, index) => {
    const patternId = String(error?.pattern_id ?? error?.patternId ?? error?.PatternId ?? '');
    const hit = hits.find((candidate) => String(candidate?.pattern_id ?? candidate?.patternId ?? candidate?.PatternId ?? '') === patternId) || null;
    return {
      key: `${patternId || 'diagnostic'}:${index}`,
      code: String(error?.code ?? error?.Code ?? ''),
      patternId,
      message: String(error?.message ?? error?.Message ?? 'Код не соответствует правилам.'),
      needle: String(error?.needle ?? error?.Needle ?? hit?.needle ?? hit?.Needle ?? ''),
      position: diagnosticNumber(error?.position ?? error?.Position ?? hit?.position ?? hit?.Position),
      line: diagnosticNumber(error?.line ?? error?.Line ?? hit?.line ?? hit?.Line),
      column: diagnosticNumber(error?.column ?? error?.Column ?? hit?.column ?? hit?.Column),
      preview: String(error?.preview ?? error?.Preview ?? hit?.preview ?? hit?.Preview ?? ''),
    };
  });
}

export function getSolutionSubmittedAt(solution) {
  return getSolutionDate(solution);
}

export function getSolutionTitle(solution) {
  return getCourseAssignmentLabel(solution);
}

export function getSolutionPassedFailed(solution) {
  const cases = getSolutionCases(solution);
  const total = firstNumber(solution?.totalCount, solution?.TotalCount, lookup(solution, ['totalCount', 'totalTests']));
  if (cases.length === 0 && (total === null || total === 0)) {
    return { passed: null, failed: null, total: 0, testsRan: false };
  }
  const passed = getPassedCount(solution);
  const failed = getFailedCount(solution);
  return { passed, failed, total: total ?? cases.length, testsRan: true };
}

export function getRunnerText(solution, key) {
  if (key === 'runnerError' || key === 'error') return getRunnerError(solution);
  return getTextOutput(solution, key);
}

export function getImageSolutionPercent(solution) {
  return getImageSimilarity(solution);
}

export function getImageSolutionThreshold(solution) {
  return getImageThreshold(solution);
}

export function getImageSolutionTitle(solution) {
  return getAssignmentLabel(solution, 'Image-решение');
}

export function getImageSolutionUrl(solution, kind) {
  return getImageUrl(solution, kind);
}
