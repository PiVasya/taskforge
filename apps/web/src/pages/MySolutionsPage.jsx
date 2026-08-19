import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Card, Button, Badge } from '../components/ui';
import CodeEditor from '../components/CodeEditor';
import { getMySolutions, getMySolutionDetails } from '../api/solutions';
import { getMyTaskTestAttempts, getMyTaskTestAttemptReview } from '../api/taskTestAttempts';
import { getMyImageSolutions, getMyImageSolutionDetails } from '../api/imageSolutions';
import { getMyMathAttempts, getMyMathAttemptReview } from '../api/mathTaskAttempts';
import MathAttemptReview from '../components/math/MathAttemptReview';
import { useNotify } from '../components/notify/NotifyProvider';
import { handleApiError } from '../utils/handleApiError';
import {
  dateMs,
  formatDateTime,
  getImageReferenceUrl,
  getImageSimilarityPercent,
  getImageSubmittedUrl,
  getImageThresholdPercent,
  getSolutionCode,
  getSolutionDate,
  getSolutionAutomationState,
  getSolutionMessage,
  getSolutionOutput,
  getSolutionStatusIntent,
  getSolutionStatusLabel,
  getSolutionTitle,
} from '../utils/solutionUi';
import { useQueryClient } from '../data/QueryClientProvider';

const PAGE_SIZE = 20;
const MY_SOLUTIONS_PAGE_STATE_KEY = ['page-state', 'my-solutions'];
const MY_SOLUTIONS_CACHE_STALE_MS = 60_000;

const FILTER_OPTIONS = [
  { label: 'За всё время', value: null },
  { label: 'За сегодня', value: 1 },
  { label: 'За неделю', value: 7 },
  { label: 'За месяц', value: 30 },
];

function rowId(row) {
  return row?.id ?? row?.Id ?? row?.attemptId ?? row?.AttemptId;
}

function setLoadingFlag(setter, id, value) {
  setter((prev) => ({ ...prev, [id]: value }));
}
function renderOutputBlock(title, value) {
  if (!value) return null;
  return (
    <div>
      <div className="text-xs uppercase tracking-wide text-neutral-500 dark:text-neutral-400 mb-1">{title}</div>
      <pre className="text-xs whitespace-pre-wrap break-words rounded-xl border border-neutral-200 dark:border-neutral-700 bg-neutral-50 dark:bg-neutral-950/40 p-3 max-h-64 overflow-auto">
        {value}
      </pre>
    </div>
  );
}

function SolutionMeta({ solution }) {
  return (
    <div className="flex flex-wrap gap-2 items-center">
      <Badge intent={getSolutionStatusIntent(solution)}>{getSolutionStatusLabel(solution)}</Badge>
    </div>
  );
}

function CodeSolutionDetails({ solution, fallbackLanguage }) {
  if (!solution) return null;
  const code = getSolutionCode(solution);
  const message = getSolutionMessage(solution);
  const stdout = getSolutionOutput(solution, 'stdout');
  const stderr = getSolutionOutput(solution, 'stderr');

  return (
    <div className="mt-3 space-y-3">
      {code ? (
        <div className="rounded-xl overflow-hidden border border-neutral-200 dark:border-neutral-700">
          <CodeEditor
            language={solution.language || fallbackLanguage || 'text'}
            value={code}
            readOnly
            onChange={() => {}}
            height={360}
            automationId={`solution-code-${rowId(solution) || 'details'}`}
            automationRole="solution-code"
            automationState="readonly"
          />
        </div>
      ) : (
        <div className="rounded-xl border border-neutral-200 dark:border-neutral-700 bg-neutral-50 dark:bg-neutral-950/30 p-3 text-sm text-neutral-500 dark:text-neutral-400">
          Код не найден в деталях решения.
        </div>
      )}

      {message ? (
        <div className="rounded-xl border border-neutral-200 dark:border-neutral-700 p-3 text-sm whitespace-pre-wrap">
          {message}
        </div>
      ) : null}

      {(stdout || stderr) ? (
        <div className="grid gap-3 md:grid-cols-2">
          {renderOutputBlock('stdout', stdout)}
          {renderOutputBlock('stderr', stderr)}
        </div>
      ) : null}
    </div>
  );
}

function ImageSolutionDetails({ solution, fallbackLanguage }) {
  if (!solution) return null;
  const code = getSolutionCode(solution);
  const referenceUrl = getImageReferenceUrl(solution);
  const submittedUrl = getImageSubmittedUrl(solution);
  const stdout = getSolutionOutput(solution, 'stdout');
  const stderr = getSolutionOutput(solution, 'stderr');
  const message = getSolutionMessage(solution) || solution.runnerError;

  return (
    <div className="mt-4 space-y-4">
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        <Card className="p-3">
          <div className="text-sm font-medium mb-2">Эталон</div>
          {referenceUrl ? (
            <img src={referenceUrl} alt="Эталон" className="w-full rounded-lg border border-neutral-200 dark:border-neutral-700" />
          ) : (
            <div className="text-sm text-neutral-500 dark:text-neutral-400">Эталон недоступен в истории.</div>
          )}
        </Card>
        <Card className="p-3">
          <div className="text-sm font-medium mb-2">Результат</div>
          {submittedUrl ? (
            <img src={submittedUrl} alt="Результат" className="w-full rounded-lg border border-neutral-200 dark:border-neutral-700" />
          ) : (
            <div className="text-sm text-neutral-500 dark:text-neutral-400">Картинка результата не найдена.</div>
          )}
        </Card>
      </div>

      {code ? (
        <div className="rounded-xl overflow-hidden border border-neutral-200 dark:border-neutral-700">
          <CodeEditor
            language={solution.language || fallbackLanguage || 'text'}
            value={code}
            readOnly
            onChange={() => {}}
            height={320}
            automationId={`image-solution-code-${rowId(solution) || 'details'}`}
            automationRole="solution-code"
            automationState="readonly"
          />
        </div>
      ) : (
        <div className="rounded-xl border border-neutral-200 dark:border-neutral-700 bg-neutral-50 dark:bg-neutral-950/30 p-3 text-sm text-neutral-500 dark:text-neutral-400">
          Код не найден в деталях image-решения.
        </div>
      )}

      {message ? <div className="text-sm text-rose-700 dark:text-rose-300 whitespace-pre-wrap">{message}</div> : null}
      {(stdout || stderr) ? (
        <Card className="p-3 grid gap-3 md:grid-cols-2">
          {renderOutputBlock('stdout', stdout)}
          {renderOutputBlock('stderr', stderr)}
        </Card>
      ) : null}
    </div>
  );
}

export default function MySolutionsPage() {
  const notify = useNotify();
  const queryClient = useQueryClient();
  const cachedStateRef = useRef(queryClient.getQueryData(MY_SOLUTIONS_PAGE_STATE_KEY));
  const cachedState = cachedStateRef.current || {};
  const cachedStateFresh = Number(cachedState.savedAt || 0) > 0
    && Date.now() - Number(cachedState.savedAt) < MY_SOLUTIONS_CACHE_STALE_MS;
  const filterInitializedRef = useRef(false);

  const [tab, setTab] = useState(() => cachedState.tab || 'code');
  const [filterDays, setFilterDays] = useState(() => cachedState.filterDays ?? null);
  const [loadedTabs, setLoadedTabs] = useState(() => cachedStateFresh ? (cachedState.loadedTabs || {}) : {});

  const [solutions, setSolutions] = useState(() => Array.isArray(cachedState.solutions) ? cachedState.solutions : []);
  const [listLoading, setListLoading] = useState(false);
  const [solHasMore, setSolHasMore] = useState(() => cachedState.solHasMore ?? true);
  const [solSkip, setSolSkip] = useState(() => Number(cachedState.solSkip || 0));
  const [details, setDetails] = useState(() => cachedState.details || {});
  const [codeDetailsLoading, setCodeDetailsLoading] = useState({});
  const [expandedId, setExpandedId] = useState(() => cachedState.expandedId ?? null);

  const [testAttempts, setTestAttempts] = useState(() => Array.isArray(cachedState.testAttempts) ? cachedState.testAttempts : []);
  const [testListLoading, setTestListLoading] = useState(false);
  const [testHasMore, setTestHasMore] = useState(() => cachedState.testHasMore ?? true);
  const [testSkip, setTestSkip] = useState(() => Number(cachedState.testSkip || 0));
  const [testDetails, setTestDetails] = useState(() => cachedState.testDetails || {});
  const [expandedTestAttemptId, setExpandedTestAttemptId] = useState(() => cachedState.expandedTestAttemptId ?? null);

  const [imageSolutions, setImageSolutions] = useState(() => Array.isArray(cachedState.imageSolutions) ? cachedState.imageSolutions : []);
  const [imageListLoading, setImageListLoading] = useState(false);
  const [imageHasMore, setImageHasMore] = useState(() => cachedState.imageHasMore ?? true);
  const [imageSkip, setImageSkip] = useState(() => Number(cachedState.imageSkip || 0));
  const [imageDetails, setImageDetails] = useState(() => cachedState.imageDetails || {});
  const [imageDetailsLoading, setImageDetailsLoading] = useState({});
  const [expandedImageId, setExpandedImageId] = useState(() => cachedState.expandedImageId ?? null);

  const [mathAttempts, setMathAttempts] = useState(() => Array.isArray(cachedState.mathAttempts) ? cachedState.mathAttempts : []);
  const [mathListLoading, setMathListLoading] = useState(false);
  const [mathHasMore, setMathHasMore] = useState(() => cachedState.mathHasMore ?? true);
  const [mathSkip, setMathSkip] = useState(() => Number(cachedState.mathSkip || 0));
  const [mathDetails, setMathDetails] = useState(() => cachedState.mathDetails || {});
  const [expandedMathAttemptId, setExpandedMathAttemptId] = useState(() => cachedState.expandedMathAttemptId ?? null);

  useEffect(() => {
    queryClient.setQueryData(MY_SOLUTIONS_PAGE_STATE_KEY, {
      savedAt: Date.now(),
      tab,
      filterDays,
      loadedTabs,
      solutions,
      solHasMore,
      solSkip,
      details,
      expandedId,
      testAttempts,
      testHasMore,
      testSkip,
      testDetails,
      expandedTestAttemptId,
      imageSolutions,
      imageHasMore,
      imageSkip,
      imageDetails,
      expandedImageId,
      mathAttempts,
      mathHasMore,
      mathSkip,
      mathDetails,
      expandedMathAttemptId,
    });
  }, [details, expandedId, expandedImageId, expandedMathAttemptId, expandedTestAttemptId, filterDays, imageDetails, imageHasMore, imageSkip, imageSolutions, loadedTabs, mathAttempts, mathDetails, mathHasMore, mathSkip, queryClient, solHasMore, solSkip, solutions, tab, testAttempts, testDetails, testHasMore, testSkip]);

  const loadSolutions = async ({ reset = false } = {}) => {
    setListLoading(true);
    try {
      const skip = reset ? 0 : solSkip;
      const list = await getMySolutions({ days: filterDays, skip, take: PAGE_SIZE });
      const arr = Array.isArray(list) ? list : [];
      if (reset) {
        setSolutions(arr);
        setSolSkip(arr.length);
        setExpandedId(null);
        setDetails({});
      } else {
        setSolutions((prev) => [...prev, ...arr]);
        setSolSkip((prev) => prev + arr.length);
      }
      setSolHasMore(arr.length === PAGE_SIZE);
    } catch (e) {
      handleApiError(e, notify, 'Не удалось загрузить решения по коду');
    } finally {
      setLoadedTabs((prev) => ({ ...prev, code: true }));
      setListLoading(false);
    }
  };

  const loadTestAttempts = async ({ reset = false } = {}) => {
    setTestListLoading(true);
    try {
      const skip = reset ? 0 : testSkip;
      const list = await getMyTaskTestAttempts({ days: filterDays, skip, take: PAGE_SIZE });
      const arr = Array.isArray(list) ? list : [];
      if (reset) {
        setTestAttempts(arr);
        setTestSkip(arr.length);
        setExpandedTestAttemptId(null);
        setTestDetails({});
      } else {
        setTestAttempts((prev) => [...prev, ...arr]);
        setTestSkip((prev) => prev + arr.length);
      }
      setTestHasMore(arr.length === PAGE_SIZE);
    } catch (e) {
      handleApiError(e, notify, 'Не удалось загрузить попытки тестов');
    } finally {
      setLoadedTabs((prev) => ({ ...prev, tests: true }));
      setTestListLoading(false);
    }
  };

  const loadImageSolutions = async ({ reset = false } = {}) => {
    setImageListLoading(true);
    try {
      const skip = reset ? 0 : imageSkip;
      const list = await getMyImageSolutions({ days: filterDays, skip, take: PAGE_SIZE });
      const arr = Array.isArray(list) ? list : [];
      if (reset) {
        setImageSolutions(arr);
        setImageSkip(arr.length);
        setExpandedImageId(null);
        setImageDetails({});
      } else {
        setImageSolutions((prev) => [...prev, ...arr]);
        setImageSkip((prev) => prev + arr.length);
      }
      setImageHasMore(arr.length === PAGE_SIZE);
    } catch (e) {
      handleApiError(e, notify, 'Не удалось загрузить решения по картинкам');
    } finally {
      setLoadedTabs((prev) => ({ ...prev, images: true }));
      setImageListLoading(false);
    }
  };

  const loadMathAttempts = async ({ reset = false } = {}) => {
    setMathListLoading(true);
    try {
      const skip = reset ? 0 : mathSkip;
      const list = await getMyMathAttempts({ days: filterDays, skip, take: PAGE_SIZE });
      const arr = Array.isArray(list) ? list : [];
      if (reset) {
        setMathAttempts(arr);
        setMathSkip(arr.length);
        setExpandedMathAttemptId(null);
        setMathDetails({});
      } else {
        setMathAttempts((prev) => [...prev, ...arr]);
        setMathSkip((prev) => prev + arr.length);
      }
      setMathHasMore(arr.length === PAGE_SIZE);
    } catch (e) {
      handleApiError(e, notify, 'Не удалось загрузить math-попытки');
    } finally {
      setLoadedTabs((prev) => ({ ...prev, math: true }));
      setMathListLoading(false);
    }
  };

  useEffect(() => {
    if (!filterInitializedRef.current) {
      filterInitializedRef.current = true;
      return;
    }

    setSolSkip(0);
    setSolHasMore(true);
    setSolutions([]);
    setDetails({});
    setExpandedId(null);

    setTestSkip(0);
    setTestHasMore(true);
    setTestAttempts([]);
    setTestDetails({});
    setExpandedTestAttemptId(null);

    setImageSkip(0);
    setImageHasMore(true);
    setImageSolutions([]);
    setImageDetails({});
    setExpandedImageId(null);

    setMathSkip(0);
    setMathHasMore(true);
    setMathAttempts([]);
    setMathDetails({});
    setExpandedMathAttemptId(null);

    setLoadedTabs({});
  }, [filterDays]);

  useEffect(() => {
    if (tab === 'code' && !loadedTabs.code) loadSolutions({ reset: true });
    if (tab === 'tests' && !loadedTabs.tests) loadTestAttempts({ reset: true });
    if (tab === 'images' && !loadedTabs.images) loadImageSolutions({ reset: true });
    if (tab === 'math' && !loadedTabs.math) loadMathAttempts({ reset: true });
  }, [tab, filterDays, loadedTabs.code, loadedTabs.tests, loadedTabs.images, loadedTabs.math]);

  const splitFillPrompt = (prompt) => {
    const p = String(prompt || '');
    const m = p.match(/_{3,}/);
    if (!m) return null;
    const blank = m[0];
    const i = p.indexOf(blank);
    return { before: p.slice(0, i), after: p.slice(i + blank.length), blankLen: blank.length };
  };

  const withAssignmentMeta = useCallback((item) => item, []);

  const displayedSolutions = useMemo(() => {
    const list = solutions.map(withAssignmentMeta);
    list.sort((a, b) => dateMs(getSolutionDate(b)) - dateMs(getSolutionDate(a)));
    return list;
  }, [solutions, withAssignmentMeta]);

  const displayedAttempts = useMemo(() => {
    const list = testAttempts.map(withAssignmentMeta);
    list.sort((a, b) => dateMs(b.submittedAt) - dateMs(a.submittedAt));
    return list;
  }, [testAttempts, withAssignmentMeta]);

  const displayedImageSolutions = useMemo(() => {
    const list = imageSolutions.map(withAssignmentMeta);
    list.sort((a, b) => dateMs(getSolutionDate(b)) - dateMs(getSolutionDate(a)));
    return list;
  }, [imageSolutions, withAssignmentMeta]);

  const displayedMathAttempts = useMemo(() => {
    const list = mathAttempts.map(withAssignmentMeta);
    list.sort((a, b) => dateMs(b.submittedAt) - dateMs(a.submittedAt));
    return list;
  }, [mathAttempts, withAssignmentMeta]);

  const handleToggleCode = async (id) => {
    if (expandedId === id) {
      setExpandedId(null);
      return;
    }

    setExpandedId(id);
    if (!details[id]) {
      setLoadingFlag(setCodeDetailsLoading, id, true);
      try {
        const full = await getMySolutionDetails(id);
        setDetails((prev) => ({ ...prev, [id]: full }));
      } catch (e) {
        handleApiError(e, notify, 'Не удалось загрузить детали решения');
        setExpandedId(null);
      } finally {
        setLoadingFlag(setCodeDetailsLoading, id, false);
      }
    }
  };

  const handleToggleImageSolution = async (id) => {
    if (expandedImageId === id) {
      setExpandedImageId(null);
      return;
    }

    setExpandedImageId(id);
    if (!imageDetails[id]) {
      setLoadingFlag(setImageDetailsLoading, id, true);
      try {
        const full = await getMyImageSolutionDetails(id);
        setImageDetails((prev) => ({ ...prev, [id]: full }));
      } catch (e) {
        handleApiError(e, notify, 'Не удалось загрузить решение по картинке');
        setExpandedImageId(null);
      } finally {
        setLoadingFlag(setImageDetailsLoading, id, false);
      }
    }
  };

  const handleToggleTestAttempt = async (attempt) => {
    const id = attempt.attemptId;
    if (expandedTestAttemptId === id) {
      setExpandedTestAttemptId(null);
      return;
    }
    if (attempt.allowReview === false) {
      notify.warn('Просмотр результатов для этого теста отключён');
      return;
    }
    if (!testDetails[id]) {
      try {
        const dto = await getMyTaskTestAttemptReview(id);
        setTestDetails((p) => ({ ...p, [id]: dto }));
      } catch (e) {
        if (e?.response?.status === 403) notify.warn('Просмотр результатов для этого теста отключён');
        else handleApiError(e, notify, 'Не удалось загрузить просмотр попытки');
        return;
      }
    }
    setExpandedTestAttemptId(id);
  };

  const handleToggleMathAttempt = async (attempt) => {
    const id = attempt.attemptId;
    if (expandedMathAttemptId === id) {
      setExpandedMathAttemptId(null);
      return;
    }
    if (attempt.allowReview === false) {
      notify.warn('Просмотр результатов для этого math-задания отключён');
      return;
    }
    if (!mathDetails[id]) {
      try {
        const dto = await getMyMathAttemptReview(id);
        setMathDetails((prev) => ({ ...prev, [id]: dto }));
      } catch (e) {
        if (e?.response?.status === 403) notify.warn('Просмотр результатов для этого math-задания отключён');
        else handleApiError(e, notify, 'Не удалось загрузить просмотр math-попытки');
        return;
      }
    }
    setExpandedMathAttemptId(id);
  };

  const renderAttemptReview = (dto) => {
    if (!dto) return null;
    const qs = Array.isArray(dto.questions) ? dto.questions : [];

    return (
      <div className="mt-4 space-y-4">
        <div className="flex flex-wrap items-center gap-2 text-sm">
          <Badge intent={dto.passed ? 'success' : 'danger'}>{dto.passed ? 'Зачёт' : 'Не зачтено'}</Badge>
          <Badge intent="secondary">{dto.scorePercent}% • {dto.correctQuestions}/{dto.totalQuestions}</Badge>
          {dto.timeExpired ? <Badge intent="danger">Время вышло</Badge> : null}
        </div>

        <div className="space-y-4">
          {qs.map((q, i) => {
            const type = String(q.type || '').toLowerCase();
            const isCorrect = !!q.isCorrect;
            const user = q.userAnswer || {};
            const split = type === 'fill' ? splitFillPrompt(q.prompt) : null;
            const userText = (user.text || '').toString();
            const selected = new Set(Array.isArray(user.selectedOptionKeys) ? user.selectedOptionKeys : []);
            const correctKeys = new Set(Array.isArray(q.correctOptionKeys) ? q.correctOptionKeys : []);
            const options = Array.isArray(q.options) ? q.options : [];

            return (
              <div key={q.id || i} className="rounded-xl border border-neutral-200 dark:border-neutral-700 p-4 bg-[rgb(var(--card))]">
                <div className="flex items-start justify-between gap-3">
                  <div className="font-medium">
                    {i + 1}.{' '}
                    {split ? (
                      <span className="fill-line">
                        {split.before}
                        <input
                          className="fill-input"
                          value={userText}
                          readOnly
                          style={{ width: `${Math.min(30, Math.max(6, (split.blankLen || 3) * 2))}ch` }}
                        />
                        {split.after}
                      </span>
                    ) : (
                      <span>{q.prompt}</span>
                    )}
                  </div>
                  <Badge intent={isCorrect ? 'success' : 'danger'}>{isCorrect ? 'Верно' : 'Неверно'}</Badge>
                </div>

                {(type === 'single-choice' || type === 'multi-choice') && (
                  <div className="mt-3 space-y-2">
                    {options.map((o) => {
                      const isSel = selected.has(o.key);
                      const isCorr = correctKeys.has(o.key);
                      const cls = [
                        'flex items-center gap-2 text-sm',
                        isCorr ? 'text-emerald-700 dark:text-emerald-300 font-medium' : '',
                        isSel && !isCorr ? 'text-rose-700 dark:text-rose-300' : '',
                      ].filter(Boolean).join(' ');
                      return (
                        <div key={o.key} className={cls}>
                          <input type={type === 'multi-choice' ? 'checkbox' : 'radio'} checked={isSel} readOnly />
                          <span>{o.text}</span>
                          {isCorr ? <span className="text-xs opacity-80">(правильный)</span> : null}
                          {isSel && !isCorr ? <span className="text-xs opacity-80">(ваш выбор)</span> : null}
                        </div>
                      );
                    })}
                  </div>
                )}

                {(type === 'text' || type === 'fill') && !split && (
                  <div className="mt-3 space-y-2 text-sm">
                    <div><span className="text-neutral-500 dark:text-neutral-400">Ваш ответ:</span> {userText || <i>—</i>}</div>
                    {Array.isArray(q.acceptedAnswers) && q.acceptedAnswers.length > 0 ? (
                      <div><span className="text-neutral-500 dark:text-neutral-400">Правильные ответы:</span> {q.acceptedAnswers.join(', ')}</div>
                    ) : null}
                  </div>
                )}
              </div>
            );
          })}
        </div>
      </div>
    );
  };

  return (
    <>
      <div
        className="py-6 space-y-4 min-w-0"
        data-taskforge-automation-id="solution-history"
        data-taskforge-agent-role="solution-history"
        data-taskforge-agent-state={tab}
      >
        <h1 className="text-2xl font-semibold">Мои решения</h1>

        <Card className="p-4 space-y-3">
          <div className="flex flex-wrap items-center gap-2">
            <Button variant={tab === 'code' ? 'primary' : 'outline'} onClick={() => setTab('code')}>Код</Button>
            <Button variant={tab === 'tests' ? 'primary' : 'outline'} onClick={() => setTab('tests')}>Тесты</Button>
            <Button variant={tab === 'images' ? 'primary' : 'outline'} onClick={() => setTab('images')}>Картинки</Button>
            <Button variant={tab === 'math' ? 'primary' : 'outline'} onClick={() => setTab('math')}>Математика</Button>
          </div>

          <div className="flex flex-wrap gap-2 items-center">
            <span className="label">Период:</span>
            {FILTER_OPTIONS.map((opt) => (
              <Button key={opt.label} variant={filterDays === opt.value ? 'primary' : 'outline'} onClick={() => setFilterDays(opt.value)}>
                {opt.label}
              </Button>
            ))}
          </div>

          {tab === 'code' && listLoading ? <div className="text-neutral-500 dark:text-neutral-400">Загрузка…</div> : null}
          {tab === 'tests' && testListLoading ? <div className="text-neutral-500 dark:text-neutral-400">Загрузка…</div> : null}
          {tab === 'images' && imageListLoading ? <div className="text-neutral-500 dark:text-neutral-400">Загрузка…</div> : null}
          {tab === 'math' && mathListLoading ? <div className="text-neutral-500 dark:text-neutral-400">Загрузка…</div> : null}

          {tab === 'code' && !listLoading && !displayedSolutions.length ? <div className="text-neutral-500 dark:text-neutral-400">За выбранный период решений нет.</div> : null}
          {tab === 'tests' && !testListLoading && !displayedAttempts.length ? <div className="text-neutral-500 dark:text-neutral-400">За выбранный период попыток тестов нет.</div> : null}
          {tab === 'images' && !imageListLoading && !displayedImageSolutions.length ? <div className="text-neutral-500 dark:text-neutral-400">За выбранный период решений по картинкам нет.</div> : null}
          {tab === 'math' && !mathListLoading && !displayedMathAttempts.length ? <div className="text-neutral-500 dark:text-neutral-400">За выбранный период math-попыток нет.</div> : null}
        </Card>

        {tab === 'code' && displayedSolutions.length > 0 ? (
          <Card className="p-4 space-y-4">
            <div className="text-sm text-neutral-500 dark:text-neutral-400 mb-2">Показано решений по коду: {displayedSolutions.length}</div>
            <div className="space-y-6">
              {displayedSolutions.map((item, index) => {
                const id = rowId(item);
                const full = details[id] || null;
                const expanded = expandedId === id;
                const loadingDetails = expanded && codeDetailsLoading[id];
                return (
                  <div
                    key={id}
                    className="border border-neutral-200 dark:border-neutral-700 rounded-xl p-4 bg-[rgb(var(--card))] tf-reveal-item"
                    style={{ "--tf-reveal-delay": `${(index % PAGE_SIZE) * 30}ms` }}
                    data-taskforge-automation-id={`solution-history-${id}`}
                    data-taskforge-agent-role="solution-history-item"
                    data-taskforge-agent-state={getSolutionAutomationState(item)}
                    data-taskforge-agent-kind="code"
                  >
                    <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-2 mb-2">
                      <div className="min-w-0">
                        <div className="font-medium">{getSolutionTitle(item)}</div>
                        <div className="text-xs text-neutral-500 dark:text-neutral-400">
                          {formatDateTime(getSolutionDate(item))} • {item.language || 'язык не указан'}
                        </div>
                      </div>
                      <div className="flex gap-2 items-center flex-wrap">
                        <SolutionMeta solution={item} />
                        <Button
                          onClick={() => handleToggleCode(id)}
                          disabled={loadingDetails}
                          data-taskforge-automation-id={`solution-history-${id}-code`}
                          data-taskforge-agent-role="solution-history-action"
                          data-taskforge-agent-action={expanded ? 'hide-code' : 'show-code'}
                        >
                          {expanded ? 'Скрыть код' : 'Показать код'}
                        </Button>
                      </div>
                    </div>
                    {loadingDetails ? <div className="mt-3 text-sm text-neutral-500 dark:text-neutral-400">Загружаю детали решения…</div> : null}
                    {expanded && !loadingDetails ? <CodeSolutionDetails solution={full || item} fallbackLanguage={item.language} /> : null}
                  </div>
                );
              })}
            </div>
            {solHasMore ? (
              <div className="pt-2 flex justify-center">
                <Button variant="outline" onClick={() => loadSolutions({ reset: false })} disabled={listLoading}>{listLoading ? 'Загрузка…' : 'Загрузить ещё'}</Button>
              </div>
            ) : null}
          </Card>
        ) : null}

        {tab === 'tests' && !testListLoading && displayedAttempts.length > 0 ? (
          <Card className="p-4 space-y-4">
            <div className="text-sm text-neutral-500 dark:text-neutral-400 mb-2">Всего попыток тестов: {displayedAttempts.length}</div>
            <div className="space-y-6">
              {displayedAttempts.map((a, index) => {
                const id = a.attemptId;
                const dto = testDetails[id] || null;
                const expanded = expandedTestAttemptId === id;
                return (
                  <div
                    key={id}
                    className="border border-neutral-200 dark:border-neutral-700 rounded-xl p-4 bg-[rgb(var(--card))] tf-reveal-item"
                    style={{ "--tf-reveal-delay": `${(index % PAGE_SIZE) * 30}ms` }}
                    data-taskforge-automation-id={`test-attempt-${id}`}
                    data-taskforge-agent-role="solution-history-item"
                    data-taskforge-agent-state={a.passed ? 'accepted' : 'rejected'}
                    data-taskforge-agent-kind="test"
                  >
                    <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-2">
                      <div>
                        <div className="font-medium">{getSolutionTitle(a)}</div>
                        <div className="text-xs text-neutral-500 dark:text-neutral-400">{formatDateTime(a.submittedAt)} • попытка #{a.attemptNumber}</div>
                      </div>
                      <div className="flex gap-2 items-center">
                        <Badge intent={a.passed ? 'success' : 'danger'}>{a.scorePercent}%</Badge>
                        {a.allowReview === false ? <Badge intent="secondary">Просмотр скрыт</Badge> : null}
                        {a.allowReview !== false ? (
                          <Button
                            variant="primary"
                            onClick={() => handleToggleTestAttempt(a)}
                            data-taskforge-automation-id={`test-attempt-${id}-review`}
                            data-taskforge-agent-role="solution-history-action"
                            data-taskforge-agent-action={expanded ? 'hide-review' : 'show-review'}
                          >
                            {expanded ? 'Скрыть' : 'Просмотреть'}
                          </Button>
                        ) : null}
                      </div>
                    </div>
                    {expanded ? renderAttemptReview(dto) : null}
                  </div>
                );
              })}
            </div>
          </Card>
        ) : null}
        {tab === 'tests' && testHasMore ? (
          <div className="pt-2 flex justify-center">
            <Button variant="outline" onClick={() => loadTestAttempts({ reset: false })} disabled={testListLoading}>{testListLoading ? 'Загрузка…' : 'Загрузить ещё'}</Button>
          </div>
        ) : null}

        {tab === 'images' && !imageListLoading && displayedImageSolutions.length > 0 ? (
          <Card className="p-4 space-y-4">
            <div className="text-sm text-neutral-500 dark:text-neutral-400 mb-2">Всего решений по картинкам: {displayedImageSolutions.length}</div>
            <div className="space-y-6">
              {displayedImageSolutions.map((item, index) => {
                const id = rowId(item);
                const full = imageDetails[id] || null;
                const expanded = expandedImageId === id;
                const loadingDetails = expanded && imageDetailsLoading[id];
                const similarity = getImageSimilarityPercent(item);
                const threshold = getImageThresholdPercent(item);
                const openResult = () => {
                  if (item.assignmentId) window.open(`/assignment/${item.assignmentId}/image-results?solutionId=${id}`, '_blank');
                };
                return (
                  <div
                    key={id}
                    className="border border-neutral-200 dark:border-neutral-700 rounded-xl p-4 bg-[rgb(var(--card))] tf-reveal-item"
                    style={{ "--tf-reveal-delay": `${(index % PAGE_SIZE) * 30}ms` }}
                    data-taskforge-automation-id={`image-solution-${id}`}
                    data-taskforge-agent-role="solution-history-item"
                    data-taskforge-agent-state={item.passed === true ? 'accepted' : item.passed === false ? 'rejected' : 'unknown'}
                    data-taskforge-agent-kind="image"
                  >
                    <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-2">
                      <div className="min-w-0">
                        <div className="font-medium">{getSolutionTitle(item)}</div>
                        <div className="text-xs text-neutral-500 dark:text-neutral-400">
                          {formatDateTime(getSolutionDate(item))} • {item.language || '—'}
                        </div>
                      </div>
                      <div className="flex flex-wrap gap-2 items-center">
                        {item.isTrial ? <Badge intent="secondary">Пробник</Badge> : null}
                        {item.passed === true ? <Badge intent="success">Пройдено</Badge> : null}
                        {item.passed === false ? <Badge intent="danger">Не пройдено</Badge> : null}
                        {typeof similarity === 'number' ? <Badge intent={item.passed ? 'success' : 'danger'}>{Math.round(similarity * 10) / 10}%{typeof threshold === 'number' ? ` / ${Math.round(threshold)}%` : ''}</Badge> : null}
                        {item.assignmentId ? <Button variant="outline" onClick={openResult}>Открыть</Button> : null}
                        <Button
                          onClick={() => handleToggleImageSolution(id)}
                          disabled={loadingDetails}
                          data-taskforge-automation-id={`image-solution-${id}-details`}
                          data-taskforge-agent-role="solution-history-action"
                          data-taskforge-agent-action={expanded ? 'hide-details' : 'show-details'}
                        >
                          {expanded ? 'Скрыть' : 'Подробнее'}
                        </Button>
                      </div>
                    </div>
                    {loadingDetails ? <div className="mt-3 text-sm text-neutral-500 dark:text-neutral-400">Загружаю детали image-решения…</div> : null}
                    {expanded && !loadingDetails ? <ImageSolutionDetails solution={full || item} fallbackLanguage={item.language} /> : null}
                  </div>
                );
              })}
            </div>
          </Card>
        ) : null}
        {tab === 'images' && imageHasMore ? (
          <div className="pt-2 flex justify-center">
            <Button variant="outline" onClick={() => loadImageSolutions({ reset: false })} disabled={imageListLoading}>{imageListLoading ? 'Загрузка…' : 'Загрузить ещё'}</Button>
          </div>
        ) : null}

        {tab === 'math' && !mathListLoading && displayedMathAttempts.length > 0 ? (
          <Card className="p-4 space-y-4">
            <div className="text-sm text-neutral-500 dark:text-neutral-400 mb-2">Всего math-попыток: {displayedMathAttempts.length}</div>
            <div className="space-y-6">
              {displayedMathAttempts.map((a, index) => {
                const id = a.attemptId;
                const dto = mathDetails[id] || null;
                const expanded = expandedMathAttemptId === id;
                return (
                  <div
                    key={id}
                    className="border border-neutral-200 dark:border-neutral-700 rounded-xl p-4 bg-[rgb(var(--card))] tf-reveal-item"
                    style={{ "--tf-reveal-delay": `${(index % PAGE_SIZE) * 30}ms` }}
                    data-taskforge-automation-id={`math-attempt-${id}`}
                    data-taskforge-agent-role="solution-history-item"
                    data-taskforge-agent-state={a.passed ? 'accepted' : 'rejected'}
                    data-taskforge-agent-kind="math"
                  >
                    <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-2">
                      <div>
                        <div className="font-medium">{getSolutionTitle(a)}</div>
                        <div className="text-xs text-neutral-500 dark:text-neutral-400">{formatDateTime(a.submittedAt)} • попытка #{a.attemptNumber}</div>
                      </div>
                      <div className="flex gap-2 items-center flex-wrap">
                        <Badge intent={a.passed ? 'success' : 'danger'}>{a.scorePercent}%</Badge>
                        <Badge intent="secondary">{a.earnedScore}/{a.totalScore}</Badge>
                        {a.allowReview === false ? <Badge intent="secondary">Просмотр скрыт</Badge> : null}
                        {a.allowReview !== false ? (
                          <Button
                            variant="primary"
                            onClick={() => handleToggleMathAttempt(a)}
                            data-taskforge-automation-id={`math-attempt-${id}-review`}
                            data-taskforge-agent-role="solution-history-action"
                            data-taskforge-agent-action={expanded ? 'hide-review' : 'show-review'}
                          >
                            {expanded ? 'Скрыть' : 'Просмотреть'}
                          </Button>
                        ) : null}
                      </div>
                    </div>
                    {expanded ? <MathAttemptReview dto={dto} /> : null}
                  </div>
                );
              })}
            </div>
          </Card>
        ) : null}
        {tab === 'math' && mathHasMore ? (
          <div className="pt-2 flex justify-center">
            <Button variant="outline" onClick={() => loadMathAttempts({ reset: false })} disabled={mathListLoading}>{mathListLoading ? 'Загрузка…' : 'Загрузить ещё'}</Button>
          </div>
        ) : null}
      </div>
    </>
  );
}
