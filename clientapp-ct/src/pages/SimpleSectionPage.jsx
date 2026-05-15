import React, { useEffect, useMemo, useState } from 'react';
import { Link, Navigate, useParams } from 'react-router-dom';
import {
  ArrowLeft,
  CheckCircle2,
  Loader2,
  PlayCircle,
  RefreshCcw,
  RotateCcw,
  XCircle,
} from 'lucide-react';
import Layout from '../components/Layout';
import InlineSectionEditor from '../components/InlineSectionEditor';
import RichConspectRenderer from '../components/RichConspectRenderer';
import { getLearningConspect, getLearningConspects } from '../api/learning';
import {
  getMyQuizProgress,
  getMyQuizSolutions,
  getQuizTask,
  getQuizTasks,
  submitQuizAttempt,
} from '../api/quiz';
import {
  CT_PARTS,
  RANDOM_TASKS_COUNT,
  SUBJECT_CODE,
  EXAM_CODE,
  getSectionPath,
  getSectionsByPart,
  isKnownSectionCode,
  normalizeSectionCode,
} from '../data/ctSections';
import { useEditorMode } from '../contexts/EditorModeContext';

function safeJson(raw, fallback) {
  if (!raw) return fallback;
  try { return typeof raw === 'string' ? JSON.parse(raw) : raw; } catch { return fallback; }
}

function randomAttemptId() {
  if (typeof crypto !== 'undefined' && crypto.randomUUID) return crypto.randomUUID();
  return `${Date.now()}-${Math.random()}`;
}

function answerPayload(answer, hasOptions) {
  return hasOptions ? { selected: [answer] } : { value: answer };
}

function progressMapFrom(progressItems) {
  return new Map((progressItems || []).map((item) => [item.taskId, item]));
}

function taskStatus(task, progressMap) {
  const progress = progressMap.get(task.id);
  if (!progress) return 'new';
  return progress.solved ? 'correct' : 'wrong';
}

function taskWeight(task, progressMap) {
  const status = taskStatus(task, progressMap);
  if (status === 'new') return 9;
  if (status === 'wrong') return 5;
  return 1;
}

function weightedRandomTasks(items, progressMap, count = RANDOM_TASKS_COUNT) {
  return [...(items || [])]
    .map((task) => {
      const weight = taskWeight(task, progressMap);
      const key = -Math.log(Math.random() || 0.000001) / weight;
      return { task, key };
    })
    .sort((a, b) => a.key - b.key)
    .slice(0, count)
    .map((item) => item.task);
}

function explanationText(raw) {
  const data = safeJson(raw, {});
  if (typeof data === 'string') return data;
  if (data.text) return data.text;
  if (data.markdown) return data.markdown;
  if (Array.isArray(data.blocks)) {
    return data.blocks
      .map((block) => block?.text || block?.content || '')
      .filter(Boolean)
      .join('\n');
  }
  return '';
}

function answerToText(raw) {
  const data = safeJson(raw, raw || '');
  if (typeof data === 'string') return data;
  if (Array.isArray(data)) return data.join(', ');
  if (Array.isArray(data.selected)) return data.selected.join(', ');
  if (Array.isArray(data.values)) return data.values.join(', ');
  if (Array.isArray(data.answers)) return data.answers.join(', ');
  if (data.value) return data.value;
  if (data.text) return data.text;
  if (data.typedText) return data.typedText;
  return JSON.stringify(data);
}

function StatusPill({ status }) {
  if (status === 'new') {
    return <span className="rounded-full bg-sky-100 px-2.5 py-1 text-xs font-bold text-sky-800 dark:bg-sky-950/40 dark:text-sky-100">новое</span>;
  }
  if (status === 'wrong') {
    return <span className="rounded-full bg-red-100 px-2.5 py-1 text-xs font-bold text-red-800 dark:bg-red-950/40 dark:text-red-100">нужно повторить</span>;
  }
  return <span className="rounded-full bg-emerald-100 px-2.5 py-1 text-xs font-bold text-emerald-800 dark:bg-emerald-950/40 dark:text-emerald-100">решено</span>;
}

function RandomTaskCard({ task, progressMap, onAnswered }) {
  const [details, setDetails] = useState(null);
  const [answer, setAnswer] = useState('');
  const [result, setResult] = useState(null);
  const [loading, setLoading] = useState(true);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => {
    let cancelled = false;
    async function load() {
      setLoading(true);
      setError('');
      setAnswer('');
      setResult(null);
      try {
        const data = await getQuizTask(task.slug || task.id);
        if (!cancelled) setDetails(data);
      } catch (e) {
        if (!cancelled) setError(e?.userMessage || e?.message || 'Не удалось открыть задание.');
      } finally {
        if (!cancelled) setLoading(false);
      }
    }
    load();
    return () => { cancelled = true; };
  }, [task.id, task.slug]);

  const taskInfo = details?.task || task;
  const status = taskStatus(taskInfo, progressMap);
  const taskData = useMemo(() => safeJson(details?.dataJson, {}), [details]);
  const options = Array.isArray(taskData.options) ? taskData.options : [];
  const answerText = taskData.answerText || taskData.hint || '';
  const resultExplanation = explanationText(result?.explanationJson || details?.explanationJson);

  const handleSubmit = async () => {
    if (!details?.task?.id || !answer.trim()) return;
    setSubmitting(true);
    setError('');
    try {
      const response = await submitQuizAttempt(
        details.task.id,
        answerPayload(answer.trim(), options.length > 0),
        { clientAttemptId: randomAttemptId() },
      );
      setResult(response);
      onAnswered?.(details.task.id, response.progress);
    } catch (e) {
      setError(e?.userMessage || e?.message || 'Не удалось проверить ответ.');
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <article className="rounded-[1.75rem] border border-neutral-200/80 bg-white p-5 shadow-soft dark:border-neutral-800 dark:bg-neutral-900">
      <div className="mb-2 flex flex-wrap items-center gap-2 text-xs font-bold uppercase tracking-wide text-brand-700 dark:text-brand-300">
        <span>{taskInfo.sectionCode || task.sectionCode} · сложность {taskInfo.difficulty || task.difficulty || 1}</span>
        <StatusPill status={status} />
      </div>
      <h3 className="text-xl font-bold">{taskInfo.title}</h3>
      <p className="mt-3 whitespace-pre-line text-lg font-semibold leading-7">{taskInfo.prompt}</p>
      {answerText ? <p className="mt-2 text-sm text-neutral-500 dark:text-neutral-400">{answerText}</p> : null}

      {loading ? (
        <div className="mt-5 flex items-center gap-2 text-sm text-neutral-500 dark:text-neutral-400">
          <Loader2 size={16} className="animate-spin" /> Загружаю задание...
        </div>
      ) : error ? (
        <div className="mt-5 rounded-2xl border border-red-200 bg-red-50 p-3 text-sm text-red-800 dark:border-red-900 dark:bg-red-950/20 dark:text-red-100">
          {error}
        </div>
      ) : (
        <>
          <div className="mt-5 flex flex-wrap gap-2">
            {options.length > 0 ? options.map((option) => (
              <button
                key={option}
                type="button"
                onClick={() => setAnswer(option)}
                className={`rounded-2xl border px-5 py-3 text-lg font-bold transition ${answer === option ? 'border-brand-600 bg-brand-600 text-white' : 'border-neutral-200 bg-neutral-50 hover:border-brand-300 dark:border-neutral-800 dark:bg-neutral-950'}`}
              >
                {option}
              </button>
            )) : (
              <input
                value={answer}
                onChange={(e) => setAnswer(e.target.value)}
                placeholder="Введите краткий ответ"
                className="w-full rounded-2xl border border-neutral-200 bg-neutral-50 px-4 py-3 outline-none focus:border-brand-400 dark:border-neutral-800 dark:bg-neutral-950"
              />
            )}
          </div>

          <div className="mt-5 flex flex-wrap gap-2">
            <button type="button" onClick={handleSubmit} disabled={!answer.trim() || submitting} className="btn-primary inline-flex items-center gap-2 disabled:opacity-60">
              {submitting ? <Loader2 size={18} className="animate-spin" /> : <PlayCircle size={18} />}
              Проверить
            </button>
            <button type="button" onClick={() => { setAnswer(''); setResult(null); }} className="btn-outline inline-flex items-center gap-2">
              <RotateCcw size={18} /> Сбросить
            </button>
          </div>
        </>
      )}

      {result && (
        <div className={`mt-5 rounded-3xl border p-4 ${result.isCorrect ? 'border-emerald-200 bg-emerald-50 dark:border-emerald-900 dark:bg-emerald-950/20' : 'border-red-200 bg-red-50 dark:border-red-900 dark:bg-red-950/20'}`}>
          <div className="flex items-center gap-2 font-semibold">
            {result.isCorrect ? <CheckCircle2 size={20} /> : <XCircle size={20} />}
            {result.isCorrect ? 'Верно' : 'Неверно'} · {result.scorePercent}%
          </div>
          <div className="mt-2 text-sm text-neutral-600 dark:text-neutral-300">
            Твой ответ: <span className="font-bold">{answer}</span>
          </div>
          {resultExplanation ? (
            <div className="mt-3 rounded-2xl bg-white/70 p-3 leading-7 dark:bg-neutral-950/40">
              <div className="mb-1 text-xs font-bold uppercase tracking-wide text-neutral-500">Почему</div>
              <p className="whitespace-pre-line">{resultExplanation}</p>
            </div>
          ) : (
            <p className="mt-3 text-sm text-neutral-600 dark:text-neutral-300">Для этого задания объяснение пока не заполнено.</p>
          )}
        </div>
      )}
    </article>
  );
}

function SolutionsPanel({ sectionCode, refreshKey }) {
  const [items, setItems] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  useEffect(() => {
    let cancelled = false;
    async function load() {
      setLoading(true);
      setError('');
      try {
        const data = await getMyQuizSolutions({ sectionCode });
        if (!cancelled) setItems(data || []);
      } catch (e) {
        if (!cancelled) setError(e?.userMessage || e?.message || 'Не удалось загрузить решения.');
      } finally {
        if (!cancelled) setLoading(false);
      }
    }
    load();
    return () => { cancelled = true; };
  }, [sectionCode, refreshKey]);

  return (
    <div className="mb-5 rounded-[1.75rem] border border-neutral-200 bg-white p-4 shadow-soft dark:border-neutral-800 dark:bg-neutral-900 md:p-5">
      <h3 className="text-2xl font-black tracking-tight">Решения {sectionCode}</h3>
      <p className="mt-1 text-sm text-neutral-500 dark:text-neutral-400">
        Здесь виден последний ответ по каждому заданию. Если ответить на то же задание ещё раз, старый ответ заменится новым.
      </p>

      {loading ? (
        <div className="mt-4 flex items-center gap-2 text-sm text-neutral-500 dark:text-neutral-400">
          <Loader2 size={16} className="animate-spin" /> Загружаю решения...
        </div>
      ) : error ? (
        <div className="mt-4 rounded-2xl border border-red-200 bg-red-50 p-3 text-sm text-red-800 dark:border-red-900 dark:bg-red-950/20 dark:text-red-100">{error}</div>
      ) : items.length === 0 ? (
        <div className="mt-4 rounded-2xl border border-dashed border-neutral-200 p-4 text-sm text-neutral-500 dark:border-neutral-800 dark:text-neutral-400">
          Пока нет решённых или проверенных заданий по {sectionCode}.
        </div>
      ) : (
        <div className="mt-4 grid gap-3">
          {items.map((item) => {
            const why = explanationText(item.explanationJson);
            return (
              <div key={item.attemptId} className="rounded-3xl border border-neutral-200 bg-neutral-50 p-4 dark:border-neutral-800 dark:bg-neutral-950">
                <div className="flex flex-wrap items-start justify-between gap-3">
                  <div>
                    <div className="text-xs font-bold uppercase tracking-wide text-neutral-500 dark:text-neutral-400">{item.task?.sectionCode}</div>
                    <div className="font-bold">{item.task?.title}</div>
                  </div>
                  <span className={`rounded-full px-3 py-1 text-xs font-bold ${item.isCorrect ? 'bg-emerald-100 text-emerald-800 dark:bg-emerald-950/40 dark:text-emerald-100' : 'bg-red-100 text-red-800 dark:bg-red-950/40 dark:text-red-100'}`}>
                    {item.isCorrect ? 'правильно' : 'неправильно'} · {item.scorePercent}%
                  </span>
                </div>
                <p className="mt-3 whitespace-pre-line text-sm leading-6 text-neutral-700 dark:text-neutral-200">{item.task?.prompt}</p>
                <div className="mt-3 rounded-2xl bg-white p-3 text-sm dark:bg-neutral-900">
                  Твой ответ: <span className="font-bold">{answerToText(item.answerJson)}</span>
                </div>
                <div className="mt-3 rounded-2xl bg-white p-3 text-sm leading-6 dark:bg-neutral-900">
                  <div className="mb-1 text-xs font-bold uppercase tracking-wide text-neutral-500">Почему</div>
                  {why ? <p className="whitespace-pre-line">{why}</p> : <p className="text-neutral-500 dark:text-neutral-400">Объяснение для этого задания пока не заполнено.</p>}
                </div>
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}

function RandomTasksBlock({ sectionCode }) {
  const [allTasks, setAllTasks] = useState([]);
  const [visibleTasks, setVisibleTasks] = useState([]);
  const [progress, setProgress] = useState([]);
  const [solutionsOpen, setSolutionsOpen] = useState(false);
  const [solutionsRefreshKey, setSolutionsRefreshKey] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  const progressMap = useMemo(() => progressMapFrom(progress), [progress]);
  const stats = useMemo(() => {
    const map = progressMap;
    return (allTasks || []).reduce((acc, task) => {
      acc[taskStatus(task, map)] += 1;
      return acc;
    }, { new: 0, wrong: 0, correct: 0 });
  }, [allTasks, progressMap]);

  const chooseRandom = (items, map = progressMap) => {
    setVisibleTasks(weightedRandomTasks(items, map));
  };

  useEffect(() => {
    let cancelled = false;
    async function load() {
      setLoading(true);
      setError('');
      try {
        const [tasks, progressItems] = await Promise.all([
          getQuizTasks({ subjectCode: SUBJECT_CODE, examCode: EXAM_CODE, sectionCode }),
          getMyQuizProgress({ sectionCode }).catch(() => []),
        ]);
        if (cancelled) return;
        const normalizedTasks = tasks || [];
        const normalizedProgress = progressItems || [];
        const map = progressMapFrom(normalizedProgress);
        setAllTasks(normalizedTasks);
        setProgress(normalizedProgress);
        setVisibleTasks(weightedRandomTasks(normalizedTasks, map));
      } catch (e) {
        if (!cancelled) setError(e?.userMessage || e?.message || 'Не удалось загрузить задания.');
      } finally {
        if (!cancelled) setLoading(false);
      }
    }
    load();
    return () => { cancelled = true; };
  }, [sectionCode]);

  function handleAnswered(taskId, progressDto) {
    if (progressDto) {
      setProgress((prev) => {
        const withoutCurrent = (prev || []).filter((item) => item.taskId !== taskId);
        return [progressDto, ...withoutCurrent];
      });
    }
    setSolutionsRefreshKey((prev) => prev + 1);
  }

  return (
    <section className="mt-8 rounded-[2rem] border border-neutral-200/80 bg-neutral-50 p-4 dark:border-neutral-800 dark:bg-neutral-950/40 md:p-6">
      <div className="mb-5 flex flex-wrap items-center justify-between gap-3">
        <div>
          <h2 className="text-3xl font-black tracking-tight">Случайные задания</h2>
          <p className="mt-1 text-neutral-600 dark:text-neutral-300">
            Сначала чаще выпадают новые, потом неправильные, потом уже решённые.
          </p>
          <div className="mt-3 flex flex-wrap gap-2 text-xs font-bold text-neutral-600 dark:text-neutral-300">
            <span className="rounded-full bg-sky-100 px-3 py-1 dark:bg-sky-950/40">новые: {stats.new}</span>
            <span className="rounded-full bg-red-100 px-3 py-1 dark:bg-red-950/40">повторить: {stats.wrong}</span>
            <span className="rounded-full bg-emerald-100 px-3 py-1 dark:bg-emerald-950/40">решено: {stats.correct}</span>
          </div>
        </div>
        <div className="flex flex-wrap gap-2">
          <button type="button" onClick={() => setSolutionsOpen((value) => !value)} className="btn-outline inline-flex items-center gap-2">
            <CheckCircle2 size={18} /> Решения
          </button>
          <button type="button" onClick={() => chooseRandom(allTasks)} disabled={allTasks.length === 0 || loading} className="btn-outline inline-flex items-center gap-2 disabled:opacity-60">
            <RefreshCcw size={18} /> Другие
          </button>
        </div>
      </div>

      {solutionsOpen ? <SolutionsPanel sectionCode={sectionCode} refreshKey={solutionsRefreshKey} /> : null}

      {loading ? (
        <div className="rounded-3xl border border-neutral-200 bg-white p-8 text-center shadow-soft dark:border-neutral-800 dark:bg-neutral-900">
          <Loader2 className="mx-auto animate-spin text-brand-600" />
          <div className="mt-3 text-neutral-600 dark:text-neutral-300">Подбираю задания...</div>
        </div>
      ) : error ? (
        <div className="rounded-3xl border border-red-200 bg-red-50 p-4 text-sm text-red-800 dark:border-red-900 dark:bg-red-950/20 dark:text-red-100">{error}</div>
      ) : visibleTasks.length === 0 ? (
        <div className="rounded-3xl border border-neutral-200 bg-white p-6 text-neutral-600 shadow-soft dark:border-neutral-800 dark:bg-neutral-900 dark:text-neutral-300">
          Для {sectionCode} пока нет опубликованных заданий.
        </div>
      ) : (
        <div className="grid gap-4">
          {visibleTasks.map((task) => (
            <RandomTaskCard key={task.id} task={task} progressMap={progressMap} onAnswered={handleAnswered} />
          ))}
        </div>
      )}
    </section>
  );
}

function SectionNav({ active }) {
  return (
    <div className="space-y-3 rounded-[2rem] border border-neutral-200/80 bg-white p-4 shadow-soft dark:border-neutral-800 dark:bg-neutral-900">
      {CT_PARTS.map((part) => (
        <div key={part.code}>
          <div className="mb-2 text-xs font-bold uppercase tracking-wide text-neutral-400">{part.title}</div>
          <div className="flex flex-wrap gap-2">
            {getSectionsByPart(part.code).map((section) => (
              <Link
                key={section.code}
                to={getSectionPath(section.code)}
                className={`rounded-2xl border px-3 py-2 text-sm font-bold transition ${section.code === active ? 'border-brand-500 bg-brand-600 text-white' : 'border-neutral-200 bg-neutral-50 hover:border-brand-300 dark:border-neutral-800 dark:bg-neutral-950'}`}
              >
                {section.code}
              </Link>
            ))}
          </div>
        </div>
      ))}
    </div>
  );
}

export default function SimpleSectionPage({ sectionCode }) {
  const params = useParams();
  const normalizedSectionCode = normalizeSectionCode(sectionCode || params.sectionCode || '');
  const { canEdit, isEditorMode } = useEditorMode();
  const [details, setDetails] = useState(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [reloadKey, setReloadKey] = useState(0);

  useEffect(() => {
    if (!normalizedSectionCode) return;
    let cancelled = false;
    async function load() {
      setLoading(true);
      setError('');
      setDetails(null);
      try {
        const conspects = await getLearningConspects({
          subjectCode: SUBJECT_CODE,
          examCode: EXAM_CODE,
          sectionCode: normalizedSectionCode,
        });
        const first = [...(conspects || [])].sort((a, b) => (a.sortOrder ?? 0) - (b.sortOrder ?? 0))[0];
        if (!first) {
          if (!cancelled) setDetails(null);
          return;
        }
        const data = await getLearningConspect(first.id || first.slug);
        if (!cancelled) setDetails(data);
      } catch (e) {
        if (!cancelled) setError(e?.userMessage || e?.message || 'Не удалось загрузить конспект.');
      } finally {
        if (!cancelled) setLoading(false);
      }
    }
    load();
    return () => { cancelled = true; };
  }, [normalizedSectionCode, reloadKey]);

  if (!normalizedSectionCode || !isKnownSectionCode(normalizedSectionCode)) {
    return <Navigate to="/" replace />;
  }

  return (
    <Layout fullWidth>
      <div className="min-h-[calc(100vh-57px)] bg-neutral-50 dark:bg-neutral-950">
        <div className="mx-auto max-w-6xl px-4 py-5 md:py-8">
          <div className="mb-5 flex flex-wrap items-center justify-between gap-3">
            <Link to="/" className="inline-flex items-center gap-2 text-sm font-semibold text-brand-700 hover:underline dark:text-brand-300">
              <ArrowLeft size={16} /> Все номера
            </Link>
          </div>

          <section className="mb-5 rounded-[2rem] border border-neutral-200/80 bg-white p-6 shadow-soft dark:border-neutral-800 dark:bg-neutral-900 md:p-8">
            <div className="text-sm font-bold uppercase tracking-wide text-brand-700 dark:text-brand-300">ЦТ / ЦЭ</div>
            <h1 className="mt-2 text-5xl font-black tracking-tight">{normalizedSectionCode}</h1>
            <p className="mt-3 max-w-2xl text-lg text-neutral-600 dark:text-neutral-300">
              Сначала HTML-конспект, ниже случайные задания по этому же номеру.
            </p>
          </section>

          <SectionNav active={normalizedSectionCode} />

          {canEdit && isEditorMode ? (
            <InlineSectionEditor
              sectionCode={normalizedSectionCode}
              onConspectSaved={() => setReloadKey((value) => value + 1)}
            />
          ) : null}

          <div className="mt-6">
            {loading ? (
              <div className="rounded-[2rem] border border-neutral-200 bg-white p-10 text-center shadow-soft dark:border-neutral-800 dark:bg-neutral-900">
                <Loader2 className="mx-auto animate-spin text-brand-600" />
                <div className="mt-3 text-neutral-600 dark:text-neutral-300">Загружаю конспект...</div>
              </div>
            ) : error ? (
              <div className="rounded-3xl border border-red-200 bg-red-50 p-4 text-sm text-red-800 dark:border-red-900 dark:bg-red-950/20 dark:text-red-100">{error}</div>
            ) : details ? (
              <RichConspectRenderer details={details} tasksBasePath={getSectionPath(normalizedSectionCode)} />
            ) : (
              <div className="rounded-[2rem] border border-neutral-200 bg-white p-8 text-neutral-600 shadow-soft dark:border-neutral-800 dark:bg-neutral-900 dark:text-neutral-300">
                Для {normalizedSectionCode} пока нет опубликованного HTML-конспекта.
              </div>
            )}
          </div>

          <RandomTasksBlock sectionCode={normalizedSectionCode} />
        </div>
      </div>
    </Layout>
  );
}
