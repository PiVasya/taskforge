
import React, { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';

import Layout from '../components/Layout';
import QuotaPill from '../components/QuotaPill';
import { Card, Button, Select, Badge } from '../components/ui';
import IfEditor from '../components/IfEditor';
import CodeEditor from '../components/CodeEditor';
import TaskTestSolve from './TaskTestSolve';
import MathTaskSolve from './MathTaskSolve';
import StatementViewer from '../components/tiptap/StatementViewer';

import { useNotify } from '../components/notify/NotifyProvider';
import { getAssignment, getAssignmentSolveShell, getAssignmentStatement, getAssignmentTests, getAssignmentsByCourse } from '../api/assignments';
import { submitSolution, getMySolutionDetails } from '../api/solutions';
import { runImageTestCode, submitImageTestCode } from '../api/imageTests';
import { recordAssignmentActivityBatch, sendAssignmentActivityBeacon } from '../api/assignmentActivity';
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
const ASSIGNMENT_MIN_REVEAL_MS = 500;

function wait(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

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

function sameAssignmentId(left, right) {
  return String(left || '').trim().toLowerCase() === String(right || '').trim().toLowerCase();
}

function getSolveDraftKey(assignmentId) {
  return `solve-draft:v2:${assignmentId}`;
}

function readSolveDraft(assignmentId) {
  if (!assignmentId) return null;
  try {
    const raw = localStorage.getItem(getSolveDraftKey(assignmentId));
    if (!raw) return null;
    const parsed = JSON.parse(raw);
    if (!parsed || typeof parsed !== 'object') return null;
    if (!sameAssignmentId(parsed.assignmentId, assignmentId)) return null;
    return parsed;
  } catch {
    return null;
  }
}

function saveSolveDraft(assignmentId, draft) {
  if (!assignmentId) return;
  try {
    localStorage.setItem(getSolveDraftKey(assignmentId), JSON.stringify({
      ...draft,
      assignmentId,
      version: 2,
    }));
  } catch {}
}


function createActivitySessionId(assignmentId) {
  const randomPart = Math.random().toString(36).slice(2, 10);
  const timePart = Date.now().toString(36);
  return `solve:${assignmentId}:${timePart}:${randomPart}`;
}

function clampActivityText(value, max = 1200) {
  const text = typeof value === 'string' ? value : '';
  if (!text) return '';
  return text.length <= max ? text : text.slice(0, max);
}

function hashActivityText(value) {
  const text = typeof value === 'string' ? value : '';
  let h = 2166136261;
  for (let i = 0; i < text.length; i += 1) {
    h ^= text.charCodeAt(i);
    h = Math.imul(h, 16777619);
  }
  return `fnv1a:${(h >>> 0).toString(16).padStart(8, '0')}:${text.length}`;
}

function clipboardTextFromEvent(event) {
  try {
    const text = event?.clipboardData?.getData?.('text/plain');
    if (typeof text === 'string' && text.length > 0) return text;
    const selection = typeof window !== 'undefined' ? window.getSelection?.()?.toString?.() : '';
    return typeof selection === 'string' ? selection : '';
  } catch {
    return '';
  }
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

function InputTextPreview({ value }) {
  const text = displayText(value);
  if (text === '') {
    return <pre className="whitespace-pre-wrap text-sm text-neutral-500 italic">Входные данные отсутствуют</pre>;
  }

  return <pre className="whitespace-pre-wrap text-sm">{text}</pre>;
}



function clampNumber(value, min, max) {
  return Math.min(max, Math.max(min, value));
}

function TypewriterText({ as: Tag = 'span', text, className = '', playKey = '', durationMs = 480, startDelay = 0, onDone }) {
  const source = String(text ?? '');
  const [visibleCount, setVisibleCount] = useState(0);
  const doneRef = React.useRef(onDone);

  useEffect(() => {
    doneRef.current = onDone;
  }, [onDone]);

  useEffect(() => {
    let frame = 0;
    let timer = 0;
    let startedAt = 0;
    let closed = false;
    const reduced = typeof window !== 'undefined' && window.matchMedia?.('(prefers-reduced-motion: reduce)')?.matches;
    const duration = Math.max(80, Number(durationMs) || 480);

    const finish = () => {
      if (closed) return;
      setVisibleCount(source.length);
      doneRef.current?.();
    };

    setVisibleCount(reduced ? source.length : 0);
    if (!source || reduced) {
      doneRef.current?.();
      return undefined;
    }

    const tick = (timestamp) => {
      if (closed) return;
      if (!startedAt) startedAt = timestamp;
      const progress = Math.min(1, (timestamp - startedAt) / duration);
      setVisibleCount(Math.min(source.length, Math.max(1, Math.ceil(source.length * progress))));
      if (progress >= 1) finish();
      else frame = window.requestAnimationFrame(tick);
    };

    timer = window.setTimeout(() => {
      frame = window.requestAnimationFrame(tick);
    }, Math.max(0, Number(startDelay) || 0));

    return () => {
      closed = true;
      window.clearTimeout(timer);
      window.cancelAnimationFrame(frame);
    };
  }, [source, playKey, durationMs, startDelay]);

  const done = visibleCount >= source.length;
  return (
    <Tag className={`typewriter-text ${done ? 'typewriter-text--done' : ''} ${className}`} aria-label={source}>
      <span aria-hidden="true">{source.slice(0, visibleCount)}</span>
      {!done && <span className="typewriter-cursor" aria-hidden="true" />}
    </Tag>
  );
}

function AnimatedHeading({ text, playKey, className = 'text-2xl font-semibold mb-1', onDone }) {
  const value = String(text ?? '');
  return (
    <TypewriterText
      as="h1"
      text={value}
      playKey={`${playKey}:title:${value}`}
      durationMs={clampNumber(value.length * 18, 180, 620)}
      className={className}
      onDone={onDone}
    />
  );
}

function SmoothHeightReveal({
  active = true,
  loading = false,
  playKey = '',
  collapsedHeight = 76,
  skeletonLines = 3,
  className = '',
  children,
  onDone,
}) {
  const contentRef = React.useRef(null);
  const doneRef = React.useRef(onDone);
  const [height, setHeight] = useState(collapsedHeight);
  const [ready, setReady] = useState(false);
  const [durationMs, setDurationMs] = useState(420);
  const shouldReveal = active && !loading;

  useEffect(() => {
    doneRef.current = onDone;
  }, [onDone]);

  React.useLayoutEffect(() => {
    let raf = 0;
    let timer = 0;
    let doneTimer = 0;
    let observer = null;
    const reduced = typeof window !== 'undefined' && window.matchMedia?.('(prefers-reduced-motion: reduce)')?.matches;

    setReady(false);
    setHeight(collapsedHeight);

    if (!shouldReveal) {
      return () => {};
    }

    const measure = () => {
      const node = contentRef.current;
      const nextHeight = Math.max(collapsedHeight, Math.ceil(node?.scrollHeight || collapsedHeight));
      const nextDuration = reduced ? 0 : clampNumber(280 + nextHeight * 0.28, 360, 860);
      setDurationMs(nextDuration);
      setHeight(nextHeight);
      return nextDuration;
    };

    timer = window.setTimeout(() => {
      raf = window.requestAnimationFrame(() => {
        const nextDuration = measure();
        setReady(true);
        doneTimer = window.setTimeout(() => doneRef.current?.(), nextDuration + 90);

        if (typeof ResizeObserver !== 'undefined' && contentRef.current) {
          observer = new ResizeObserver(() => measure());
          observer.observe(contentRef.current);
        }
      });
    }, reduced ? 0 : 35);

    return () => {
      window.clearTimeout(timer);
      window.clearTimeout(doneTimer);
      window.cancelAnimationFrame(raf);
      if (observer) observer.disconnect();
    };
  }, [shouldReveal, collapsedHeight, playKey]);

  return (
    <div
      className={`smooth-height-reveal ${shouldReveal ? 'smooth-height-reveal--content' : 'smooth-height-reveal--placeholder'} ${ready ? 'smooth-height-reveal--ready' : ''} ${className}`}
      style={{ height: `${height}px`, '--solve-reveal-duration': `${durationMs}ms` }}
    >
      <div ref={contentRef} className="smooth-height-reveal-inner">
        {shouldReveal ? children : <SolveSkeletonLines lines={skeletonLines} />}
      </div>
    </div>
  );
}

function AnimatedStatementViewer({ value, playKey, active = true, loading = false, collapsedHeight = 84, skeletonLines = 3, onDone }) {
  return (
    <SmoothHeightReveal
      active={active}
      loading={loading}
      playKey={`statement:${playKey}:${String(value || '').length}`}
      collapsedHeight={collapsedHeight}
      skeletonLines={skeletonLines}
      className="animated-statement-reveal"
      onDone={onDone}
    >
      <div className="animated-statement-rich">
        <StatementViewer value={value} />
      </div>
    </SmoothHeightReveal>
  );
}

function SolveSkeletonLines({ lines = 4, className = '' }) {
  return (
    <div className={`solve-skeleton-lines ${className}`} aria-hidden="true">
      {Array.from({ length: lines }).map((_, index) => (
        <div
          key={index}
          className="solve-skeleton-line tf-skeleton"
          style={{ width: `${Math.max(34, 92 - index * 11)}%` }}
        />
      ))}
    </div>
  );
}

function SolvePart({ loading = false, delay = 0, minHeight, className = '', children }) {
  return (
    <div
      className={`solve-part ${loading ? 'solve-part--loading' : 'solve-part--ready'} ${className}`}
      style={{ '--solve-part-delay': `${delay}ms`, minHeight }}
    >
      {children}
    </div>
  );
}


function getPlainStatementEstimate(value) {
  const source = String(value ?? '');
  return source
    .replace(/<[^>]*>/g, ' ')
    .replace(/&nbsp;/g, ' ')
    .replace(/\r/g, '')
    .trim();
}

function estimateStatementReserveHeight(value, compact = false) {
  const text = getPlainStatementEstimate(value);
  const newlineCount = (text.match(/\n/g) || []).length;
  const charsPerLine = compact ? 42 : 82;
  const estimatedLines = Math.max(4, newlineCount + Math.ceil(text.length / charsPerLine));
  const base = compact ? 132 : 150;
  const max = compact ? 620 : 700;
  const min = compact ? 260 : 300;
  return clampNumber(base + estimatedLines * 20, min, max);
}

function SolveActionDock({
  nextTitle = 'Следующее задание',
  nextDisabled = false,
  onNext,
  statusText = '',
  primaryLabel = 'Отправить решение',
  primaryIcon = null,
  primaryDisabled = false,
  onPrimary,
  secondaryActions = [],
}) {
  const visibleStatus = String(statusText || '').trim();
  return (
    <div className="solve-action-dock">
      {visibleStatus && (
        <div className="solve-action-floating-status" aria-live="polite">
          {visibleStatus}
        </div>
      )}
      <div className="solve-action-dock-panel">
        <Button
          className="solve-action-button"
          variant="outline"
          onClick={onNext}
          disabled={nextDisabled}
          title={nextTitle || 'Следующее задание'}
        >
          Следующее задание
        </Button>
        {secondaryActions.map((action, index) => (
          <Button
            key={action.key || index}
            className="solve-action-button"
            variant={action.variant || 'outline'}
            onClick={action.onClick}
            disabled={action.disabled}
            title={action.title || action.label}
          >
            {action.icon || null}
            <span>{action.label}</span>
          </Button>
        ))}
        <Button
          className="solve-action-button solve-action-button--primary"
          onClick={onPrimary}
          disabled={primaryDisabled}
        >
          {primaryIcon}
          <span>{primaryLabel}</span>
        </Button>
      </div>
    </div>
  );
}

function AssignmentFirstLoadSkeleton() {
  return (
    <Layout>
      <div className="solve-page-shell solve-page-shell--loading">
        <div className="flex items-center justify-between mb-6">
          <div className="solve-skeleton-pill w-36" />
          <div className="flex items-center gap-2">
            <div className="solve-skeleton-pill tf-skeleton w-32" />
            <div className="solve-skeleton-pill tf-skeleton w-28" />
          </div>
        </div>
        <div className="grid lg:grid-cols-3 gap-6">
          <Card className="lg:col-span-2 min-h-[420px] tf-skeleton-card">
            <SolveSkeletonLines lines={8} />
          </Card>
          <Card className="min-h-[420px] tf-skeleton-card">
            <SolveSkeletonLines lines={7} />
          </Card>
        </div>
      </div>
      <SolveActionDockSkeleton />
    </Layout>
  );
}

function SolveActionDockSkeleton() {
  return (
    <SolveActionDock
      nextDisabled
      primaryDisabled
      statusText="Загружаю задание"
      primaryLabel="Отправить решение"
    />
  );
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

  const [a, setA] = useState(null);
  const [loading, setLoading] = useState(true);
  const [assignmentSwitching, setAssignmentSwitching] = useState(false);
  const [partLoading, setPartLoading] = useState({ shell: true, statement: true, tests: true });
  const [revealFlow, setRevealFlow] = useState({ key: '', titleDone: false, statementDone: false });

  
  const [nextA, setNextA] = useState(null); 

  const [language, setLanguage] = useState('cpp');
  const [code, setCode] = useState('');

  
  const [codeSolveLayout, setCodeSolveLayout] = useState(
    () => localStorage.getItem('codeSolveLayout') || 'split'
  );

  const [submitting, setSubmitting] = useState(false);
  const [submitPhase, setSubmitPhase] = useState('idle');
  const [error, setError] = useState('');
  const [result, setResult] = useState(null);
  const [checkedDraftKey, setCheckedDraftKey] = useState('');
  const [hydratedAssignmentId, setHydratedAssignmentId] = useState('');

  const activitySessionIdRef = React.useRef('');
  const activityQueueRef = React.useRef([]);
  const activitySendingRef = React.useRef(false);
  const lastCodeActivityRef = React.useRef({ initialized: false, length: 0, at: 0 });
  const lastLanguageActivityRef = React.useRef('');
  const activityStatsRef = React.useRef({ startedAt: Date.now(), hiddenAt: 0, blurAt: 0, hiddenDurationMs: 0, blurDurationMs: 0 });
  const activitySeqRef = React.useRef(0);
  const latestActivityRef = React.useRef({ code: '', language: 'cpp', type: 'code-test' });
  const currentAssignmentIdRef = React.useRef('');

  
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
    latestActivityRef.current = { code, language, type: a?.type || 'code-test' };
  }, [code, language, a?.type]);

  useEffect(() => {
    currentAssignmentIdRef.current = a?.id ? String(a.id) : '';
  }, [a?.id]);

  const flushActivity = React.useCallback((useBeacon = false) => {
    const events = activityQueueRef.current.splice(0, activityQueueRef.current.length);
    if (!assignmentId || events.length === 0) return;
    const payload = {
      sessionId: activitySessionIdRef.current || createActivitySessionId(assignmentId),
      events,
    };
    activitySessionIdRef.current = payload.sessionId;

    if (useBeacon) {
      sendAssignmentActivityBeacon(assignmentId, payload);
      return;
    }

    if (activitySendingRef.current) {
      activityQueueRef.current.unshift(...events);
      return;
    }

    activitySendingRef.current = true;
    recordAssignmentActivityBatch(assignmentId, payload)
      .catch(() => {})
      .finally(() => {
        activitySendingRef.current = false;
      });
  }, [assignmentId]);

  const queueActivity = React.useCallback((eventType, event = {}) => {
    if (!a?.id || !sameAssignmentId(a.id, assignmentId)) return;
    if (!activitySessionIdRef.current) activitySessionIdRef.current = createActivitySessionId(assignmentId);
    const stats = activityStatsRef.current || {};
    const now = Date.now();
    const seq = activitySeqRef.current + 1;
    activitySeqRef.current = seq;
    const latest = latestActivityRef.current || {};
    activityQueueRef.current.push({
      eventType,
      eventUid: `${activitySessionIdRef.current}:${seq}`,
      sequence: seq,
      clientTime: new Date().toISOString(),
      language: event.language ?? latest.language ?? 'cpp',
      activeDurationMs: Math.max(0, now - (stats.startedAt || now) - (stats.hiddenDurationMs || 0) - (stats.blurDurationMs || 0)),
      hiddenDurationMs: Math.max(0, stats.hiddenDurationMs || 0),
      blurDurationMs: Math.max(0, stats.blurDurationMs || 0),
      ...event,
    });

    const important = ['assignment_closed', 'page_unloaded', 'submit_started', 'submit_finished', 'submit_failed', 'paste', 'visibility_hidden', 'window_blur', 'fullscreen_exit'];
    if (activityQueueRef.current.length >= 25 || important.includes(eventType)) {
      flushActivity(false);
    }
  }, [a?.id, assignmentId, flushActivity]);

  useEffect(() => {
    if (!a?.id || !sameAssignmentId(a.id, assignmentId)) return undefined;
    activitySessionIdRef.current = createActivitySessionId(assignmentId);
    activityStatsRef.current = { startedAt: Date.now(), hiddenAt: 0, blurAt: 0, hiddenDurationMs: 0, blurDurationMs: 0 };
    activitySeqRef.current = 0;
    lastCodeActivityRef.current = { initialized: false, length: latestActivityRef.current.code.length, at: Date.now() };
    lastLanguageActivityRef.current = latestActivityRef.current.language;
    queueActivity('assignment_opened', {
      codeLength: latestActivityRef.current.code.length,
      payload: { type: latestActivityRef.current.type || 'code-test', layout: codeSolveLayout },
    });

    const timer = window.setInterval(() => flushActivity(false), 10000);
    return () => {
      window.clearInterval(timer);
      const latest = latestActivityRef.current || { code: '', language: 'cpp', type: 'code-test' };
      const closeNow = Date.now();
      if (activityStatsRef.current.hiddenAt) {
        activityStatsRef.current.hiddenDurationMs += Math.max(0, closeNow - activityStatsRef.current.hiddenAt);
        activityStatsRef.current.hiddenAt = 0;
      }
      if (activityStatsRef.current.blurAt) {
        activityStatsRef.current.blurDurationMs += Math.max(0, closeNow - activityStatsRef.current.blurAt);
        activityStatsRef.current.blurAt = 0;
      }
      const seq = activitySeqRef.current + 1;
      activitySeqRef.current = seq;
      activityQueueRef.current.push({
        eventType: 'assignment_closed',
        eventUid: `${activitySessionIdRef.current}:${seq}`,
        sequence: seq,
        clientTime: new Date().toISOString(),
        language: latest.language,
        codeLength: latest.code.length,
        codeHash: hashActivityText(latest.code),
        activeDurationMs: Math.max(0, Date.now() - (activityStatsRef.current.startedAt || Date.now()) - (activityStatsRef.current.hiddenDurationMs || 0) - (activityStatsRef.current.blurDurationMs || 0)),
        hiddenDurationMs: Math.max(0, activityStatsRef.current.hiddenDurationMs || 0),
        blurDurationMs: Math.max(0, activityStatsRef.current.blurDurationMs || 0),
        payload: { type: latest.type || 'code-test' },
      });
      flushActivity(true);
    };
  }, [a?.id, assignmentId, queueActivity, flushActivity]);

  useEffect(() => {
    if (!a?.id) return undefined;

    const onVisibility = () => {
      const stats = activityStatsRef.current;
      if (document.visibilityState === 'hidden') {
        stats.hiddenAt = Date.now();
      } else if (stats.hiddenAt) {
        stats.hiddenDurationMs += Math.max(0, Date.now() - stats.hiddenAt);
        stats.hiddenAt = 0;
      }
      const latest = latestActivityRef.current;
      queueActivity(document.visibilityState === 'hidden' ? 'visibility_hidden' : 'visibility_visible', {
        codeLength: latest.code.length,
        codeHash: hashActivityText(latest.code),
        language: latest.language,
        payload: { visibilityState: document.visibilityState },
      });
    };
    const onBlur = () => {
      activityStatsRef.current.blurAt = Date.now();
      const latest = latestActivityRef.current;
      queueActivity('window_blur', { codeLength: latest.code.length, codeHash: hashActivityText(latest.code), language: latest.language });
    };
    const onFocus = () => {
      const stats = activityStatsRef.current;
      if (stats.blurAt) {
        stats.blurDurationMs += Math.max(0, Date.now() - stats.blurAt);
        stats.blurAt = 0;
      }
      const latest = latestActivityRef.current;
      queueActivity('window_focus', { codeLength: latest.code.length, codeHash: hashActivityText(latest.code), language: latest.language });
    };
    const onCopy = (event) => {
      const sample = clipboardTextFromEvent(event);
      const latest = latestActivityRef.current;
      queueActivity('copy', { textLength: sample.length, textHash: hashActivityText(sample), textSample: clampActivityText(sample), codeLength: latest.code.length, codeHash: hashActivityText(latest.code), language: latest.language });
    };
    const onCut = (event) => {
      const sample = clipboardTextFromEvent(event);
      const latest = latestActivityRef.current;
      queueActivity('cut', { textLength: sample.length, textHash: hashActivityText(sample), textSample: clampActivityText(sample), codeLength: latest.code.length, codeHash: hashActivityText(latest.code), language: latest.language });
    };
    const onPaste = (event) => {
      const sample = clipboardTextFromEvent(event);
      const latest = latestActivityRef.current;
      queueActivity('paste', { textLength: sample.length, textHash: hashActivityText(sample), textSample: clampActivityText(sample), codeLength: latest.code.length, codeHash: hashActivityText(latest.code), codeSample: clampActivityText(latest.code), language: latest.language });
    };
    const onFullscreen = () => {
      const latest = latestActivityRef.current;
      queueActivity(document.fullscreenElement ? 'fullscreen_enter' : 'fullscreen_exit', { codeLength: latest.code.length, codeHash: hashActivityText(latest.code), language: latest.language });
    };
    const onBeforeUnload = () => {
      const closeNow = Date.now();
      if (activityStatsRef.current.hiddenAt) {
        activityStatsRef.current.hiddenDurationMs += Math.max(0, closeNow - activityStatsRef.current.hiddenAt);
        activityStatsRef.current.hiddenAt = 0;
      }
      if (activityStatsRef.current.blurAt) {
        activityStatsRef.current.blurDurationMs += Math.max(0, closeNow - activityStatsRef.current.blurAt);
        activityStatsRef.current.blurAt = 0;
      }
      const seq = activitySeqRef.current + 1;
      activitySeqRef.current = seq;
      activityQueueRef.current.push({
        eventType: 'page_unloaded',
        eventUid: `${activitySessionIdRef.current}:${seq}`,
        sequence: seq,
        clientTime: new Date().toISOString(),
        language: latestActivityRef.current.language,
        codeLength: latestActivityRef.current.code.length,
        codeHash: hashActivityText(latestActivityRef.current.code),
        activeDurationMs: Math.max(0, Date.now() - (activityStatsRef.current.startedAt || Date.now()) - (activityStatsRef.current.hiddenDurationMs || 0) - (activityStatsRef.current.blurDurationMs || 0)),
        hiddenDurationMs: Math.max(0, activityStatsRef.current.hiddenDurationMs || 0),
        blurDurationMs: Math.max(0, activityStatsRef.current.blurDurationMs || 0),
      });
      flushActivity(true);
    };

    document.addEventListener('visibilitychange', onVisibility);
    window.addEventListener('blur', onBlur);
    window.addEventListener('focus', onFocus);
    document.addEventListener('copy', onCopy);
    document.addEventListener('cut', onCut);
    document.addEventListener('paste', onPaste);
    document.addEventListener('fullscreenchange', onFullscreen);
    window.addEventListener('beforeunload', onBeforeUnload);

    return () => {
      document.removeEventListener('visibilitychange', onVisibility);
      window.removeEventListener('blur', onBlur);
      window.removeEventListener('focus', onFocus);
      document.removeEventListener('copy', onCopy);
      document.removeEventListener('cut', onCut);
      document.removeEventListener('paste', onPaste);
      document.removeEventListener('fullscreenchange', onFullscreen);
      window.removeEventListener('beforeunload', onBeforeUnload);
    };
  }, [a?.id, queueActivity, flushActivity]);

  useEffect(() => {
    if (!a?.id) return;
    const now = Date.now();
    const prev = lastCodeActivityRef.current;
    if (!prev.initialized) {
      lastCodeActivityRef.current = { initialized: true, length: code.length, at: now };
      return;
    }
    const delta = code.length - prev.length;
    if (Math.abs(delta) >= 200 || now - prev.at >= 5000) {
      queueActivity('code_changed_aggregate', {
        codeLength: code.length,
        codeDelta: delta,
        codeHash: hashActivityText(code),
        codeSample: Math.abs(delta) >= 500 ? clampActivityText(code) : undefined,
        payload: { layout: codeSolveLayout },
      });
      lastCodeActivityRef.current = { initialized: true, length: code.length, at: now };
    }
  }, [a?.id, code, codeSolveLayout, queueActivity]);

  useEffect(() => {
    if (!a?.id) return;
    const prev = lastLanguageActivityRef.current;
    if (!prev) {
      lastLanguageActivityRef.current = language;
      return;
    }
    if (prev !== language) {
      queueActivity('language_changed', { payload: { from: prev, to: language }, language });
      lastLanguageActivityRef.current = language;
    }
  }, [a?.id, language, queueActivity]);

  useEffect(() => {
    let alive = true;
    const mergeAssignmentPart = (part) => {
      if (!alive || !part) return;
      setA((prev) => {
        if (!prev || !sameAssignmentId(prev.id, assignmentId)) return prev;
        if (!sameAssignmentId(part.id, assignmentId)) return prev;
        return { ...prev, ...part };
      });
    };

    (async () => {
      const revealDelay = wait(ASSIGNMENT_MIN_REVEAL_MS);
      const firstLoad = !currentAssignmentIdRef.current;
      setLoading(firstLoad);
      setAssignmentSwitching(true);
      setPartLoading({ shell: true, statement: true, tests: true });
      setRevealFlow({ key: String(assignmentId || ''), titleDone: false, statementDone: false });
      setError('');
      setHydratedAssignmentId('');
      setResult(null);
      setCheckedDraftKey('');
      setSubmitPhase('idle');
      setImgError('');
      setImgCompare(null);
      setImageInput('');

      try {
        let shell = null;
        try {
          shell = await getAssignmentSolveShell(assignmentId);
        } catch {
          shell = await getAssignment(assignmentId);
        }
        if (!alive) return;

        const defaultLangFromApi = normalizeLang(shell?.language || shell?.defaultLanguage) || 'cpp';
        const parsedAllowed = parseAllowedLanguages(
          shell?.allowedLanguages ??
          shell?.courseAllowedLanguages ??
          shell?.course?.allowedLanguages
        );
        const effectiveAllowed = (String(shell?.type || '').trim() === 'image-test')
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

        const nextCode = typeof draft?.code === 'string'
          ? draft.code
          : (typeof shell?.starterCode === 'string' ? shell.starterCode : '');

        setLanguage(nextLang);
        setCode(nextCode);
        setA({
          ...shell,
          description: typeof shell?.description === 'string' ? shell.description : '',
          tests: shell?.tests ?? shell?.testCases ?? [],
          testCases: shell?.testCases ?? shell?.tests ?? [],
        });
        setHydratedAssignmentId(String(shell?.id || assignmentId));
        setPartLoading((prev) => ({ ...prev, shell: false }));

        const statementPromise = getAssignmentStatement(assignmentId)
          .then((part) => {
            if (!alive) return;
            mergeAssignmentPart(part);
          })
          .catch(async () => {
            if (!alive) return;
            try {
              const full = await getAssignment(assignmentId);
              mergeAssignmentPart({
                id: full?.id || assignmentId,
                title: full?.title,
                description: full?.description,
                tags: full?.tags,
                difficulty: full?.difficulty,
                rating: full?.rating,
              });
            } catch {}
          })
          .finally(async () => {
            await revealDelay;
            if (alive) setPartLoading((prev) => ({ ...prev, statement: false }));
          });

        const testsPromise = getAssignmentTests(assignmentId)
          .then((part) => {
            if (!alive) return;
            mergeAssignmentPart(part);
          })
          .catch(async () => {
            if (!alive) return;
            try {
              const full = await getAssignment(assignmentId);
              mergeAssignmentPart({
                id: full?.id || assignmentId,
                tests: full?.tests,
                testCases: full?.testCases ?? full?.tests,
                testsJson: full?.testsJson,
                imageTestReferenceKey: full?.imageTestReferenceKey,
                imageTestSimilarityThreshold: full?.imageTestSimilarityThreshold,
              });
            } catch {}
          })
          .finally(async () => {
            await revealDelay;
            if (alive) setPartLoading((prev) => ({ ...prev, tests: false }));
          });

        await Promise.allSettled([statementPromise, testsPromise, revealDelay]);
      } catch (e) {
        const msg = getApiErrorMessage(e, 'Не удалось загрузить задание');
        if (alive) {
          setError(msg);
          notify.error(msg);
          if (!currentAssignmentIdRef.current) setA(null);
        }
      } finally {
        await revealDelay;
        if (alive) {
          setLoading(false);
          setAssignmentSwitching(false);
          setPartLoading((prev) => ({ ...prev, shell: false, statement: false, tests: false }));
        }
      }
    })();
    return () => { alive = false; };
  }, [assignmentId, notify]);

  
  useEffect(() => {
    const onUi = () => setCodeSolveLayout(localStorage.getItem('codeSolveLayout') || 'split');
    window.addEventListener('tf-ui-settings-changed', onUi);
    return () => window.removeEventListener('tf-ui-settings-changed', onUi);
  }, []);

  useEffect(() => {
    if (!a?.id) return;
    if (!sameAssignmentId(a.id, assignmentId)) return;
    if (!sameAssignmentId(hydratedAssignmentId, assignmentId)) return;
    saveSolveDraft(assignmentId, { code, language, updatedAt: new Date().toISOString() });
  }, [a?.id, assignmentId, code, hydratedAssignmentId, language]);

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

  const resetCodeToStarter = React.useCallback(() => {
    const starter = typeof a?.starterCode === 'string' ? a.starterCode : '';
    if (code !== starter && code.trim()) {
      const ok = window.confirm('Заменить текущий код заготовкой задания?');
      if (!ok) return;
    }

    setCode(starter);
    setResult(null);
    setCheckedDraftKey('');
    setSubmitPhase('idle');
    setError('');
    setImgError('');
    setImgCompare(null);
  }, [a?.starterCode, code]);

  const hasStarterCode = typeof a?.starterCode === 'string' && a.starterCode.length > 0;
  const canResetCodeToStarter = hasStarterCode && code !== a.starterCode;

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
    queueActivity('submit_started', {
      codeLength: code.length,
      codeHash: hashActivityText(code),
      codeSample: clampActivityText(code),
      fullCode: code,
      textLength: code.length,
      payload: { type: 'code-test' },
      language,
    });

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
      queueActivity('submit_finished', {
        codeLength: code.length,
        codeHash: hashActivityText(code),
        codeSample: clampActivityText(code),
        fullCode: code,
        textLength: code.length,
        submissionId: solutionId || null,
        payload: { status: statusKey || null, passed: allOk, timedOut: !!timedOut },
        language,
      });
      setCheckedDraftKey(`${language}\n${code}`);
      setResult(nextResult);
      try { localStorage.setItem(`results:${assignmentId}`, JSON.stringify({ result: nextResult })); } catch {}
      setTimeout(() => {
        try {
          const target = document.getElementById('solution-tests-result') || document.getElementById('solution-check-result');
          target?.scrollIntoView({ behavior: 'smooth', block: 'start' });
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
      queueActivity('submit_failed', {
        codeLength: code.length,
        codeHash: hashActivityText(code),
        codeSample: clampActivityText(code),
        fullCode: code,
        textLength: code.length,
        payload: { message: msg },
        language,
      });
      setSubmitPhase('error');
      setError(msg);
      notify.error(msg);
    } finally {
      flushActivity(false);
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

          <div id="solution-tests-result" className="space-y-3 scroll-mt-24">
            <div className="font-semibold">Результаты тестов</div>
            {cases.length > 0 ? cases.map((c, i) => {
              const passed = isCasePassed(c);
              const expectedText = c.expected ?? c.expectedOutput ?? c.ExpectedOutput ?? '';
              const actualText = c.actual ?? c.actualOutput ?? c.ActualOutput ?? '';
              const hasInputText = c.input != null || c.Input != null;
              const inputText = hasInputText ? (c.input ?? c.Input) : '';
              const errorText = c.compileStderr || c.stderr || c.error || '';
              const casePolicy = parsePolicyText(errorText);
              return (
                <div key={`${result?.id || result?.solutionId || checkedDraftKey || 'solution'}-case-${i}`} className={`solve-test-case-reveal rounded-2xl border border-white/10 bg-black/10 p-3 ${isHiddenTestCase(c) ? 'border-amber-300/40 bg-amber-500/5' : ''}`} style={{ '--solve-test-delay': `${Math.min(i, 12) * 75}ms` }}>
                  <div className="flex items-center justify-between gap-3 mb-3">
                    <div className="flex items-center gap-2">
                      <div className="text-sm font-medium">Тест #{i + 1}</div>
                      {isHiddenTestCase(c) && <Badge intent="warning">Скрытый тест</Badge>}
                    </div>
                    <span className={`rounded-full px-2 py-0.5 text-xs ${passed ? 'bg-emerald-500/15 text-emerald-300' : 'bg-rose-500/15 text-rose-300'}`}>
                      {passed ? 'OK' : 'FAIL'}
                    </span>
                  </div>

                  {hasInputText ? (
                    <div className="mb-2">
                      <div className="text-xs text-neutral-500 mb-1">Ввод</div>
                      <InputTextPreview value={inputText} />
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


  const revealKey = String(a?.id || assignmentId || '');
  const sequenceStatementBeforeTests = !!a && a.type !== 'test' && a.type !== 'math';
  const titleAnimationDone = !sequenceStatementBeforeTests || (revealFlow.key === revealKey && revealFlow.titleDone);
  const statementAnimationDone = !sequenceStatementBeforeTests || (revealFlow.key === revealKey && revealFlow.statementDone);
  const testsContentLoading = partLoading.tests || (sequenceStatementBeforeTests && !statementAnimationDone);

  const completeTitleReveal = () => {
    if (!sequenceStatementBeforeTests) return;
    setRevealFlow((prev) => {
      if (prev.key === revealKey && prev.titleDone) return prev;
      return { key: revealKey, titleDone: true, statementDone: false };
    });
  };

  const completeStatementReveal = () => {
    if (!sequenceStatementBeforeTests) return;
    setRevealFlow((prev) => {
      if (prev.key === revealKey && prev.statementDone) return prev;
      return { key: revealKey, titleDone: true, statementDone: true };
    });
  };

  const renderAssignmentStatement = (minLines = 3, compact = false) => (
    <AnimatedStatementViewer
      value={a.description}
      playKey={revealKey}
      active={titleAnimationDone}
      loading={partLoading.statement}
      collapsedHeight={compact ? 72 : 84}
      skeletonLines={Math.max(2, Math.min(minLines, 4))}
      onDone={completeStatementReveal}
    />
  );

  const renderAssignmentLoadHint = () => null;
  const renderAdminQuickInsights = () => null;

  if (loading && !a) {
    return <AssignmentFirstLoadSkeleton />;
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

        {partLoading.tests ? (
          <Card className="min-h-[360px] assignment-reveal">
            <SolveSkeletonLines lines={8} />
          </Card>
        ) : (
          <div className="user-flow-reveal"><TaskTestSolve assignment={a} assignmentId={a.id} onActivity={queueActivity} /></div>
        )}
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

        {partLoading.tests ? (
          <Card className="min-h-[360px] assignment-reveal">
            <SolveSkeletonLines lines={8} />
          </Card>
        ) : (
          <div className="user-flow-reveal"><MathTaskSolve assignment={a} assignmentId={a.id} onActivity={queueActivity} /></div>
        )}
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
      queueActivity('image_trial_started', { codeLength: code.length, codeHash: hashActivityText(code), codeSample: clampActivityText(code), fullCode: code, textLength: code.length, language });

      try {
        const resp = await runImageTestCode(assignmentId, language, code, imageInput);
        const normalized = normalizeImageTaskResult(resp, expectedUrl, {
          isTrial: true,
          code,
          language,
          assignmentTitle: a.title,
        });

        queueActivity('image_trial_finished', {
          codeLength: code.length,
          codeHash: hashActivityText(code),
          codeSample: clampActivityText(code),
          fullCode: code,
          textLength: code.length,
          payload: { passed: !!normalized.passed, similarityPercent: normalized.similarityPercent ?? null },
          language,
        });


        if (hasImageResultPayload(resp) && normalized.actualUrl) {
          setImgCompare(normalized);
        } else {
          const errMsg = buildImageTaskResponseText(resp, 'Не удалось сгенерировать картинку');
          setImgError(errMsg);
        }
      } catch (e) {
        const errMsg = buildImageTaskErrorText(e, 'Не удалось выполнить пробный запуск');
        queueActivity('image_trial_finished', { codeLength: code.length, codeHash: hashActivityText(code), codeSample: clampActivityText(code), fullCode: code, textLength: code.length, payload: { error: errMsg }, language });
        setImgError(errMsg);
      } finally {
        flushActivity(false);
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
      queueActivity('image_submit_started', { codeLength: code.length, codeHash: hashActivityText(code), codeSample: clampActivityText(code), fullCode: code, textLength: code.length, language });

      try {
        const resp = await submitImageTestCode(assignmentId, language, code, imageInput);
        const normalized = normalizeImageTaskResult(resp, expectedUrl, {
          isTrial: false,
          code,
          language,
          assignmentTitle: a.title,
        });

        queueActivity('image_submit_finished', {
          codeLength: code.length,
          codeHash: hashActivityText(code),
          codeSample: clampActivityText(code),
          fullCode: code,
          textLength: code.length,
          submissionId: normalized.solutionId || null,
          payload: { passed: !!normalized.passed, similarityPercent: normalized.similarityPercent ?? null },
          language,
        });

        if (hasImageResultPayload(resp) && normalized.actualUrl) {
          setImgCompare(normalized);
          try { localStorage.setItem(`image-results:${assignmentId}`, JSON.stringify(normalized)); } catch {}
          setTimeout(() => {
            try { document.getElementById('image-test-result')?.scrollIntoView({ behavior: 'smooth', block: 'start' }); } catch {}
          }, 0);

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
        queueActivity('image_submit_finished', { codeLength: code.length, codeHash: hashActivityText(code), codeSample: clampActivityText(code), fullCode: code, textLength: code.length, payload: { error: errMsg }, language });
        setImgError(errMsg);
        notify.error(errMsg);
      } finally {
        flushActivity(false);
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


        <div className="grid lg:grid-cols-3 gap-6">
          
          <div className="lg:col-span-2 space-y-5">
            <Card className="assignment-reveal">
              <SolvePart loading={partLoading.statement} delay={60}>
                <AnimatedHeading text={a.title} playKey={revealKey} onDone={completeTitleReveal} />
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
                {renderAssignmentStatement(5, false)}
              </SolvePart>
            </Card>

            <Card>
              <div className="flex items-center justify-between mb-3">
                <div className="font-medium">Эталон</div>
              </div>

              {testsContentLoading ? (
                <SolveSkeletonLines lines={4} />
              ) : expectedUrl ? (
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
                  <div className="mb-1 flex items-center justify-between gap-3">
                    <label className="label mb-0">Код</label>
                    {hasStarterCode ? (
                      <Button
                        type="button"
                        variant="ghost"
                        className="px-2 py-1 text-xs"
                        onClick={resetCodeToStarter}
                        disabled={!canResetCodeToStarter}
                      >
                        Вернуть заготовку
                      </Button>
                    ) : null}
                  </div>
                  <div className="rounded-xl overflow-hidden border border-neutral-200 dark:border-neutral-700">
                    <CodeEditor
                      key={`image-code-${assignmentId}-${hydratedAssignmentId}-${language}`}
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
                  <Card id="image-test-result" className="p-4 space-y-3 border-emerald-400/30 bg-emerald-500/5 scroll-mt-24">
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

        
        <SolveActionDock
          nextTitle={nextA?.title || 'Следующее задание'}
          nextDisabled={assignmentSwitching || !nextA?.id}
          onNext={goNextAssignment}
          statusText={assignmentSwitching ? 'Загружаю следующее задание' : imgBusy ? 'Идёт обработка изображения' : ''}
          primaryLabel="Отправить решение"
          primaryDisabled={assignmentSwitching || imgBusy || !code.trim() || partLoading.tests || (!expectedUrl && !hasConfiguredImageCases)}
          onPrimary={onSubmitImageTest}
          secondaryActions={[
            {
              key: 'trial-image',
              label: 'Пробник',
              onClick: onTrialImageTest,
              disabled: assignmentSwitching || imgBusy || !code.trim(),
            },
          ]}
        />
      </Layout>
    );
  }

  const canViewHiddenTests = isAdmin || a?.canEdit === true;
  const allAssignmentTests = Array.isArray(a.testCases) ? a.testCases : [];
  const visibleTests = canViewHiddenTests ? allAssignmentTests : allAssignmentTests.filter((t) => !isHiddenTestCase(t));
  const assignmentTestsTitle = canViewHiddenTests ? 'Тесты задания' : 'Публичные тесты';
  const emptyAssignmentTestsText = canViewHiddenTests ? 'У задания нет тестов.' : 'У задания нет публичных тестов.';
  const hasNextAssignmentSlot = Boolean(nextA?.id);

  const renderCodeTestCase = (t, i) => {
    const expectedText = t.expected ?? t.expectedOutput ?? t.ExpectedOutput ?? '';
    return (
      <div
        key={`${a.id || assignmentId}-test-${i}`}
        className={`solve-test-case-reveal rounded border p-3 ${isHiddenTestCase(t) ? 'border-amber-300/60 bg-amber-500/5' : ''}`}
        style={{ '--solve-test-delay': `${Math.min(i, 18) * 78}ms` }}
      >
        <div className="flex items-center justify-between gap-2 mb-1">
          <div className="text-xs text-neutral-500">Ввод</div>
          {isHiddenTestCase(t) && <Badge intent="warning">Скрытый тест</Badge>}
        </div>
        <InputTextPreview value={t.input ?? t.Input ?? ''} />

        {(expectedText ?? '') !== '' && (
          <>
            <div className="text-xs text-neutral-500 mt-2 mb-1">Ожидаемый вывод</div>
            <pre className="whitespace-pre-wrap text-sm">{expectedText}</pre>
          </>
        )}
      </div>
    );
  };

  const renderTestsCard = () => (
    <Card className="solve-tests-card solve-user-reveal-block">
      <div className="flex items-center justify-between mb-3">
        <div className="font-medium">{assignmentTestsTitle}</div>
      </div>

      <SmoothHeightReveal
        active={!testsContentLoading}
        loading={testsContentLoading}
        playKey={`tests:${revealKey}:${visibleTests.length}:${partLoading.tests ? 'loading' : 'ready'}`}
        collapsedHeight={72}
        skeletonLines={4}
        className="solve-tests-height-reveal"
      >
        {visibleTests.length === 0 ? (
          <div className="text-neutral-500">{emptyAssignmentTestsText}</div>
        ) : (
          <div className="solve-test-list space-y-3">
            {visibleTests.map(renderCodeTestCase)}
          </div>
        )}
      </SmoothHeightReveal>
    </Card>
  );

  const submitStatusText = submitMessage;
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


      
      {codeSolveLayout !== 'editorTop' ? (
        <div className="grid lg:grid-cols-3 gap-6">
          
          <div className="lg:col-span-2 space-y-5">
            <Card className="assignment-reveal">
              <SolvePart loading={partLoading.statement} delay={60}>
                <AnimatedHeading text={a.title} playKey={revealKey} onDone={completeTitleReveal} />
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
                {renderAssignmentStatement(5, false)}
              </SolvePart>
            </Card>
            {renderTestsCard()}

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
                  <div className="mb-1 flex items-center justify-between gap-3">
                    <label className="label mb-0">Ваш код</label>
                    {hasStarterCode ? (
                      <Button
                        type="button"
                        variant="ghost"
                        className="px-2 py-1 text-xs"
                        onClick={resetCodeToStarter}
                        disabled={!canResetCodeToStarter}
                      >
                        Вернуть заготовку
                      </Button>
                    ) : null}
                  </div>
                  <CodeEditor
                    key={`code-editor-${assignmentId}-${hydratedAssignmentId}-${language}`}
                    language={language}
                    value={code}
                    onChange={setCode}
                    height={380}
                  />
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
              </div>

              <div>
                <div className="flex items-center justify-between gap-3 mb-2">
                  <TypewriterText as="div" text={a.title} playKey={`${revealKey}:compact-title`} durationMs={clampNumber(String(a.title || '').length * 14, 160, 480)} className="font-semibold text-lg" onDone={completeTitleReveal} />
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

                <div className="mb-1 flex items-center justify-between gap-3">
                  <label className="label mb-0">Ваш код</label>
                  {hasStarterCode ? (
                    <Button
                      type="button"
                      variant="ghost"
                      className="px-2 py-1 text-xs"
                      onClick={resetCodeToStarter}
                      disabled={!canResetCodeToStarter}
                    >
                      Вернуть заготовку
                    </Button>
                  ) : null}
                </div>
                <CodeEditor
                  key={`code-editor-wide-${assignmentId}-${hydratedAssignmentId}-${language}`}
                  language={language}
                  value={code}
                  onChange={setCode}
                  height={460}
                />
              </div>

              {error && <div className="text-sm text-red-600">{error}</div>}
              {renderSubmitState()}
            </div>
          </Card>

          <Card className="assignment-reveal">
            <SolvePart loading={partLoading.statement} delay={90}>
              {renderAssignmentStatement(5, true)}
            </SolvePart>
          </Card>
            {renderTestsCard()}

          {renderSolutionResultCard()}
        </div>
      )}

      
      <SolveActionDock
        nextTitle={nextA?.title || 'Следующее задание'}
        nextDisabled={assignmentSwitching || !hasNextAssignmentSlot}
        onNext={goNextAssignment}
        statusText={assignmentSwitching ? 'Загружаю следующее задание' : submitStatusText}
        primaryLabel="Отправить решение"
        primaryIcon={<Play size={16} className="mr-1" />}
        primaryDisabled={assignmentSwitching || submitting || !code.trim()}
        onPrimary={onSubmit}
      />
    </Layout>
  );
}