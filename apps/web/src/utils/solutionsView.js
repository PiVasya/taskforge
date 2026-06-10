export function firstDefined(...values) {
  return values.find((value) => value !== undefined && value !== null);
}

export function normalizeResult(value) {
  if (!value) return null;
  if (typeof value === 'string') {
    try {
      return JSON.parse(value);
    } catch {
      return { raw: value };
    }
  }
  return value;
}

export function getResultValue(source, ...keys) {
  const result = normalizeResult(source?.result ?? source?.resultJson ?? source?.ResultJson);
  for (const key of keys) {
    if (source && source[key] !== undefined && source[key] !== null) return source[key];
    if (result && result[key] !== undefined && result[key] !== null) return result[key];
  }
  return undefined;
}

export function normalizePercent(value) {
  if (value === undefined || value === null || value === '') return null;
  const n = Number(value);
  if (!Number.isFinite(n)) return null;
  const percent = n >= 0 && n <= 1 ? n * 100 : n;
  return Math.round(percent * 10) / 10;
}

export function getSolutionCode(solution) {
  return String(firstDefined(
    solution?.submittedCode,
    solution?.code,
    solution?.Code,
    getResultValue(solution, 'submittedCode'),
    getResultValue(solution, 'code')
  ) ?? '');
}

export function getSolutionSubmittedAt(solution) {
  return firstDefined(
    solution?.submittedAt,
    solution?.createdAtUtc,
    solution?.createdAt,
    solution?.CreatedAt,
    solution?.timestamp,
    solution?.date
  );
}

export function formatDateTime(value) {
  if (!value) return '—';
  const d = new Date(value);
  return Number.isNaN(d.getTime()) ? '—' : d.toLocaleString();
}

export function getSolutionStatus(solution) {
  return String(firstDefined(
    solution?.status,
    solution?.verdict,
    solution?.Status,
    solution?.Verdict,
    getResultValue(solution, 'status'),
    getResultValue(solution, 'verdict')
  ) ?? '').trim();
}

export function normalizeStatus(status) {
  return String(status || '').trim().toLowerCase();
}

export function isPendingSolution(solution) {
  const status = normalizeStatus(getSolutionStatus(solution));
  return solution?.isPending === true || solution?.result?.pending === true || ['preparing', 'queued', 'running', 'pending'].includes(status);
}

export function isAcceptedSolution(solution) {
  const status = normalizeStatus(getSolutionStatus(solution));
  return solution?.passedAllTests === true || solution?.passedAll === true || status === 'accepted';
}

export function getSolutionCases(solution) {
  const result = normalizeResult(solution?.result ?? solution?.resultJson ?? solution?.ResultJson);
  const candidates = [
    solution?.cases,
    solution?.testCases,
    solution?.results,
    result?.cases,
    result?.testCases,
    result?.results,
  ];
  return candidates.find(Array.isArray) || [];
}

export function getSolutionCounts(solution) {
  const explicitPassed = firstDefined(solution?.passedCount, solution?.PassedCount);
  const explicitFailed = firstDefined(solution?.failedCount, solution?.FailedCount);
  if (Number.isFinite(Number(explicitPassed)) || Number.isFinite(Number(explicitFailed))) {
    return {
      passed: Number(explicitPassed ?? 0),
      failed: Number(explicitFailed ?? 0),
    };
  }

  const cases = getSolutionCases(solution);
  if (cases.length === 0) return { passed: null, failed: null };

  let passed = 0;
  let failed = 0;
  for (const c of cases) {
    const status = normalizeStatus(c?.status);
    const ok = c?.passed === true || status === 'ok' || status === 'accepted';
    if (ok) passed += 1;
    else failed += 1;
  }
  return { passed, failed };
}

export function getSolutionScore(solution) {
  const value = firstDefined(solution?.score, solution?.Score, getResultValue(solution, 'score'));
  if (value === undefined || value === null || value === '') return null;
  const n = Number(value);
  return Number.isFinite(n) ? n : null;
}

export function getSolutionTitle(solution, fallbackPrefix = 'Задание') {
  const courseTitle = String(firstDefined(solution?.courseTitle, solution?.courseName, '') || '').trim();
  const assignmentTitle = String(firstDefined(solution?.assignmentTitle, solution?.title, solution?.assignmentName, '') || '').trim();
  if (courseTitle && assignmentTitle) return `${courseTitle} • ${assignmentTitle}`;
  if (assignmentTitle) return assignmentTitle;
  const id = firstDefined(solution?.assignmentId, solution?.taskAssignmentId, solution?.AssignmentId);
  return id ? `${fallbackPrefix} ${String(id).slice(0, 8)}` : fallbackPrefix;
}

export function getSolutionBadge(solution) {
  const status = normalizeStatus(getSolutionStatus(solution));
  if (isPendingSolution(solution)) return { intent: 'secondary', label: 'Проверяется' };
  if (isAcceptedSolution(solution)) return { intent: 'success', label: 'Принято' };
  if (status === 'compileerror') return { intent: 'danger', label: 'Ошибка компиляции' };
  if (status === 'policyfailed') return { intent: 'danger', label: 'Отклонено анализатором' };
  if (status === 'notestsconfigured') return { intent: 'danger', label: 'Нет тестов' };
  if (status === 'judgeunavailable') return { intent: 'danger', label: 'Проверка недоступна' };
  if (status === 'languagenotallowed') return { intent: 'danger', label: 'Язык запрещён' };
  if (status === 'rejected' || status === 'failed') return { intent: 'danger', label: 'Не принято' };
  return { intent: 'secondary', label: getSolutionStatus(solution) || 'Статус неизвестен' };
}

export function getSolutionStreams(solution) {
  const result = normalizeResult(solution?.result ?? solution?.resultJson ?? solution?.ResultJson);
  return {
    stdout: String(firstDefined(solution?.stdout, result?.stdout, result?.output, '') ?? ''),
    stderr: String(firstDefined(solution?.stderr, result?.stderr, result?.compileStderr, '') ?? ''),
    runnerError: String(firstDefined(solution?.runnerError, result?.runnerError, result?.error, '') ?? ''),
    message: String(firstDefined(solution?.message, result?.message, '') ?? ''),
  };
}

export function getImageSimilarityPercent(solution) {
  return normalizePercent(firstDefined(
    solution?.similarityPercent,
    solution?.similarity,
    getResultValue(solution, 'similarityPercent'),
    getResultValue(solution, 'similarity'),
    getResultValue(solution, 'combined_similarity')
  ));
}

export function getImageThresholdPercent(solution) {
  return normalizePercent(firstDefined(
    solution?.thresholdPercent,
    solution?.threshold,
    getResultValue(solution, 'thresholdPercent'),
    getResultValue(solution, 'threshold')
  ));
}

export function dataUrlFromBase64(base64) {
  if (!base64 || typeof base64 !== 'string') return null;
  if (base64.startsWith('data:')) return base64;
  return `data:image/png;base64,${base64}`;
}

export function getImageReferenceUrl(solution) {
  return firstDefined(
    solution?.referenceUrl,
    solution?.expectedUrl,
    getResultValue(solution, 'referenceUrl'),
    getResultValue(solution, 'expectedUrl'),
    dataUrlFromBase64(getResultValue(solution, 'referenceBase64'))
  ) || null;
}

export function getImageSubmittedUrl(solution) {
  return firstDefined(
    solution?.submittedUrl,
    solution?.actualUrl,
    solution?.submissionUrl,
    getResultValue(solution, 'submittedUrl'),
    getResultValue(solution, 'actualUrl'),
    getResultValue(solution, 'renderedUrl'),
    dataUrlFromBase64(getResultValue(solution, 'pngBase64'))
  ) || null;
}

export function getImageTitle(solution) {
  const title = String(firstDefined(solution?.assignmentTitle, solution?.title, getResultValue(solution, 'assignmentTitle'), '') || '').trim();
  if (title) return title;
  const id = firstDefined(solution?.assignmentId, solution?.taskAssignmentId, solution?.AssignmentId);
  return id ? `Задание ${String(id).slice(0, 8)}` : 'Image-решение';
}
