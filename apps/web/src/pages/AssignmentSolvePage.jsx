
import React, { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';

import Layout from '../components/Layout';
import QuotaPill from '../components/QuotaPill';
import { Card, Button, Select, Textarea, Badge } from '../components/ui';
import IfEditor from '../components/IfEditor';
import CodeEditor from '../components/CodeEditor';
import TaskTestSolve from './TaskTestSolve';
import MathTaskSolve from './MathTaskSolve';
import StatementViewer from '../components/tiptap/StatementViewer';

import { useNotify } from '../components/notify/NotifyProvider';
import { getAssignment, getAssignmentsByCourse } from '../api/assignments';
import { submitSolution, getMySolutionDetails } from '../api/solutions';
import { runImageTestCode, submitImageTestCode } from '../api/imageTests';
import { getAdminAssignmentInsights } from '../api/adminAssignmentInsights';
import { extractApiErrorMessages } from '../utils/handleApiError';
import { getApiErrorMessage } from '../api/http';
import { sanitizeRunnerText } from '../utils/runnerText';
import {
  dataUrlFromBase64,
  getImageReferenceUrl,
  getImageSimilarityPercent,
  getImageSubmittedUrl,
  getImageThresholdPercent,
} from '../utils/solutionsView';

import { ArrowLeft, Play, CheckCircle2, XCircle, BarChart3 } from 'lucide-react';
import { useRoleFlags } from '../contexts/EditorModeContext';


const ALL_LANGS = [
  { value: 'cpp',        label: 'C++' },
  { value: 'python',     label: 'Python' },
  { value: 'csharp',     label: 'C#' },
  { value: 'javascript', label: 'JavaScript' },
  { value: 'pascal',     label: 'Pascal' },
  { value: 'java',       label: 'Java' },
];


function normalizeLang(x) {
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





function parseAllowedLanguages(raw) {
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

function isPendingSolution(value) {
  const status = String(value?.status || value?.verdict || '').trim().toLowerCase();
  return value?.isPending === true || value?.result?.pending === true || PENDING_SOLUTION_STATUSES.has(status);
}

async function waitForSolutionVerdict(solutionId, options = {}) {
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

function readSolveDraft(assignmentId) {
  try {
    const raw = localStorage.getItem(`solve-draft:${assignmentId}`);
    if (!raw) return null;
    const parsed = JSON.parse(raw);
    if (!parsed || typeof parsed !== 'object') return null;
    return parsed;
  } catch {
    return null;
  }
}

function saveSolveDraft(assignmentId, draft) {
  if (!assignmentId) return;
  try {
    localStorage.setItem(`solve-draft:${assignmentId}`, JSON.stringify(draft));
  } catch {}
}

function getSolutionCases(resObj) {
  const cases =
    resObj?.cases ??
    resObj?.testCases ??
    resObj?.results ??
    resObj?.result?.cases ??
    resObj?.result?.results ??
    [];
  return Array.isArray(cases) ? cases : [];
}

function isCasePassed(c) {
  if (!c || typeof c !== 'object') return false;
  if (typeof c.passed === 'boolean') return c.passed;
  if (typeof c.Passed === 'boolean') return c.Passed;
  const status = String(c?.status ?? c?.Status ?? '').trim().toLowerCase();
  // status=ok from runners means only that the program exited normally.
  // Do not let it override passed:false on wrong answers.
  return status === 'accepted' || status === 'passed' || status === 'success';
}

function getSolutionStatusKey(resObj) {
  return String(resObj?.status || resObj?.verdict || '').trim().toLowerCase();
}

function getSolutionOutput(resObj) {
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

function getResultSummary(resObj) {
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

function parsePolicyText(raw) {
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

    const body = line.replace(/^\-\s*/, '');
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

function getSolutionPolicyUi(resObj) {
  return parsePolicyText(extractPolicyRawFromResult(resObj));
}

function displayText(value) {
  if (value == null) return '';
  return String(value);
}

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

export default function AssignmentSolvePage() {
  const { assignmentId } = useParams();
  const nav = useNavigate();

  const notify = useNotify();
  const { isAdmin } = useRoleFlags();
  const [adminInsights, setAdminInsights] = useState(null);

  const [a, setA] = useState(null);
  const [loading, setLoading] = useState(true);

  
  const [nextA, setNextA] = useState(null); 

  const [language, setLanguage] = useState('cpp');
  const [code, setCode] = useState('');
  const [plainMode, setPlainMode] = useState(false);

  
  const [codeSolveLayout, setCodeSolveLayout] = useState(
    () => localStorage.getItem('codeSolveLayout') || 'split'
  );

  const [submitting, setSubmitting] = useState(false);
  const [submitPhase, setSubmitPhase] = useState('idle');
  const [error, setError] = useState('');
  const [result, setResult] = useState(null);
  const [checkedDraftKey, setCheckedDraftKey] = useState('');

  
  const [imgBusy, setImgBusy] = useState(false);
  const [imgError, setImgError] = useState('');
  const [imgCompare, setImgCompare] = useState(null); 
  const [imageInput, setImageInput] = useState('');

  
  const allowedLangs = useMemo(() => {
    
    
    const raw =
      a?.allowedLanguages ??
      a?.allowedLanguagesCsv ??
      a?.courseAllowedLanguages ??
      a?.course?.allowedLanguages ??
      null;

    const parsed = parseAllowedLanguages(raw);

    
    if (String(a?.type || '').trim() === 'image-test') {
      return parsed.length > 0 ? parsed : ['python', 'pascal', 'cpp'];
    }

    return parsed;
  }, [a]);

  
  const langsForSelect = useMemo(() => {
    if (!allowedLangs || allowedLangs.length === 0) return ALL_LANGS;

    
    const set = new Set(allowedLangs);
    return ALL_LANGS.filter(x => set.has(x.value));
  }, [allowedLangs]);

  useEffect(() => {
    let alive = true;
    (async () => {
      setLoading(true);
      setError('');
      try {
        const data = await getAssignment(assignmentId);
        if (!alive) return;

        setA(data);

        const defaultLangFromApi = normalizeLang(data?.language || data?.defaultLanguage) || 'cpp';

        
        const parsedAllowed = parseAllowedLanguages(
          data?.allowedLanguages ??
          data?.courseAllowedLanguages ??
          data?.course?.allowedLanguages
        );
        
        const effectiveAllowed = (String(data?.type || '').trim() === 'image-test')
          ? (parsedAllowed.length > 0 ? parsedAllowed : ['pascal','cpp'])
          : parsedAllowed;

        let nextLang = defaultLangFromApi;

        if (effectiveAllowed.length > 0 && !effectiveAllowed.includes(nextLang)) {
          nextLang = effectiveAllowed[0];
        }

        const draft = readSolveDraft(assignmentId);
        const draftLang = normalizeLang(draft?.language);
        if (draftLang && (!effectiveAllowed.length || effectiveAllowed.includes(draftLang))) {
          nextLang = draftLang;
        }

        setLanguage(nextLang);

        if (typeof draft?.code === 'string') setCode(draft.code);
        else if (data?.starterCode) setCode(data.starterCode);
      } catch (e) {
        const msg = getApiErrorMessage(e, 'Не удалось загрузить задание');
        if (alive) {
          setError(msg);
          notify.error(msg);
        }
      } finally {
        if (alive) setLoading(false);
      }
    })();
    return () => { alive = false; };
  }, [assignmentId]);

  
  useEffect(() => {
    const onUi = () => setCodeSolveLayout(localStorage.getItem('codeSolveLayout') || 'split');
    window.addEventListener('tf-ui-settings-changed', onUi);
    return () => window.removeEventListener('tf-ui-settings-changed', onUi);
  }, []);

  useEffect(() => {
    if (!a?.id) return;
    saveSolveDraft(assignmentId, { code, language, updatedAt: new Date().toISOString() });
  }, [a?.id, assignmentId, code, language]);

  useEffect(() => {
    if (!result || !checkedDraftKey) return;
    const currentKey = `${language}\n${code}`;
    if (currentKey !== checkedDraftKey) {
      setResult(null);
      setSubmitPhase('idle');
      setError('');
    }
  }, [code, language, result, checkedDraftKey]);

  
  
  useEffect(() => {
    let alive = true;
    (async () => {
      if (!a?.courseId || !a?.id) {
        if (alive) setNextA(null);
        return;
      }
      try {
        const list = await getAssignmentsByCourse(a.courseId);
        if (!alive) return;

        const ordered = (Array.isArray(list) ? list : [])
          .slice()
          .sort((x, y) => {
            const sx = Number(x?.sort ?? 0);
            const sy = Number(y?.sort ?? 0);
            if (sx !== sy) return sx - sy;
            return String(x?.title ?? '').localeCompare(String(y?.title ?? ''));
          });

        const idx = ordered.findIndex(x => String(x?.id) === String(a.id));
        const n = (idx >= 0) ? ordered[idx + 1] : null;
        if (n?.id) setNextA({ id: n.id, title: n.title || 'Следующее задание' });
        else setNextA(null);
      } catch {
        if (alive) setNextA(null);
      }
    })();
    return () => { alive = false; };
  }, [a?.courseId, a?.id]);

  const goNextAssignment = React.useCallback(() => {
    if (!nextA?.id) return;
    nav(`/assignment/${nextA.id}`);
  }, [nextA?.id, nav]);

  
  
  useEffect(() => {
    if (!allowedLangs || allowedLangs.length === 0) return;
    if (!allowedLangs.includes(language)) {
      setLanguage(allowedLangs[0]);
    }
  }, [allowedLangs, language]);

  const onSubmit = async () => {
    if (!code.trim()) {
      notify.warn('Введите код перед отправкой');
      return;
    }
    setSubmitting(true);
    setSubmitPhase('submitting');
    setError('');
    setResult(null);
    setCheckedDraftKey('');

    try {
      let r = await submitSolution(assignmentId, { language, code });
      let solutionId = r?.id || r?.Id || r?.solutionId || r?.SolutionId || null;
      let timedOut = false;

      if (isPendingSolution(r) && solutionId) {
        setSubmitPhase('queued');
        notify.info('Решение поставлено в очередь проверки. Страница результата будет обновляться автоматически.');

        const pollResult = await waitForSolutionVerdict(solutionId, {
          maxAttempts: 18,
          onUpdate: (latest) => {
            const status = String(latest?.status || latest?.verdict || '').trim().toLowerCase();
            if (status === 'running') setSubmitPhase('running');
            else if (isPendingSolution(latest)) setSubmitPhase('queued');
          },
        });

        if (pollResult?.solution) {
          r = pollResult.solution;
          solutionId = r?.id || r?.Id || solutionId;
        }
        timedOut = pollResult?.timedOut === true && isPendingSolution(r);
        setSubmitPhase(timedOut ? 'timeout' : 'final');
      } else {
        setSubmitPhase('final');
      }

      const cases = getSolutionCases(r);
      const statusKey = getSolutionStatusKey(r);
      const explicitPassedAll = r?.passedAllTests === true || r?.passedAll === true || r?.PassedAllTests === true || r?.PassedAll === true;
      const explicitFailed = r?.passedAllTests === false || r?.passedAll === false || r?.PassedAllTests === false || r?.PassedAll === false;
      const allOk = explicitFailed
        ? false
        : explicitPassedAll || statusKey === 'accepted' || (cases.length > 0 && cases.every(isCasePassed));

      const nextResult = { ...r, __allPassed: allOk };
      setCheckedDraftKey(`${language}\n${code}`);
      setResult(nextResult);
      try { localStorage.setItem(`results:${assignmentId}`, JSON.stringify({ result: nextResult })); } catch {}
      setTimeout(() => {
        try {
          document.getElementById('solution-check-result')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
        } catch {}
      }, 0);

      if (timedOut || isPendingSolution(r)) {
        notify.info('Проверка ещё выполняется. Код остался на странице, результат обновится здесь и в «Моих решениях».');
      } else if (allOk) {
        notify.success('Все тесты пройдены!');
      } else {
        const policyInfo = getSolutionPolicyUi(nextResult);
        if (policyInfo?.kind === 'platform') {
          notify.error('Решение отклонено системой безопасности. Уберите запрещённые системные возможности.');
        } else if (policyInfo) {
          const short = policyInfo.bullets?.[0] ? `: ${policyInfo.bullets[0]}` : '';
          notify.error(`Отклонено по правилам задания${short}`);
        } else if (r?.compileError || statusKey === 'compileerror') {
          notify.error('Ошибка компиляции');
        } else if (statusKey === 'notestsconfigured') {
          notify.error('Для задания не настроены тесты');
        } else if (statusKey === 'judgeunavailable') {
          notify.error('Система проверки временно недоступна');
        } else {
          notify.error('Не все тесты пройдены');
        }
      }
    } catch (e) {
      const msg = getApiErrorMessage(e, 'Не удалось отправить решение');
      setSubmitPhase('error');
      setError(msg);
      notify.error(msg);
    } finally {
      setSubmitting(false);
    }
  };

  const policyUi = getSolutionPolicyUi(result);

  const submitMessage = ({
    submitting: 'Отправляю решение…',
    queued: 'Решение поставлено в очередь проверки',
    running: 'Проверяется execution-worker…',
    timeout: 'Проверка ещё выполняется. Результат появится в «Моих решениях».',
    final: result?.__allPassed ? 'Проверка завершена: все тесты пройдены' : 'Проверка завершена',
    error: 'Ошибка отправки решения',
  })[submitPhase] || '';

  const renderSubmitState = () => {
    const text = ({
      submitting: 'Отправляю решение…',
      queued: 'Решение поставлено в очередь проверки…',
      running: 'Решение проверяется execution-worker…',
      timeout: 'Проверка ещё выполняется. Страница результатов обновится автоматически.',
      final: result?.__allPassed ? 'Готово: решение принято.' : 'Проверка завершена.',
      error: error || 'Ошибка отправки',
    })[submitPhase] || '';
    if (!text || submitPhase === 'idle') return null;
    const danger = submitPhase === 'error';
    const success = submitPhase === 'final' && result?.__allPassed;
    const cls = danger
      ? 'border-rose-200 bg-rose-50 text-rose-800 dark:border-rose-900/50 dark:bg-rose-950/30 dark:text-rose-200'
      : success
        ? 'border-emerald-200 bg-emerald-50 text-emerald-800 dark:border-emerald-900/50 dark:bg-emerald-950/30 dark:text-emerald-200'
        : 'border-sky-200 bg-sky-50 text-sky-800 dark:border-sky-900/50 dark:bg-sky-950/30 dark:text-sky-200';
    return <div className={`rounded-xl border px-3 py-2 text-sm ${cls}`}>{text}</div>;
  };


  const renderSolutionResultCard = () => {
    if (!result) return null;

    const rawCases = getSolutionCases(result);
    const cases = rawCases.filter((c) => canViewHiddenTests || !isHiddenTestCase(c));
    const summary = getResultSummary(result);
    const output = getSolutionOutput(result);
    const pending = isPendingSolution(result);
    const solutionId = result?.id || result?.Id || result?.solutionId || result?.SolutionId || null;
    const resultUrl = `/assignment/${assignmentId}/results${solutionId ? `?solutionId=${encodeURIComponent(solutionId)}` : ''}`;

    const toneClass = {
      success: 'border-emerald-400/30 bg-emerald-500/5',
      danger: 'border-rose-400/30 bg-rose-500/5',
      info: 'border-sky-400/30 bg-sky-500/5',
    }[summary.tone] || 'border-neutral-300/30';

    const badgeClass = {
      success: 'bg-emerald-500/15 text-emerald-300 border-emerald-400/30',
      danger: 'bg-rose-500/15 text-rose-300 border-rose-400/30',
      info: 'bg-sky-500/15 text-sky-300 border-sky-400/30',
    }[summary.tone] || 'bg-white/10';

    return (
      <Card id="solution-check-result" className={`scroll-mt-24 ${toneClass}`}>
        <div className="flex flex-col gap-4">
          <div className="flex flex-col gap-3 md:flex-row md:items-start md:justify-between">
            <div>
              <div className="flex items-center gap-2 flex-wrap">
                <span className={`rounded-full border px-3 py-1 text-xs font-semibold ${badgeClass}`}>
                  {summary.title}
                </span>
              </div>
              <div className="mt-2 text-sm text-neutral-400">{summary.description}</div>
            </div>
            {solutionId ? (
              <a href={resultUrl} target="_blank" rel="noreferrer" className="btn-outline shrink-0">
                Открыть подробно
              </a>
            ) : null}
          </div>

          {policyUi ? (
            <div className="rounded-2xl border border-rose-400/30 bg-rose-500/10 p-4 text-sm">
              <div className="font-semibold text-rose-200 mb-2">{policyUi.title}</div>
              <ul className="list-disc pl-5 space-y-1 text-rose-100/90">
                {(policyUi.bullets || []).slice(0, 8).map((item, idx) => (
                  <li key={idx}>{item}</li>
                ))}
              </ul>
              {policyUi.hitLines?.length ? (
                <div className="mt-3 rounded-xl bg-black/20 p-3 text-xs text-rose-100/75 whitespace-pre-wrap">
                  {policyUi.hitLines.map((x) => `• ${x}`).join('\n')}
                </div>
              ) : null}
            </div>
          ) : null}

          {(output.message || output.stdout || output.stderr) ? (
            <div className="grid gap-3">
              {output.message && !policyUi ? (
                <div className="rounded-2xl border border-white/10 bg-black/10 p-3 text-sm text-neutral-300">
                  {displayRunnerText(output.message)}
                </div>
              ) : null}
              {output.stdout ? (
                <div>
                  <div className="text-xs text-neutral-500 mb-1">stdout</div>
                  <pre className="rounded-2xl border border-white/10 bg-black/20 p-3 text-xs whitespace-pre-wrap overflow-x-auto">
                    {displayText(output.stdout)}
                  </pre>
                </div>
              ) : null}
              {output.stderr && !policyUi ? (
                <div>
                  <div className="text-xs text-neutral-500 mb-1">stderr / compile error</div>
                  <pre className="rounded-2xl border border-rose-400/20 bg-rose-500/10 p-3 text-xs text-rose-100 whitespace-pre-wrap overflow-x-auto">
                    {displayRunnerText(output.stderr)}
                  </pre>
                </div>
              ) : null}
            </div>
          ) : null}

          <div className="space-y-3">
            <div className="font-semibold">Результаты тестов</div>
            {cases.length > 0 ? cases.map((c, i) => {
              const passed = isCasePassed(c);
              const expectedText = c.expected ?? c.expectedOutput ?? c.ExpectedOutput ?? '';
              const actualText = c.actual ?? c.actualOutput ?? c.ActualOutput ?? '';
              const inputText = c.input ?? c.Input ?? '';
              const errorText = c.compileStderr || c.stderr || c.error || '';
              const casePolicy = parsePolicyText(errorText);
              return (
                <div key={i} className={`rounded-2xl border border-white/10 bg-black/10 p-3 ${isHiddenTestCase(c) ? 'border-amber-300/40 bg-amber-500/5' : ''}`}>
                  <div className="flex items-center justify-between gap-3 mb-3">
                    <div className="flex items-center gap-2">
                      <div className="text-sm font-medium">Тест #{i + 1}</div>
                      {isHiddenTestCase(c) && <Badge intent="warning">Скрытый тест</Badge>}
                    </div>
                    <span className={`rounded-full px-2 py-0.5 text-xs ${passed ? 'bg-emerald-500/15 text-emerald-300' : 'bg-rose-500/15 text-rose-300'}`}>
                      {passed ? 'OK' : 'FAIL'}
                    </span>
                  </div>

                  {inputText !== '' ? (
                    <div className="mb-2">
                      <div className="text-xs text-neutral-500 mb-1">Ввод</div>
                      <pre className="whitespace-pre-wrap text-sm">{displayText(inputText)}</pre>
                    </div>
                  ) : null}

                  {expectedText !== '' ? (
                    <div className="mb-2">
                      <div className="text-xs text-neutral-500 mb-1">Ожидаемый вывод</div>
                      <pre className="whitespace-pre-wrap text-sm">{displayText(expectedText)}</pre>
                    </div>
                  ) : null}

                  {actualText !== '' ? (
                    <div className="mb-2">
                      <div className="text-xs text-neutral-500 mb-1">Фактически</div>
                      <pre className="whitespace-pre-wrap text-sm">{displayText(actualText)}</pre>
                    </div>
                  ) : null}

                  {(c.referenceUrl || c.ReferenceUrl || c.expectedUrl || c.submittedUrl || c.SubmittedUrl || c.actualUrl) ? (
                    <div className="grid sm:grid-cols-2 gap-3 my-3">
                      {(c.referenceUrl || c.ReferenceUrl || c.expectedUrl) ? (
                        <div>
                          <div className="text-xs text-neutral-500 mb-1">Ожидаемая картинка</div>
                          <img className="max-h-56 rounded-xl border border-white/10 bg-white object-contain" src={c.referenceUrl || c.ReferenceUrl || c.expectedUrl} alt={`expected-${i + 1}`} />
                        </div>
                      ) : null}
                      {(c.submittedUrl || c.SubmittedUrl || c.actualUrl) ? (
                        <div>
                          <div className="text-xs text-neutral-500 mb-1">Полученная картинка</div>
                          <img className="max-h-56 rounded-xl border border-white/10 bg-white object-contain" src={c.submittedUrl || c.SubmittedUrl || c.actualUrl} alt={`actual-${i + 1}`} />
                        </div>
                      ) : null}
                    </div>
                  ) : null}

                  {typeof (c.similarityPercent ?? c.SimilarityPercent ?? c.similarity) === 'number' ? (
                    <div className="mb-2 text-xs text-neutral-400">
                      Схожесть: {Math.round(c.similarityPercent ?? c.SimilarityPercent ?? c.similarity)}% · порог: {Math.round(c.thresholdPercent ?? c.ThresholdPercent ?? c.threshold ?? 0)}%
                    </div>
                  ) : null}

                  {errorText && !casePolicy ? (
                    <div>
                      <div className="text-xs text-neutral-500 mb-1">Ошибки</div>
                      <pre className="whitespace-pre-wrap text-xs text-rose-200">{displayRunnerText(errorText)}</pre>
                    </div>
                  ) : null}
                </div>
              );
            }) : pending ? (
              <div className="rounded-2xl border border-white/10 p-4 text-sm text-neutral-400">
                Жду результат проверки…
              </div>
            ) : (
              <div className="rounded-2xl border border-white/10 p-4 text-sm text-neutral-400">
                Детальных тест-кейсов в ответе нет. Итоговый статус показан выше.
              </div>
            )}
          </div>
        </div>
      </Card>
    );
  };

  useEffect(() => {
    let alive = true;
    (async () => {
      if (!isAdmin || !assignmentId) return;
      try {
        const stats = await getAdminAssignmentInsights(assignmentId);
        if (alive) setAdminInsights(stats);
      } catch {
        if (alive) setAdminInsights(null);
      }
    })();
    return () => { alive = false; };
  }, [assignmentId, isAdmin]);

  const renderAdminQuickInsights = () => {
    if (!isAdmin || !adminInsights) return null;
    const totalAttempts = (adminInsights.codeAttempts || 0) + (adminInsights.testAttempts || 0) + (adminInsights.imageAttempts || 0);
    const successRate = totalAttempts > 0 ? Math.round((adminInsights.successUsers || 0) / Math.max(adminInsights.uniqueUsers || 1, 1) * 100) : 0;
    return (
      <Card className="mb-6">
        <div className="flex items-center justify-between gap-3 mb-4">
          <div>
            <div className="font-semibold">Быстрая статистика задания</div>
            <div className="text-sm text-neutral-500 mt-1">Этот блок виден только администратору.</div>
          </div>
          <Link to={`/admin/assignments/${assignmentId}/insights`} className="btn-outline">
            <BarChart3 size={16} className="mr-2" /> Полная аналитика
          </Link>
        </div>
        <div className="grid sm:grid-cols-2 xl:grid-cols-4 gap-3">
          <div className="rounded-2xl border px-4 py-3"><div className="text-xs opacity-60">Пользователи</div><div className="text-2xl font-semibold mt-1">{adminInsights.uniqueUsers || 0}</div></div>
          <div className="rounded-2xl border px-4 py-3"><div className="text-xs opacity-60">Решили</div><div className="text-2xl font-semibold mt-1">{adminInsights.successUsers || 0}</div></div>
          <div className="rounded-2xl border px-4 py-3"><div className="text-xs opacity-60">Попытки</div><div className="text-2xl font-semibold mt-1">{totalAttempts}</div></div>
          <div className="rounded-2xl border px-4 py-3"><div className="text-xs opacity-60">Успешность</div><div className="text-2xl font-semibold mt-1">{successRate}%</div></div>
        </div>
      </Card>
    );
  };

  if (loading) {
    return (
      <Layout>
        <div className="text-neutral-500">Загрузка…</div>
      </Layout>
    );
  }
  if (!a) {
    return (
      <Layout>
        <div className="text-red-600">{error || 'Задание не найдено'}</div>
      </Layout>
    );
  }

  
  if (a.type === 'test') {
    return (
      <Layout>
        
        <div className="flex items-center justify-between mb-6">
          <div className="flex items-center gap-2">
            <Button
              variant="ghost"
              className="inline-flex items-center gap-1"
              onClick={() => nav(`/course/${a.courseId}`)}
            >
              <ArrowLeft size={16} /> к заданиям курса
            </Button>
          </div>
          <div className="flex items-center gap-2 flex-wrap">
            <QuotaPill bucket="tasks" />
            {isAdmin && (
              <Link to={`/admin/assignments/${a.id}/insights`} className="btn-outline">
                <BarChart3 size={16} className="mr-2" /> Аналитика задания
              </Link>
            )}
            <IfEditor>
              <Link to={`/assignment/${a.id}/edit`} className="btn-outline">
                Редактировать
              </Link>
            </IfEditor>
          </div>
        </div>

        {renderAdminQuickInsights()}
        <TaskTestSolve assignment={a} assignmentId={a.id} />
      </Layout>
    );
  }


  
  if (a.type === 'math') {
    return (
      <Layout>
        <div className="flex items-center justify-between mb-6">
          <div className="flex items-center gap-2">
            <Button
              variant="ghost"
              className="inline-flex items-center gap-1"
              onClick={() => nav(`/course/${a.courseId}`)}
            >
              <ArrowLeft size={16} /> к заданиям курса
            </Button>
          </div>
          <div className="flex items-center gap-2 flex-wrap">
            <QuotaPill bucket="tasks" />
            {isAdmin && (
              <Link to={`/admin/assignments/${a.id}/insights`} className="btn-outline">
                <BarChart3 size={16} className="mr-2" /> Аналитика задания
              </Link>
            )}
            <IfEditor>
              <Link to={`/assignment/${a.id}/edit`} className="btn-outline">
                Редактировать
              </Link>
            </IfEditor>
          </div>
        </div>

        {renderAdminQuickInsights()}
        <MathTaskSolve assignment={a} assignmentId={a.id} />
      </Layout>
    );
  }

  
  if (a.type === 'image-test') {
    const expectedUrl = imageReferenceUrlFromAssignment(a);
    const configuredImageCases = imageTestCasesFromAssignment(a);
    const hasConfiguredImageCases = configuredImageCases.some((t) => t?.hasExpectedImage || t?.expectedImageUrl || t?.referenceUrl || t?.expectedImageKey || t?.referenceKey || t?.imageKey || t?.expectedImageBase64 || t?.referenceBase64 || t?.imageBase64);

    const imageLangs = langsForSelect.filter((l) => ['python', 'pascal', 'cpp'].includes(l.value));

    const openImageResultsUrl = (url) => {
      try {
        const opened = window.open(url, '_blank', 'noopener,noreferrer');
        if (!opened) nav(url);
      } catch {
        nav(url);
      }
    };

    const onTrialImageTest = async () => {
      if (!code.trim()) {
        notify.warn('Введите код перед пробным запуском');
        return;
      }
      setImgError(null);
      setImgCompare(null);
      setImgBusy(true);

      try {
        const resp = await runImageTestCode(assignmentId, language, code, imageInput);
        const normalized = normalizeImageTaskResult(resp, expectedUrl, {
          isTrial: true,
          code,
          language,
          assignmentTitle: a.title,
        });

        if (hasImageResultPayload(resp) && normalized.actualUrl) {
          setImgCompare(normalized);
        } else {
          const errMsg = buildImageTaskResponseText(resp, 'Не удалось сгенерировать картинку');
          setImgError(errMsg);
        }
      } catch (e) {
        const errMsg = buildImageTaskErrorText(e, 'Не удалось выполнить пробный запуск');
        setImgError(errMsg);
      } finally {
        setImgBusy(false);
      }
    };

    const onSubmitImageTest = async () => {
      if (!code.trim()) {
        notify.warn('Введите код перед отправкой');
        return;
      }
      if (!expectedUrl && !hasConfiguredImageCases) {
        setImgError('Image-тесты не настроены. В режиме редактирования добавьте Input, Expected output и Expected image.');
        return;
      }

      setImgError(null);
      setImgCompare(null);
      setImgBusy(true);

      try {
        const resp = await submitImageTestCode(assignmentId, language, code, imageInput);
        const normalized = normalizeImageTaskResult(resp, expectedUrl, {
          isTrial: false,
          code,
          language,
          assignmentTitle: a.title,
        });

        if (hasImageResultPayload(resp) && normalized.actualUrl) {
          setImgCompare(normalized);
          try { localStorage.setItem(`image-results:${assignmentId}`, JSON.stringify(normalized)); } catch {}

          if (normalized.passed) {
            notify.success(`Задание выполнено! Схожесть: ${Math.round(normalized.similarityPercent ?? 0)}%`);
          } else if (normalized.similarityPercent !== null && normalized.thresholdPercent !== null) {
            notify.warn(`Схожесть ${Math.round(normalized.similarityPercent)}% < ${Math.round(normalized.thresholdPercent)}%`);
          } else {
            notify.warn('Решение отправлено, но сравнение не вернуло проценты схожести');
          }

          const idParam = normalized.solutionId ? `?solutionId=${encodeURIComponent(normalized.solutionId)}` : '';
          const imageResultUrl = `/assignment/${assignmentId}/image-results${idParam}`;
          normalized.resultUrl = imageResultUrl;
        } else {
          const errMsg = buildImageTaskResponseText(resp, 'Не удалось проверить решение');
          setImgError(errMsg);
        }
      } catch (e) {
        const errMsg = buildImageTaskErrorText(e, 'Не удалось отправить решение');
        setImgError(errMsg);
        notify.error(errMsg);
      } finally {
        setImgBusy(false);
      }
    };

    return (
      <Layout>
        
        <div className="flex items-center justify-between mb-6">
          <div className="flex items-center gap-2">
            <Button
              variant="ghost"
              className="inline-flex items-center gap-1"
              onClick={() => nav(`/course/${a.courseId}`)}
            >
              <ArrowLeft size={16} /> к заданиям курса
            </Button>
          </div>
          <div className="flex items-center gap-2 flex-wrap">
            <QuotaPill bucket="tasks" />
            {isAdmin && (
              <Link to={`/admin/assignments/${a.id}/insights`} className="btn-outline">
                <BarChart3 size={16} className="mr-2" /> Аналитика задания
              </Link>
            )}
            <IfEditor>
              <Link to={`/assignment/${a.id}/edit`} className="btn-outline">
                Редактировать
              </Link>
            </IfEditor>
          </div>
        </div>

        {renderAdminQuickInsights()}

        <div className="grid lg:grid-cols-3 gap-6">
          
          <div className="lg:col-span-2 space-y-5">
            <Card>
              <h1 className="text-2xl font-semibold mb-1">{a.title}</h1>
              {a.tags && (
                <div className="flex flex-wrap gap-2 mb-3">
                  {a.tags
                    .split(',')
                    .filter(Boolean)
                    .map((t) => (
                      <Badge key={t.trim()}>{t.trim()}</Badge>
                    ))}
                </div>
              )}
              <StatementViewer value={a.description} />
            </Card>

            <Card>
              <div className="flex items-center justify-between mb-3">
                <div className="font-medium">Эталон</div>
              </div>

              {expectedUrl ? (
                <div className="rounded border overflow-hidden bg-white dark:bg-neutral-950">
                  <img
                    src={expectedUrl}
                    alt="Эталон"
                    className="w-full max-h-[70vh] object-contain"
                  />
                </div>
              ) : hasConfiguredImageCases ? (
                <div className="text-neutral-500">
                  Эталонная картинка настроена, но скрыта от ученика. Проверка всё равно выполнит сравнение по картинке.
                </div>
              ) : (
                <div className="text-neutral-500">
                  Эталонная картинка не настроена. Открой «Редактировать» и нажми «Загрузить эталон».
                </div>
              )}
            </Card>

            {renderSolutionResultCard()}
          </div>

          
          <div className="space-y-4">
            <Card>
              <div className="grid gap-3">
                <div>
                  <label className="label">Язык</label>
                  <Select
                    value={language}
                    onChange={(e) => setLanguage(e.target.value)}
                  >
                    {imageLangs.map((l) => (
                      <option key={l.value} value={l.value}>{l.label}</option>
                    ))}
                  </Select>
                  <div className="text-xs text-neutral-500 mt-1">
                    Для image-test доступны Python Turtle/matplotlib, Pascal GraphABC, C++ GLUT и C++ Turtle. Runner принимает stdin и сравнивает stdout + картинку.
                  </div>
                </div>

                <div>
                  <label className="label">Код</label>
                  <div className="rounded-xl overflow-hidden border border-neutral-200 dark:border-neutral-700">
                    <CodeEditor
                      value={code}
                      onChange={setCode}
                      language={language}
                    />
                  </div>
                </div>

                <div>
                  <label className="label">Входные данные для программы</label>
                  <textarea
                    value={imageInput}
                    onChange={(e) => setImageInput(e.target.value)}
                    rows={4}
                    placeholder={language === 'cpp' ? 'Если программа читает stdin, введи данные сюда' : 'Необязательно. Можно оставить пустым.'}
                    className="w-full rounded-xl border border-neutral-200 dark:border-neutral-700 bg-white dark:bg-neutral-900 px-3 py-2 text-sm"
                  />
                  <div className="text-xs text-neutral-500 mt-1">Эти данные передаются в stdin при пробном запуске. При отправке используются input-ы из тестов задания, если они настроены.</div>
                </div>

                {imgError ? (
                  <div className="text-rose-700 dark:text-rose-300 whitespace-pre-wrap">
                    {imgError}
                  </div>
                ) : null}

                
                {imgCompare && (
                  <Card className="p-4 space-y-3 border-emerald-400/30 bg-emerald-500/5">
                    <div className="flex items-center justify-between gap-3">
                      <h3 className="font-semibold">Результат</h3>
                      <div className="flex items-center gap-2">
                        {imgCompare.resultUrl && !imgCompare.isTrial ? (
                          <a href={imgCompare.resultUrl} target="_blank" rel="noreferrer" className="btn-outline text-xs px-3 py-1.5">
                            Подробно
                          </a>
                        ) : null}
                        {imgCompare.isTrial ? (
                          <Badge variant="outline">Пробник</Badge>
                        ) : imgCompare.passed ? (
                          <Badge intent="success">Пройдено ✓</Badge>
                        ) : (
                          <Badge intent="danger">Не пройдено</Badge>
                        )}
                      </div>
                    </div>

                    {!imgCompare.isTrial && imgCompare.similarityPercent != null && (
                      <div className="text-sm">
                        <div>Схожесть: <strong>{Math.round(imgCompare.similarityPercent)}%</strong></div>
                        <div>Порог: <strong>{Math.round(imgCompare.thresholdPercent)}%</strong></div>
                      </div>
                    )}

                    <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                      {imgCompare.expectedUrl && (
                        <div>
                          <div className="text-xs font-medium mb-1">Эталон</div>
                          <img 
                            src={imgCompare.expectedUrl} 
                            alt="Эталон" 
                            className="w-full border border-neutral-300 dark:border-neutral-600 rounded"
                          />
                        </div>
                      )}
                      {imgCompare.actualUrl && (
                        <div>
                          <div className="text-xs font-medium mb-1">Ваш результат</div>
                          <img 
                            src={imgCompare.actualUrl} 
                            alt="Результат" 
                            className="w-full border border-neutral-300 dark:border-neutral-600 rounded"
                          />
                        </div>
                      )}
                    </div>
                  </Card>
                )}

                <Button
                  variant="outline"
                  onClick={() => {
                    const url = `/assignment/${assignmentId}/image-results`;
                    openImageResultsUrl(url);
                  }}
                >
                  Открыть последние результаты
                </Button>

                <div className="text-xs text-neutral-500">
                  Пробник возвращает картинку без сравнения. Отправка выполняет сравнение с эталоном.
                </div>
              </div>
            </Card>
          </div>
        </div>

        
        <div
          className="fixed right-6 z-50"
          style={{ bottom: 'calc(env(safe-area-inset-bottom) + 84px)' }}
        >
          <div
            className="flex flex-col gap-2 rounded-2xl p-2 border shadow-lg w-56"
            style={{
              background: 'rgba(var(--card) / 0.60)',
              borderColor: 'rgba(var(--border) / 0.70)',
              backdropFilter: 'blur(14px)',
              WebkitBackdropFilter: 'blur(14px)',
            }}
          >
            {nextA?.id && (
              <Button
                className="w-full"
                variant="outline"
                onClick={goNextAssignment}
                title={nextA?.title || 'Следующее задание'}
              >
                Следующее задание
              </Button>
            )}
            <Button
              className="w-full"
              variant="outline"
              onClick={onTrialImageTest}
              disabled={imgBusy || !code.trim()}
            >
              {imgBusy ? 'Генерация картинки...' : 'Пробник'}
            </Button>
            <Button
              className="w-full"
              onClick={onSubmitImageTest}
              disabled={imgBusy || !code.trim() || (!expectedUrl && !hasConfiguredImageCases)}
            >
              {imgBusy ? 'Отправка...' : 'Отправить (сравнение)'}
            </Button>
          </div>
        </div>
      </Layout>
    );
  }

  const canViewHiddenTests = isAdmin || a?.canEdit === true;
  const allAssignmentTests = Array.isArray(a.testCases) ? a.testCases : [];
  const visibleTests = canViewHiddenTests ? allAssignmentTests : allAssignmentTests.filter((t) => !isHiddenTestCase(t));
  const assignmentTestsTitle = canViewHiddenTests ? 'Тесты задания' : 'Публичные тесты';
  const emptyAssignmentTestsText = canViewHiddenTests ? 'У задания нет тестов.' : 'У задания нет публичных тестов.';

  const submitStatusText = submitMessage;
  const submitButtonLabel = ({
    submitting: 'Отправка…',
    queued: 'В очереди…',
    running: 'Проверяется…',
  })[submitPhase] || 'Отправить';

  return (
    <Layout>
      
      <div className="flex items-center justify-between mb-6">
        <div className="flex items-center gap-2">
          <Button
            variant="ghost"
            className="inline-flex items-center gap-1"
            onClick={() => nav(`/course/${a.courseId}`)}
          >
            <ArrowLeft size={16} /> к заданиям курса
          </Button>
        </div>
        <div className="flex items-center gap-2">
          {isAdmin && (
            <Link to={`/admin/assignments/${a.id}/insights`} className="btn-outline">
              <BarChart3 size={16} className="mr-2" /> Аналитика задания
            </Link>
          )}
          <IfEditor>
            <Link to={`/assignment/${a.id}/edit`} className="btn-outline">
              Редактировать
            </Link>
          </IfEditor>
        </div>
      </div>

      {renderAdminQuickInsights()}

      
      {codeSolveLayout !== 'editorTop' ? (
        <div className="grid lg:grid-cols-3 gap-6">
          
          <div className="lg:col-span-2 space-y-5">
            <Card>
              <h1 className="text-2xl font-semibold mb-1">{a.title}</h1>
              {a.tags && (
                <div className="flex flex-wrap gap-2 mb-3">
                  {a.tags
                    .split(',')
                    .filter(Boolean)
                    .map((t) => (
                      <Badge key={t.trim()}>{t.trim()}</Badge>
                    ))}
                </div>
              )}
              <StatementViewer value={a.description} />
            </Card>

            <Card>
              <div className="flex items-center justify-between mb-3">
                <div className="font-medium">{assignmentTestsTitle}</div>
              </div>

              {visibleTests.length === 0 ? (
                <div className="text-neutral-500">{emptyAssignmentTestsText}</div>
              ) : (
                <div className="space-y-3">
                  {visibleTests.map((t, i) => {
                    const expectedText = t.expected ?? t.expectedOutput ?? t.ExpectedOutput ?? '';
                    return (
                      <div key={i} className={`rounded border p-3 ${isHiddenTestCase(t) ? 'border-amber-300/60 bg-amber-500/5' : ''}`}>
                        <div className="flex items-center justify-between gap-2 mb-1">
                          <div className="text-xs text-neutral-500">Ввод</div>
                          {isHiddenTestCase(t) && <Badge intent="warning">Скрытый тест</Badge>}
                        </div>
                        <pre className="whitespace-pre-wrap text-sm">{t.input ?? t.Input ?? ''}</pre>

                        {(expectedText ?? '') !== '' && (
                          <>
                            <div className="text-xs text-neutral-500 mt-2 mb-1">Ожидаемый вывод</div>
                            <pre className="whitespace-pre-wrap text-sm">{expectedText}</pre>
                          </>
                        )}
                      </div>
                    );
                  })}
                </div>
              )}
            </Card>

            {renderSolutionResultCard()}
          </div>

          
          <div className="space-y-4">
            <Card>
              <div className="grid gap-3">
                <div>
                  <label className="label">Язык</label>
                  <Select value={language} onChange={(e) => setLanguage(e.target.value)}>
                    {langsForSelect.map((l) => (
                      <option key={l.value} value={l.value}>{l.label}</option>
                    ))}
                  </Select>

                  
                  {allowedLangs && allowedLangs.length > 0 && (
                    <div className="text-xs text-neutral-500 mt-1">
                      Языки ограничены курсом: {langsForSelect.map(x => x.label).join(', ')}
                    </div>
                  )}
                </div>

                <div>
                  <label className="label">Режим ввода</label>
                  <Select
                    value={plainMode ? 'plain' : 'editor'}
                    onChange={(e) => setPlainMode(e.target.value === 'plain')}
                  >
                    <option value="editor">Редактор кода</option>
                    <option value="plain">Простой текст</option>
                  </Select>
                </div>

                <div>
                  <label className="label">Ваш код</label>
                  {plainMode ? (
                    <Textarea value={code} onChange={(e) => setCode(e.target.value)} rows={16} />
                  ) : (
                    <CodeEditor language={language} value={code} onChange={setCode} height={380} />
                  )}
                </div>

                {result && (
                  <div className="flex items-center gap-2 text-sm">
                    {result.__allPassed ? (
                      <>
                        <CheckCircle2 className="text-emerald-600" size={16} /> Все тесты пройдены
                      </>
                    ) : (
                      <>
                        <XCircle className="text-red-600" size={16} /> Не все тесты пройдены
                      </>
                    )}
                  </div>
                )}

                {policyUi && (
                  <div className="rounded border border-red-200 bg-red-50 p-3 text-sm">
                    <div className="font-medium text-red-800 mb-2">{policyUi.title}</div>
                    <ul className="list-disc pl-5 text-red-800 space-y-1">
                      {(policyUi.bullets || []).slice(0, 4).map((x, i) => (
                        <li key={i}>{x}</li>
                      ))}
                    </ul>
                  </div>
                )}

                {error && <div className="text-sm text-red-600">{error}</div>}
                {renderSubmitState()}
              </div>
            </Card>
          </div>
        </div>
      ) : (
        
        <div className="space-y-6">
          <Card>
            <div className="grid gap-3">
              <div className="grid gap-3 md:grid-cols-2">
                <div>
                  <label className="label">Язык</label>
                  <Select value={language} onChange={(e) => setLanguage(e.target.value)}>
                    {langsForSelect.map((l) => (
                      <option key={l.value} value={l.value}>{l.label}</option>
                    ))}
                  </Select>
                  {allowedLangs && allowedLangs.length > 0 && (
                    <div className="text-xs text-neutral-500 mt-1">
                      Языки ограничены курсом: {langsForSelect.map(x => x.label).join(', ')}
                    </div>
                  )}
                </div>

                <div>
                  <label className="label">Режим ввода</label>
                  <Select
                    value={plainMode ? 'plain' : 'editor'}
                    onChange={(e) => setPlainMode(e.target.value === 'plain')}
                  >
                    <option value="editor">Редактор кода</option>
                    <option value="plain">Простой текст</option>
                  </Select>
                </div>
              </div>

              <div>
                <div className="flex items-center justify-between gap-3 mb-2">
                  <div className="font-semibold text-lg">{a.title}</div>
                  {result && (
                    <div className="flex items-center gap-2 text-sm">
                      {result.__allPassed ? (
                        <>
                          <CheckCircle2 className="text-emerald-600" size={16} /> Все тесты пройдены
                        </>
                      ) : (
                        <>
                          <XCircle className="text-red-600" size={16} /> Не все тесты пройдены
                        </>
                      )}
                    </div>
                  )}
                </div>

                {policyUi && (
                  <div className="rounded border border-red-200 bg-red-50 p-3 text-sm mb-3">
                    <div className="font-medium text-red-800 mb-2">{policyUi.title}</div>
                    <ul className="list-disc pl-5 text-red-800 space-y-1">
                      {(policyUi.bullets || []).slice(0, 4).map((x, i) => (
                        <li key={i}>{x}</li>
                      ))}
                    </ul>
                  </div>
                )}

                {a.tags && (
                  <div className="flex flex-wrap gap-2 mb-3">
                    {a.tags
                      .split(',')
                      .filter(Boolean)
                      .map((t) => (
                        <Badge key={t.trim()}>{t.trim()}</Badge>
                      ))}
                  </div>
                )}

                <label className="label">Ваш код</label>
                {plainMode ? (
                  <Textarea value={code} onChange={(e) => setCode(e.target.value)} rows={18} />
                ) : (
                  <CodeEditor language={language} value={code} onChange={setCode} height={460} />
                )}
              </div>

              {error && <div className="text-sm text-red-600">{error}</div>}
              {renderSubmitState()}
            </div>
          </Card>

          <Card>
            <StatementViewer value={a.description} />
          </Card>

          <Card>
            <div className="flex items-center justify-between mb-3">
              <div className="font-medium">{assignmentTestsTitle}</div>
            </div>

            {visibleTests.length === 0 ? (
              <div className="text-neutral-500">{emptyAssignmentTestsText}</div>
            ) : (
              <div className="space-y-3">
                {visibleTests.map((t, i) => {
                  const expectedText = t.expected ?? t.expectedOutput ?? t.ExpectedOutput ?? '';
                  return (
                    <div key={i} className={`rounded border p-3 ${isHiddenTestCase(t) ? 'border-amber-300/60 bg-amber-500/5' : ''}`}>
                      <div className="flex items-center justify-between gap-2 mb-1">
                        <div className="text-xs text-neutral-500">Ввод</div>
                        {isHiddenTestCase(t) && <Badge intent="warning">Скрытый тест</Badge>}
                      </div>
                      <pre className="whitespace-pre-wrap text-sm">{t.input ?? t.Input ?? ''}</pre>

                      {(expectedText ?? '') !== '' && (
                        <>
                          <div className="text-xs text-neutral-500 mt-2 mb-1">Ожидаемый вывод</div>
                          <pre className="whitespace-pre-wrap text-sm">{expectedText}</pre>
                        </>
                      )}
                    </div>
                  );
                })}
              </div>
            )}
          </Card>

          {renderSolutionResultCard()}
        </div>
      )}

      
      <div
        className="fixed right-6 z-50"
        style={{ bottom: 'calc(env(safe-area-inset-bottom) + 84px)' }}
      >
        <div
          className="flex flex-col gap-2 rounded-2xl p-2 border shadow-lg"
          style={{
            background: 'rgba(var(--card) / 0.60)',
            borderColor: 'rgba(var(--border) / 0.70)',
            backdropFilter: 'blur(14px)',
            WebkitBackdropFilter: 'blur(14px)',
          }}
        >
          {nextA?.id && (
            <Button variant="outline" onClick={goNextAssignment} title={nextA?.title || 'Следующее задание'}>
              Следующее задание
            </Button>
          )}
          {submitStatusText ? (
            <div className="max-w-56 rounded-xl px-3 py-2 text-xs text-neutral-600 dark:text-neutral-300 bg-white/70 dark:bg-neutral-900/60">
              {submitStatusText}
            </div>
          ) : null}
          <Button onClick={onSubmit} disabled={submitting || !code.trim()}>
            <Play size={16} className="mr-1" />
            {submitButtonLabel}
          </Button>
        </div>
      </div>
    </Layout>
  );
}