import { extractApiErrorMessages } from '../../utils/handleApiError';
import { sanitizeRunnerText } from '../../utils/runnerText';
import {
  dataUrlFromBase64,
  getImageReferenceUrl,
  getImageSimilarityPercent,
  getImageSubmittedUrl,
  getImageThresholdPercent,
} from '../../utils/solutionsView';

function displayRunnerText(value) {
  return sanitizeRunnerText(value);
}

function isHiddenTestCase(t) {
  return t?.isHidden === true || t?.hidden === true || t?.Hidden === true;
}

function buildImageTaskErrorText(err, fallbackMessage) {
  const parsed = extractApiErrorMessages(err, fallbackMessage);
  const lines = [parsed.primaryMessage];

  if (parsed.userHint && parsed.userHint !== parsed.primaryMessage) {
    lines.push(parsed.userHint);
  }

  for (const step of parsed.howToFix || []) {
    lines.push(`• ${step}`);
  }

  if (parsed.code) {
    lines.push(`Код ошибки: ${parsed.code}`);
  }

  return Array.from(new Set(lines.filter(Boolean))).join('\n');
}

function imageReferenceUrlFromAssignment(assignment) {
  const tests = assignment?.tests || assignment?.testCases || {};
  const arr = Array.isArray(tests) ? tests : Array.isArray(tests?.testCases) ? tests.testCases : Array.isArray(tests?.tests) ? tests.tests : Array.isArray(tests?.cases) ? tests.cases : [];
  const firstImageCase = arr.find((t) => t && !t.isHidden && !t.hidden && (t.expectedImageUrl || t.referenceUrl || t.expectedImageKey || t.referenceKey || t.imageKey || t.expectedImageBase64 || t.referenceBase64 || t.imageBase64));
  if (firstImageCase) {
    const key = firstImageCase.expectedImageKey || firstImageCase.referenceKey || firstImageCase.imageKey || firstImageCase.imageTestReferenceKey;
    if (firstImageCase.expectedImageUrl || firstImageCase.referenceUrl) return firstImageCase.expectedImageUrl || firstImageCase.referenceUrl;
    if (key) return `/api/private-files/${encodeURIComponent(key)}`;
    const base64 = firstImageCase.expectedImageBase64 || firstImageCase.referenceBase64 || firstImageCase.imageBase64;
    const contentType = firstImageCase.expectedImageContentType || firstImageCase.referenceContentType || 'image/png';
    return String(base64).startsWith('data:') ? base64 : `data:${contentType};base64,${base64}`;
  }
  const base64 = tests?.referenceBase64 || assignment?.referenceBase64;
  if (base64) {
    const contentType = tests?.referenceContentType || assignment?.referenceContentType || 'image/png';
    return String(base64).startsWith('data:') ? base64 : `data:${contentType};base64,${base64}`;
  }
  const key = assignment?.imageTestReferenceKey || tests?.imageTestReferenceKey;
  return key ? `/api/private-files/${encodeURIComponent(key)}` : null;
}

function imageTestCasesFromAssignment(assignment) {
  const tests = assignment?.tests || assignment?.testCases || {};
  const arr = Array.isArray(tests) ? tests : Array.isArray(tests?.testCases) ? tests.testCases : Array.isArray(tests?.tests) ? tests.tests : Array.isArray(tests?.cases) ? tests.cases : [];
  return Array.isArray(arr) ? arr : [];
}

function buildImageTaskResponseText(resp, fallbackMessage) {
  if (!resp || typeof resp !== 'object') return fallbackMessage;

  const primaryMessage =
    resp.message ||
    resp.error ||
    resp.runnerError ||
    fallbackMessage ||
    'Не удалось обработать ответ сервера';

  const lines = [primaryMessage];

  if (resp.detail && resp.detail !== primaryMessage) {
    lines.push(resp.detail);
  }

  if (resp.userHint && resp.userHint !== primaryMessage) {
    lines.push(resp.userHint);
  }

  for (const step of Array.isArray(resp.howToFix) ? resp.howToFix : []) {
    lines.push(`• ${step}`);
  }

  if (resp.code) {
    lines.push(`Код ошибки: ${resp.code}`);
  }

  return Array.from(new Set(lines.filter(Boolean))).join('\n');
}


function hasImageResultPayload(resp) {
  return Boolean(
    resp && typeof resp === 'object' && (
      resp.ok === true ||
      resp.passed === true ||
      resp.passed === false ||
      resp.pngBase64 ||
      resp.renderedUrl ||
      resp.submittedUrl ||
      resp.actualUrl ||
      Array.isArray(resp.cases)
    )
  );
}

function normalizeImageTaskResult(resp, expectedUrl, { isTrial = false, code = '', language = '', assignmentTitle = '' } = {}) {
  const actualUrl = getImageSubmittedUrl(resp) || dataUrlFromBase64(resp?.pngBase64) || resp?.renderedUrl || null;
  const referenceUrl = getImageReferenceUrl(resp) || expectedUrl || null;
  const similarityPercent = getImageSimilarityPercent(resp);
  const thresholdPercent = getImageThresholdPercent(resp);

  return {
    ...resp,
    id: resp?.id ?? resp?.solutionId ?? null,
    solutionId: resp?.solutionId ?? resp?.id ?? null,
    assignmentTitle,
    passed: typeof resp?.passed === 'boolean' ? resp.passed : null,
    similarityPercent,
    thresholdPercent,
    expectedUrl: referenceUrl,
    referenceUrl,
    actualUrl,
    submittedUrl: actualUrl,
    isTrial,
    code,
    language,
    createdAtUtc: resp?.createdAtUtc || resp?.submittedAt || new Date().toISOString(),
  };
}


export {
  displayRunnerText,
  isHiddenTestCase,
  buildImageTaskErrorText,
  imageReferenceUrlFromAssignment,
  imageTestCasesFromAssignment,
  buildImageTaskResponseText,
  hasImageResultPayload,
  normalizeImageTaskResult,
};
