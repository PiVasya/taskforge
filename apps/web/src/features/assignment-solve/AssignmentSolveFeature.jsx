import React, { useEffect, useMemo, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';

import { Card, Button, Badge } from '../../components/ui';
import TaskTestSolve from '../../pages/TaskTestSolve';
import MathTaskSolve from '../../pages/MathTaskSolve';

import { useNotify } from '../../components/notify/NotifyProvider';
import { getAssignment, getAssignmentSolveShell, getAssignmentStatement, getAssignmentTests, getAssignmentsByCourse } from '../../api/assignments';
import { listMySolutions, submitSolution } from '../../api/solutions';
import { runImageTestCode, submitImageTestCode } from '../../api/imageTests';
import { recordAssignmentActivityBatch, sendAssignmentActivityBeacon } from '../../api/assignmentActivity';
import { getApiErrorMessage } from '../../api/http';
import { getLearningCourseMapDelta } from '../../api/courseMaps';

import { Play, CheckCircle2, XCircle } from 'lucide-react';
import { useRoleFlags } from '../../contexts/EditorModeContext';
import { useAuth } from '../../auth/AuthContext';
import { useEditorUiSettings } from '../../contexts/UiSettingsContext';
import SolveDraftEditor from './components/SolveDraftEditor';
import SolveDraftLanguageSelect from './components/SolveDraftLanguageSelect';
import AssignmentTaskConstraints from './components/AssignmentTaskConstraints';
import { getSolveDraftStore, getSolveDraftSnapshot, initializeSolveDraft, releaseSolveDraftStore } from './solveDraftStore';
import { clearCourseMapLocalCache, readCourseMapLocalCache, readCourseMapLocalCacheAsync, writeCourseMapLocalCache } from '../course-assignments/courseMapLocalCache';
import { buildNextNodeOptions, buildSortedFallbackNext, courseMapContainsAssignment } from '../course-assignments/courseMapNextNodes';
import {
  ALL_LANGS,
  normalizeLang,
  parseAllowedLanguages,
  ASSIGNMENT_MIN_REVEAL_MS,
  wait,
  useStableEvent,
  isPendingSolution,
  waitForSolutionVerdict,
  sameAssignmentId,
  createActivitySessionId,
  clampActivityText,
  hashActivityText,
  clipboardTextFromEvent,
  getSolutionCases,
  isCasePassed,
  getSolutionStatusKey,
  getSolutionOutput,
  getResultSummary,
  parsePolicyText,
  getSolutionPolicyUi,
} from './assignmentSolveSupport';
import {
  displayText,
  InputTextPreview,
  clampNumber,
  TypewriterText,
  AnimatedHeading,
  SmoothHeightReveal,
  AnimatedStatementViewer,
  SolveSkeletonLines,
  SolvePart,
  AssignmentSolveHeader,
  NextAssignmentDock,
  SolveDraftActionDock,
  AssignmentFirstLoadSkeleton,
} from './components/AssignmentSolvePresentation';
import {
  displayRunnerText,
  isHiddenTestCase,
  buildImageTaskErrorText,
  imageReferenceUrlFromAssignment,
  imageTestCasesFromAssignment,
  buildImageTaskResponseText,
  hasImageResultPayload,
  normalizeImageTaskResult,
} from './imageTaskModel';
async function recoverRecentCodeSubmission(assignmentId, language, code, submitStartedAt) {
  const attempts = [0, 300, 900];
  for (const delayMs of attempts) {
    if (delayMs > 0) await wait(delayMs);
    try {
      const rows = await listMySolutions(assignmentId);
      const match = (Array.isArray(rows) ? rows : []).find((row) => {
        const rowCode = String(row?.code ?? row?.submittedCode ?? '');
        const rowLanguage = normalizeLang(row?.language);
        const createdAt = Date.parse(row?.createdAtUtc || row?.submittedAt || row?.createdAt || '');
        const isRecent = !Number.isFinite(createdAt) || createdAt >= submitStartedAt - 5000;
        return rowCode === code && rowLanguage === normalizeLang(language) && isRecent;
      });
      if (match) return match;
    } catch {
      // Best-effort reconciliation after a transport/proxy failure. The original
      // submit error remains authoritative when no matching server-side row exists.
    }
  }
  return null;
}

export default function AssignmentSolvePage() {
  const { assignmentId } = useParams();
  const nav = useNavigate();

  const notify = useNotify();
  const { isAdmin } = useRoleFlags();
  const { user } = useAuth();
  const { codeSolveLayout } = useEditorUiSettings();

  const [a, setA] = useState(null);
  const [loading, setLoading] = useState(true);
  const [assignmentSwitching, setAssignmentSwitching] = useState(false);
  const [partLoading, setPartLoading] = useState({ shell: true, statement: true, tests: true });
  const [revealFlow, setRevealFlow] = useState({ key: '', titleDone: false, statementDone: false });

  
  const [nextOptions, setNextOptions] = useState([]);
  const [nextNavigationLoading, setNextNavigationLoading] = useState(false);
  const [progressionRevision, setProgressionRevision] = useState(0);

  const draftStore = useMemo(() => getSolveDraftStore(assignmentId), [assignmentId]);
  const readDraft = React.useCallback(() => getSolveDraftSnapshot(assignmentId), [assignmentId]);

  const [submitting, setSubmitting] = useState(false);
  const [submitPhase, setSubmitPhase] = useState('idle');
  const [error, setError] = useState('');
  const [result, setResult] = useState(null);
  const [checkedDraftKey, setCheckedDraftKey] = useState('');

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
    if (!draftStore) return undefined;
    const syncDraftActivity = () => {
      const draft = draftStore.getSnapshot();
      latestActivityRef.current = { code: draft.code, language: draft.language, type: a?.type || 'code-test' };
    };
    syncDraftActivity();
    return draftStore.subscribe(syncDraftActivity);
  }, [a?.type, draftStore]);

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
    if (!a?.id || !draftStore) return undefined;
    let previous = draftStore.getSnapshot();
    latestActivityRef.current = { code: previous.code, language: previous.language, type: a?.type || 'code-test' };
    lastCodeActivityRef.current = { initialized: true, length: previous.code.length, at: Date.now() };
    lastLanguageActivityRef.current = previous.language;

    return draftStore.subscribe(() => {
      const draft = draftStore.getSnapshot();
      const now = Date.now();
      latestActivityRef.current = { code: draft.code, language: draft.language, type: a?.type || 'code-test' };

      if (draft.code !== previous.code) {
        const last = lastCodeActivityRef.current;
        const delta = draft.code.length - last.length;
        if (Math.abs(delta) >= 200 || now - last.at >= 5000) {
          queueActivity('code_changed_aggregate', {
            codeLength: draft.code.length,
            codeDelta: delta,
            codeHash: hashActivityText(draft.code),
            codeSample: Math.abs(delta) >= 500 ? clampActivityText(draft.code) : undefined,
            payload: { layout: codeSolveLayout },
          });
          lastCodeActivityRef.current = { initialized: true, length: draft.code.length, at: now };
        }
        if (result && checkedDraftKey && `${draft.language}\n${draft.code}` !== checkedDraftKey) {
          setResult(null);
          setCheckedDraftKey('');
        }
      }

      if (draft.language !== previous.language) {
        queueActivity('language_changed', { payload: { from: previous.language, to: draft.language }, language: draft.language });
        lastLanguageActivityRef.current = draft.language;
      }
      previous = draft;
    });
  }, [a?.id, a?.type, checkedDraftKey, codeSolveLayout, draftStore, queueActivity, result]);

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

        const initializedDraft = initializeSolveDraft(assignmentId, {
          code: typeof shell?.starterCode === 'string' ? shell.starterCode : '',
          language: nextLang,
        });
        const persistedLang = normalizeLang(initializedDraft.language);
        if (effectiveAllowed.length > 0 && !effectiveAllowed.includes(persistedLang)) {
          draftStore?.setLanguage(effectiveAllowed[0]);
        }
        setA({
          ...shell,
          description: typeof shell?.description === 'string' ? shell.description : '',
          tests: shell?.tests ?? shell?.testCases ?? [],
          testCases: shell?.testCases ?? shell?.tests ?? [],
        });
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
  }, [assignmentId, draftStore, notify]);

  useEffect(() => () => releaseSolveDraftStore(assignmentId), [assignmentId]);

  
  
  useEffect(() => {
    let alive = true;
    if (!a?.courseId || !a?.id) {
      setNextOptions([]);
      setNextNavigationLoading(false);
      return () => { alive = false; };
    }

    const currentUserId = String(user?.id || user?.userId || user?.uuid || '');
    const applyNavigation = (mapRecord, rows) => {
      if (!alive) return;
      const assignments = Array.isArray(rows) ? rows : [];
      if (mapRecord?.document) {
        setNextOptions(courseMapContainsAssignment(mapRecord.document, a.id)
          ? buildNextNodeOptions(mapRecord.document, a.id, assignments)
          : []);
        return;
      }
      setNextOptions(buildSortedFallbackNext(assignments, a.id));
    };

    let cached = readCourseMapLocalCache({ courseId: a.courseId, editorMode: false, userId: currentUserId });
    if (cached?.mapRecord) applyNavigation(cached.mapRecord, cached.assignments);
    setNextNavigationLoading(Boolean(progressionRevision) && Boolean(cached?.mapRecord?.projectionToken));

    (async () => {
      if (!cached) {
        cached = await readCourseMapLocalCacheAsync({ courseId: a.courseId, editorMode: false, userId: currentUserId });
        if (!alive) return;
        if (cached?.mapRecord) applyNavigation(cached.mapRecord, cached.assignments);
      }
      if (progressionRevision && cached?.mapRecord?.projectionToken) setNextNavigationLoading(true);
      if (!cached?.mapRecord?.projectionToken) {
        if (!cached?.mapRecord) {
          try {
            const rows = await getAssignmentsByCourse(a.courseId);
            if (alive) applyNavigation(null, rows);
          } catch {
            if (alive) setNextOptions([]);
          }
        }
        return;
      }

      try {
        const projectionCourseId = cached.mapRecord.requestedCourseId || cached.requestedCourseId || a.courseId;
        const delta = await getLearningCourseMapDelta(
          projectionCourseId,
          cached.mapRecord.projectionToken,
          progressionRevision ? a.id : null,
        );
        if (!alive || !delta) return;
        if (delta.resetRequired) {
          clearCourseMapLocalCache({ courseId: projectionCourseId, editorMode: false, userId: currentUserId });
          setNextOptions([]);
          return;
        }

        const currentDocument = cached.mapRecord.document || { nodes: [], edges: [], viewport: { x: 0, y: 0, zoom: 1 } };
        const removeNodeIds = new Set((delta.nodeIdsRemoved || []).map(String));
        const removeEdgeIds = new Set((delta.edgeIdsRemoved || []).map(String));
        const nodeById = new Map((currentDocument.nodes || [])
          .filter((node) => !removeNodeIds.has(String(node?.id)))
          .map((node) => [String(node.id), node]));
        for (const node of delta.nodesAdded || []) {
          if (node?.id) nodeById.set(String(node.id), node);
        }
        const edgeById = new Map((currentDocument.edges || [])
          .filter((edge) => !removeEdgeIds.has(String(edge?.id)) && !removeNodeIds.has(String(edge?.source)) && !removeNodeIds.has(String(edge?.target)))
          .map((edge) => [String(edge.id), edge]));
        for (const edge of delta.edgesAdded || []) {
          if (edge?.id) edgeById.set(String(edge.id), edge);
        }

        const assignmentById = new Map((cached.assignments || []).map((item) => [String(item?.id || ''), item]));
        for (const item of delta.assignmentsChanged || []) {
          if (!item?.id) continue;
          const previous = assignmentById.get(String(item.id)) || {};
          assignmentById.set(String(item.id), {
            ...previous,
            ...item,
            isSolved: item.solvedByCurrentUser === true,
            progressStatus: item.solvedByCurrentUser === true ? 'solved' : 'not-started',
          });
        }

        const courseProgress = { ...(currentDocument.courseProgress || {}), ...(delta.courseProgress || {}) };
        const mapRecord = {
          ...cached.mapRecord,
          version: Number(delta.version || cached.mapRecord.version || 0),
          projectionToken: delta.projectionToken || cached.mapRecord.projectionToken,
          projectionRevision: Number(delta.projectionRevision || cached.mapRecord.projectionRevision || 0),
          document: {
            ...currentDocument,
            courseProgressVersion: 1,
            courseProgress,
            nodes: Array.from(nodeById.values()),
            edges: Array.from(edgeById.values()),
          },
        };
        const assignments = Array.from(assignmentById.values());
        const courseById = new Map((cached.courses || []).map((item) => [String(item?.id || ''), item]));
        for (const item of delta.coursesChanged || []) {
          if (!item?.id) continue;
          const previous = courseById.get(String(item.id)) || {};
          courseById.set(String(item.id), { ...previous, ...item });
        }
        const courses = Array.from(courseById.values());
        writeCourseMapLocalCache({
          courseId: projectionCourseId,
          rootCourseId: mapRecord.rootCourseId || cached.rootCourseId || projectionCourseId,
          aliases: cached.aliases || [a.courseId],
          editorMode: false,
          userId: currentUserId,
          mapRecord,
          assignments,
          courses,
          pendingRevealNodeIds: (delta.nodesAdded || []).map((node) => String(node?.id || '')).filter(Boolean),
        });
        applyNavigation(mapRecord, assignments);
      } catch {
        if (!cached?.mapRecord && alive) setNextOptions([]);
      } finally {
        if (alive) setNextNavigationLoading(false);
      }
    })();

    return () => { alive = false; };
  }, [a?.courseId, a?.id, progressionRevision, user?.id, user?.userId, user?.uuid]);

  const refreshProgression = React.useCallback(() => {
    setProgressionRevision((value) => value + 1);
  }, []);

  const resetSolveUi = React.useCallback(() => {
    setResult(null);
    setCheckedDraftKey('');
    setSubmitPhase('idle');
    setError('');
    setImgError('');
    setImgCompare(null);
  }, []);

  const goNextAssignment = React.useCallback((option = null) => {
    const target = option?.id ? option : nextOptions.find((item) => item?.id && !item.disabled);
    if (!target?.id) return;
    nav(`/assignment/${target.id}`);
  }, [nav, nextOptions]);

  
  
  useEffect(() => {
    if (!allowedLangs || allowedLangs.length === 0 || !draftStore) return;
    const currentLanguage = draftStore.getSnapshot().language;
    if (!allowedLangs.includes(currentLanguage)) draftStore.setLanguage(allowedLangs[0]);
  }, [allowedLangs, draftStore]);

  const onSubmit = async () => {
    const { code, language } = readDraft();
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
      const submitStartedAt = Date.now();
      let r;
      try {
        r = await submitSolution(assignmentId, { language, code });
      } catch (submitError) {
        const status = Number(submitError?.response?.status || 0);
        if (submitError?.response && status < 500) throw submitError;
        r = await recoverRecentCodeSubmission(assignmentId, language, code, submitStartedAt);
        if (!r) throw submitError;
        notify.info('Связь с ответом прервалась, но решение уже принято сервером. Результат восстановлен.');
      }
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
        refreshProgression();
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
  const submitCodeSolution = useStableEvent(onSubmit);

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
    return (
      <div
        className={`rounded-xl border px-3 py-2 text-sm ${cls}`}
        data-taskforge-automation-id="solution-submit-state"
        data-taskforge-agent-role="solution-status"
        data-taskforge-agent-state={submitPhase === 'final' ? (result?.__allPassed ? 'accepted' : 'rejected') : submitPhase}
      >
        {text}
      </div>
    );
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
      <Card
        id="solution-check-result"
        className={`scroll-mt-24 ${toneClass}`}
        data-taskforge-automation-id="solution-status"
        data-taskforge-agent-role="solution-status"
        data-taskforge-agent-state={pending ? "running" : result?.__allPassed ? "accepted" : "rejected"}
      >
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
            ) : null}
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

  if (loading && !a) {
    return <AssignmentFirstLoadSkeleton />;
  }
  if (!a) {
    return (
      <>
        <div className="text-red-600">{error || 'Задание не найдено'}</div>
      </>
    );
  }

  
  if (a.type === 'test') {
    return (
      <>
        
        <AssignmentSolveHeader
          courseId={a.courseId}
          assignmentId={a.id}
          isAdmin={isAdmin}
          showQuota
        />

        {partLoading.tests ? (
          <Card className="min-h-[360px] assignment-reveal">
            <SolveSkeletonLines lines={8} />
          </Card>
        ) : (
          <div className="user-flow-reveal"><TaskTestSolve assignment={a} assignmentId={a.id} onActivity={queueActivity} onCompleted={refreshProgression} /></div>
        )}
        <NextAssignmentDock nextOptions={nextOptions} nextLoading={nextNavigationLoading} nextDisabled={assignmentSwitching} onNext={goNextAssignment} />
      </>
    );
  }

  
  if (a.type === 'math') {
    return (
      <>
        <AssignmentSolveHeader
          courseId={a.courseId}
          assignmentId={a.id}
          isAdmin={isAdmin}
          showQuota
        />

        {partLoading.tests ? (
          <Card className="min-h-[360px] assignment-reveal">
            <SolveSkeletonLines lines={8} />
          </Card>
        ) : (
          <div className="user-flow-reveal"><MathTaskSolve assignment={a} assignmentId={a.id} onActivity={queueActivity} onCompleted={refreshProgression} /></div>
        )}
        <NextAssignmentDock nextOptions={nextOptions} nextLoading={nextNavigationLoading} nextDisabled={assignmentSwitching} onNext={goNextAssignment} />
      </>
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
      const { code, language } = readDraft();
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
      const { code, language } = readDraft();
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
            refreshProgression();
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
      <>
        
        <AssignmentSolveHeader
          courseId={a.courseId}
          assignmentId={a.id}
          isAdmin={isAdmin}
          showQuota
        />

        <div className="grid lg:grid-cols-3 gap-6">
          
          <div className="lg:col-span-2 space-y-5">
            <Card className="assignment-reveal" data-taskforge-automation-id="assignment-statement" data-taskforge-agent-role="assignment-statement" data-taskforge-agent-kind={a?.type || "code-test"}>
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
                <AssignmentTaskConstraints constraints={a?.taskConstraints} />
              </SolvePart>
            </Card>

            {(expectedUrl || isAdmin || a?.canEdit === true) ? (
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
                ) : (
                  <div className="text-neutral-500">Эталон недоступен.</div>
                )}
              </Card>
            ) : null}

            {renderSolutionResultCard()}
          </div>

          
          <div className="space-y-4">
            <Card>
              <div className="grid gap-3">
                <SolveDraftLanguageSelect
                  assignmentId={a.id}
                  languages={imageLangs}
                  allowedLangs={allowedLangs}
                />
                <SolveDraftEditor
                  assignmentId={a.id}
                  starterCode={a.starterCode}
                  langsForSelect={imageLangs}
                  allowedLangs={allowedLangs}
                  height={380}
                  showLanguage={false}
                  showAllowedHint={false}
                  onReset={resetSolveUi}
                />

                <div>
                  <label className="label">Входные данные для программы</label>
                  <textarea
                    value={imageInput}
                    onChange={(e) => setImageInput(e.target.value)}
                    rows={4}
                    placeholder="Введите входные данные"
                    className="w-full rounded-xl border border-neutral-200 dark:border-neutral-700 bg-white dark:bg-neutral-900 px-3 py-2 text-sm"
                  />
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

              </div>
            </Card>
          </div>
        </div>

        
        <SolveDraftActionDock
          assignmentId={a.id}
          disableSecondaryWhenEmpty
          nextOptions={nextOptions}
          nextLoading={nextNavigationLoading}
          nextDisabled={assignmentSwitching}
          onNext={goNextAssignment}
          statusText={assignmentSwitching ? 'Загружаю следующее задание' : imgBusy ? 'Идёт обработка изображения' : ''}
          primaryLabel="Отправить решение"
          primaryDisabled={assignmentSwitching || imgBusy || partLoading.tests || (!expectedUrl && !hasConfiguredImageCases)}
          onPrimary={onSubmitImageTest}
          secondaryActions={[
            {
              key: 'trial-image',
              label: 'Пробник',
              onClick: onTrialImageTest,
              disabled: assignmentSwitching || imgBusy,
            },
          ]}
        />
      </>
    );
  }

  const canViewHiddenTests = isAdmin || a?.canEdit === true;
  const allAssignmentTests = Array.isArray(a.testCases) ? a.testCases : [];
  const visibleTests = canViewHiddenTests ? allAssignmentTests : allAssignmentTests.filter((t) => !isHiddenTestCase(t));
  const assignmentTestsTitle = canViewHiddenTests ? 'Тесты задания' : 'Публичные тесты';
  const emptyAssignmentTestsText = canViewHiddenTests ? 'У задания нет тестов.' : 'У задания нет публичных тестов.';

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
    <Card className="solve-tests-card solve-user-reveal-block" data-taskforge-automation-id="assignment-tests" data-taskforge-agent-role="assignment-tests" data-taskforge-agent-kind={a?.type || "code-test"}>
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
    <>
      
      <AssignmentSolveHeader
        courseId={a.courseId}
        assignmentId={a.id}
        isAdmin={isAdmin}
      />

      
      {codeSolveLayout !== 'editorTop' ? (
        <div className="grid lg:grid-cols-3 gap-6">
          
          <div className="lg:col-span-2 space-y-5">
            <Card className="assignment-reveal" data-taskforge-automation-id="assignment-statement" data-taskforge-agent-role="assignment-statement" data-taskforge-agent-kind={a?.type || "code-test"}>
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
                <AssignmentTaskConstraints constraints={a?.taskConstraints} />
              </SolvePart>
            </Card>
            {renderTestsCard()}

            {renderSolutionResultCard()}
          </div>

          
          <div className="space-y-4">
            <Card>
              <div className="grid gap-3">
                <SolveDraftEditor
                  assignmentId={a.id}
                  starterCode={a.starterCode}
                  langsForSelect={langsForSelect}
                  allowedLangs={allowedLangs}
                  height={380}
                  onReset={resetSolveUi}
                />

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

                <SolveDraftEditor
                  assignmentId={a.id}
                  starterCode={a.starterCode}
                  langsForSelect={langsForSelect}
                  allowedLangs={allowedLangs}
                  height={460}
                  onReset={resetSolveUi}
                />
              </div>

              {error && <div className="text-sm text-red-600">{error}</div>}
              {renderSubmitState()}
            </div>
          </Card>

          <Card className="assignment-reveal" data-taskforge-automation-id="assignment-statement" data-taskforge-agent-role="assignment-statement" data-taskforge-agent-kind={a?.type || "code-test"}>
            <SolvePart loading={partLoading.statement} delay={90}>
              {renderAssignmentStatement(5, true)}
              <AssignmentTaskConstraints constraints={a?.taskConstraints} />
            </SolvePart>
          </Card>
            {renderTestsCard()}

          {renderSolutionResultCard()}
        </div>
      )}

      
      <SolveDraftActionDock
        assignmentId={a.id}
        nextOptions={nextOptions}
        nextLoading={nextNavigationLoading}
        nextDisabled={assignmentSwitching}
        onNext={goNextAssignment}
        statusText={assignmentSwitching ? 'Загружаю следующее задание' : submitStatusText}
        primaryLabel="Отправить решение"
        primaryIcon={Play}
        primaryDisabled={assignmentSwitching || submitting}
        onPrimary={submitCodeSolution}
      />
    </>
  );
}
