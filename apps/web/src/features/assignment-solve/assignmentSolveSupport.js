import React from 'react';
import { getMySolutionDetails } from '../../api/solutions';

export const ALL_LANGS = [
  { value: 'cpp',        label: 'C++' },
  { value: 'python',     label: 'Python' },
  { value: 'csharp',     label: 'C#' },
  { value: 'javascript', label: 'JavaScript' },
  { value: 'pascal',     label: 'Pascal' },
  { value: 'java',       label: 'Java' },
];


export function normalizeLang(x) {
  if (!x) return '';
  const s = String(x).trim().toLowerCase();

  if (s === 'c++' || s === 'cpp' || s === 'g++' || s === 'gcc' || s === 'cxx' || s === 'си++' || s === 'с++') return 'cpp';
  if (s === 'c#' || s === 'cs' || s === 'csharp' || s === 'sharp' || s === 'си#' || s === 'с#' || s === 'шарп') return 'csharp';
  if (s === 'py' || s === 'python' || s === 'python3' || s === 'питон') return 'python';
  if (s === 'js' || s === 'node' || s === 'nodejs' || s === 'node.js' || s === 'javascript' || s === 'java-script') return 'javascript';

  
  if (s === 'pas' || s === 'pascal' || s === 'pascalabc' || s === 'pascalabcnet') return 'pascal';

  
  if (s === 'java' || s === 'джава') return 'java';

  return s;
}





export function parseAllowedLanguages(raw) {
  let arr = [];

  if (Array.isArray(raw)) arr = raw;
  else if (typeof raw === 'string') arr = raw.split(',').map(x => x.trim()).filter(Boolean);
  else arr = [];

  const allowed = arr
    .map(normalizeLang)
    .filter(Boolean);

  
  const allowedSet = new Set(allowed);
  const knownSet = new Set(ALL_LANGS.map(x => x.value));
  const filtered = Array.from(allowedSet).filter(x => knownSet.has(x));

  return filtered;
}

const PENDING_SOLUTION_STATUSES = new Set(['preparing', 'queued', 'running', 'pending']);
export const ASSIGNMENT_MIN_REVEAL_MS = 500;

export function wait(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

export function useStableEvent(handler) {
  const handlerRef = React.useRef(handler);
  React.useLayoutEffect(() => {
    handlerRef.current = handler;
  }, [handler]);
  return React.useCallback((...args) => handlerRef.current?.(...args), []);
}

export function isPendingSolution(value) {
  const status = String(value?.status || value?.verdict || '').trim().toLowerCase();
  return value?.isPending === true || value?.result?.pending === true || PENDING_SOLUTION_STATUSES.has(status);
}

export async function waitForSolutionVerdict(solutionId, options = {}) {
  const { maxAttempts = 30, onUpdate } = options || {};
  let latest = null;
  for (let i = 0; i < maxAttempts; i += 1) {
    await new Promise(resolve => setTimeout(resolve, i < 4 ? 700 : 1200));
    latest = await getMySolutionDetails(solutionId);
    onUpdate?.(latest, i + 1);
    if (!isPendingSolution(latest)) return { solution: latest, timedOut: false };
  }
  return { solution: latest, timedOut: true };
}

export function sameAssignmentId(left, right) {
  return String(left || '').trim().toLowerCase() === String(right || '').trim().toLowerCase();
}

export function createActivitySessionId(assignmentId) {
  const randomPart = Math.random().toString(36).slice(2, 10);
  const timePart = Date.now().toString(36);
  return `solve:${assignmentId}:${timePart}:${randomPart}`;
}

export function clampActivityText(value, max = 1200) {
  const text = typeof value === 'string' ? value : '';
  if (!text) return '';
  return text.length <= max ? text : text.slice(0, max);
}

export function hashActivityText(value) {
  const text = typeof value === 'string' ? value : '';
  let h = 2166136261;
  for (let i = 0; i < text.length; i += 1) {
    h ^= text.charCodeAt(i);
    h = Math.imul(h, 16777619);
  }
  return `fnv1a:${(h >>> 0).toString(16).padStart(8, '0')}:${text.length}`;
}

export function clipboardTextFromEvent(event) {
  try {
    const text = event?.clipboardData?.getData?.('text/plain');
    if (typeof text === 'string' && text.length > 0) return text;
    const selection = typeof window !== 'undefined' ? window.getSelection?.()?.toString?.() : '';
    return typeof selection === 'string' ? selection : '';
  } catch {
    return '';
  }
}

export function getSolutionCases(resObj) {
  const cases =
    resObj?.cases ??
    resObj?.testCases ??
    resObj?.results ??
    resObj?.result?.cases ??
    resObj?.result?.results ??
    [];
  return Array.isArray(cases) ? cases : [];
}

export function isCasePassed(c) {
  if (!c || typeof c !== 'object') return false;
  if (typeof c.passed === 'boolean') return c.passed;
  if (typeof c.Passed === 'boolean') return c.Passed;
  const status = String(c?.status ?? c?.Status ?? '').trim().toLowerCase();
  return status === 'accepted' || status === 'passed' || status === 'success';
}

export function getSolutionStatusKey(resObj) {
  return String(resObj?.status || resObj?.verdict || '').trim().toLowerCase();
}

export function getSolutionOutput(resObj) {
  return {
    stdout: resObj?.stdout || resObj?.result?.stdout || '',
    stderr:
      resObj?.stderr ||
      resObj?.compileError ||
      resObj?.result?.stderr ||
      resObj?.result?.compileStderr ||
      '',
    message: resObj?.message || resObj?.result?.message || '',
  };
}

export function getResultSummary(resObj) {
  const cases = getSolutionCases(resObj);
  const status = getSolutionStatusKey(resObj);
  const pending = isPendingSolution(resObj);
  const explicitFailed = resObj?.passedAll === false || resObj?.passedAllTests === false || resObj?.PassedAll === false || resObj?.PassedAllTests === false;
  const passedAll = !explicitFailed && (
    resObj?.__allPassed === true ||
    resObj?.passedAll === true ||
    resObj?.passedAllTests === true ||
    status === 'accepted' ||
    (cases.length > 0 && cases.every(isCasePassed))
  );

  if (pending) {
    return {
      tone: 'info',
      title: status === 'running' ? 'Решение проверяется' : 'Решение в очереди',
      description: 'Код не потерян. Можно дождаться результата на этой странице или открыть подробности отдельно.',
    };
  }
  if (passedAll) {
    return {
      tone: 'success',
      title: 'Все тесты пройдены',
      description: 'Решение принято. Результат сохранён в истории.',
    };
  }
  if (status === 'compileerror') {
    return {
      tone: 'danger',
      title: 'Ошибка компиляции',
      description: 'Исправьте сообщения компилятора и отправьте решение снова.',
    };
  }
  if (status === 'policyfailed') {
    return {
      tone: 'danger',
      title: 'Решение отклонено анализатором кода',
      description: 'Ниже показана безопасная причина отклонения.',
    };
  }
  if (status === 'languagenotallowed') {
    return {
      tone: 'danger',
      title: 'Язык не разрешён для задания',
      description: 'Выберите язык из списка разрешённых для этого курса.',
    };
  }
  if (status === 'notestsconfigured') {
    return {
      tone: 'danger',
      title: 'Для задания не настроены тесты',
      description: 'Проверка не может быть выполнена без тестов.',
    };
  }
  if (status === 'judgeunavailable') {
    return {
      tone: 'danger',
      title: 'Система проверки временно недоступна',
      description: 'Попробуйте отправить решение позже.',
    };
  }
  return {
    tone: 'danger',
    title: 'Не все тесты пройдены',
    description: 'Сравните фактический вывод с ожидаемым и исправьте решение.',
  };
}

function extractPolicyRawFromResult(resObj) {
  const candidates = [
    resObj?.policyDetails,
    resObj?.policyError,
    resObj?.result?.policyDetails,
    resObj?.result?.policyError,
    resObj?.result?.raw,
    resObj?.raw,
  ];

  for (const candidate of candidates) {
    if (!candidate) continue;
    if (typeof candidate === 'string') return candidate;
    if (typeof candidate === 'object') return candidate;
  }

  const cases = getSolutionCases(resObj);
  const policyCase = cases.find(
    (c) =>
      String(c?.status || '').trim().toLowerCase() === 'policy_failed' ||
      String(c?.compileStderr || c?.stderr || c?.error || '').includes('[policy_failed]'),
  );
  return policyCase ? (policyCase.policyDetails || policyCase.raw || String(policyCase.compileStderr || policyCase.stderr || policyCase.error || '')) : '';
}

function isPlatformPolicyPattern(patternId) {
  const id = String(patternId || '').trim().toLowerCase();
  if (!id || id === '-') return false;
  return (
    id === 'platform.security' ||
    id.startsWith('py.') ||
    id.startsWith('js.') ||
    id.startsWith('c.') ||
    id.startsWith('cpp.') ||
    id.startsWith('cs.') ||
    id.startsWith('java.') ||
    id.startsWith('pas.')
  );
}

function cleanupPolicyMessage(message) {
  return String(message || '')
    .replace(/\(pattern_id=[^)]+\)/gi, '')
    .replace(/\s{2,}/g, ' ')
    .trim();
}

function policyValue(obj, ...names) {
  if (!obj || typeof obj !== 'object') return '';
  for (const name of names) {
    if (obj[name] != null) return obj[name];
  }
  return '';
}

function buildPolicyUiFromParts(violations, hits, raw = '') {
  const taskViolations = violations.filter((v) => !isPlatformPolicyPattern(v.patternId));
  const platformViolations = violations.filter((v) => isPlatformPolicyPattern(v.patternId));
  const taskHits = hits.filter((h) => !isPlatformPolicyPattern(h.patternId));

  if (taskViolations.length === 0 && platformViolations.length > 0) {
    return {
      kind: 'platform',
      title: 'Решение отклонено системой безопасности',
      bullets: ['Код использует системные возможности, которые нельзя запускать в песочнице.'],
      hitLines: [],
      raw,
    };
  }

  const forbidden = [];
  const required = [];
  const cyrillic = [];
  const other = [];

  for (const v of taskViolations) {
    const code = String(v.code || '').toLowerCase();
    const patternId = String(v.patternId || '').toLowerCase();
    const msg = cleanupPolicyMessage(v.message || '');
    if (!msg) continue;
    if (code.includes('cyrillic') || patternId === 'unicode.cyrillic_in_code' || /кириллиц/i.test(msg)) {
      cyrillic.push(msg);
    } else if (code.includes('missing_required') || /обязатель|не найдено обязательное/i.test(msg)) {
      required.push(msg.replace(/^Не найдено обязательное:\s*/i, ''));
    } else if (code.includes('forbidden') || /запрещ/i.test(msg)) {
      forbidden.push(msg.replace(/^Запрещено:\s*/i, '').replace(/^Запрещённая конструкция:\s*/i, ''));
    } else {
      other.push(msg);
    }
  }

  const unique = (items) => [...new Set(items.filter(Boolean))];
  const bullets = [];
  if (cyrillic.length) bullets.push(...unique(cyrillic));
  if (forbidden.length) bullets.push(`Запрещено по условию задания: ${unique(forbidden).join(' • ')}`);
  if (required.length) bullets.push(`Нужно обязательно использовать: ${unique(required).join(' • ')}`);
  if (other.length) bullets.push(...unique(other));
  if (platformViolations.length) {
    bullets.push('Дополнительно решение отклонено системой безопасности. Подробности системного ограничения скрыты.');
  }

  const hitLines = taskHits
    .filter((h) => h.needle || h.pos || h.preview)
    .slice(0, 4)
    .map((h) => {
      const parts = [];
      if (h.needle) parts.push(`«${h.needle}»`);
      if (h.pos !== '' && h.pos != null) parts.push(`позиция ${h.pos}`);
      if (h.preview) parts.push(`фрагмент: ${h.preview}`);
      return `Найдено ${parts.join(', ')}`;
    });

  return {
    kind: taskViolations.length ? (platformViolations.length ? 'mixed' : 'task') : 'mixed',
    title: cyrillic.length && taskViolations.length === cyrillic.length && !platformViolations.length
      ? 'Решение отклонено: в коде найдена кириллица'
      : 'Решение отклонено по правилам задания',
    bullets: bullets.length ? bullets : ['Анализатор кода нашёл нарушение правил задания.'],
    hitLines,
    raw,
  };
}

function parsePolicyObject(payload) {
  if (!payload || typeof payload !== 'object') return null;

  const errors = Array.isArray(payload.errors) ? payload.errors : [];
  const hits = Array.isArray(payload.hits) ? payload.hits : [];
  if (!errors.length && !hits.length && payload.policyKind !== 'platform') return null;

  const violations = errors.map((e) => ({
    code: policyValue(e, 'code'),
    message: cleanupPolicyMessage(policyValue(e, 'message')),
    patternId: policyValue(e, 'pattern_id', 'patternId'),
  })).filter((v) => v.code || v.message || v.patternId);

  const parsedHits = hits.map((h) => ({
    patternId: policyValue(h, 'pattern_id', 'patternId'),
    needle: policyValue(h, 'needle'),
    pos: policyValue(h, 'position', 'pos'),
    preview: policyValue(h, 'preview'),
  })).filter((h) => h.patternId || h.needle || h.preview);

  if (violations.length === 0 && payload.policyKind === 'platform') {
    violations.push({ code: 'sandbox_security', message: 'Код использует системные возможности, которые нельзя запускать в песочнице.', patternId: 'platform.security' });
  }

  return buildPolicyUiFromParts(violations, parsedHits, payload);
}

export function parsePolicyText(raw) {
  if (raw && typeof raw === 'object') return parsePolicyObject(raw);

  const txt = String(raw || '');
  if (!txt || (!txt.includes('[policy_failed]') && !txt.toLowerCase().includes('code analyzer blocked'))) {
    return null;
  }

  const lines = txt.split('\n').map((x) => x.trim()).filter(Boolean);
  const violations = [];
  const hits = [];
  let inHits = false;

  for (const line of lines) {
    if (line.startsWith('[hits]')) {
      inHits = true;
      continue;
    }
    if (line.startsWith('[') && line.endsWith(']')) {
      inHits = false;
      continue;
    }
    if (!line.startsWith('- ')) continue;

    const body = line.replace(/^-\s*/, '');
    if (inHits) {
      const mId = body.match(/\bid=([^\s]+)\b/i);
      const mNeedle = body.match(/needle='([^']*)'/i);
      const mPos = body.match(/pos=(\d+)/i);
      const mPrev = body.match(/preview='([^']*)'/i);
      hits.push({
        patternId: mId?.[1] || '',
        needle: mNeedle?.[1] || '',
        pos: mPos?.[1] || '',
        preview: mPrev?.[1] || '',
      });
      continue;
    }

    const match = body.match(/^([^:]+):\s*(.*?)(?:\s*\(pattern_id=([^)]*)\))?$/i);
    if (match) {
      violations.push({
        code: match[1] || '',
        message: cleanupPolicyMessage(match[2] || body),
        patternId: match[3] || '',
      });
    } else {
      violations.push({ code: '', message: cleanupPolicyMessage(body), patternId: '' });
    }
  }

  return buildPolicyUiFromParts(violations, hits, txt);
}

export function getSolutionPolicyUi(resObj) {
  return parsePolicyText(extractPolicyRawFromResult(resObj));
}

