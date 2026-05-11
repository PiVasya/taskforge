import React, { useEffect, useMemo, useState } from 'react';
import { Link, Navigate, useParams } from 'react-router-dom';
import { ArrowLeft, CheckCircle2, Loader2, PlayCircle, RefreshCcw, RotateCcw, XCircle } from 'lucide-react';
import Layout from '../components/Layout';
import RichConspectRenderer from '../components/RichConspectRenderer';
import { getLearningConspect, getLearningConspects } from '../api/learning';
import { getQuizTask, getQuizTasks, submitQuizAttempt } from '../api/quiz';
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

function safeJson(raw, fallback) {
  if (!raw) return fallback;
  try { return typeof raw === 'string' ? JSON.parse(raw) : raw; } catch { return fallback; }
}

function shuffle(items) {
  const result = [...(items || [])];
  for (let i = result.length - 1; i > 0; i -= 1) {
    const j = Math.floor(Math.random() * (i + 1));
    [result[i], result[j]] = [result[j], result[i]];
  }
  return result;
}

function randomAttemptId() {
  if (typeof crypto !== 'undefined' && crypto.randomUUID) return crypto.randomUUID();
  return `${Date.now()}-${Math.random()}`;
}

function answerPayload(answer, hasOptions) {
  return hasOptions ? { selected: [answer] } : { value: answer };
}

function RandomTaskCard({ task }) {
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
  const taskData = useMemo(() => safeJson(details?.dataJson, {}), [details]);
  const explanation = useMemo(() => safeJson(result?.explanationJson || details?.explanationJson, {}), [result, details]);
  const options = Array.isArray(taskData.options) ? taskData.options : [];
  const answerText = taskData.answerText || taskData.hint || '';

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
    } catch (e) {
      setError(e?.userMessage || e?.message || 'Не удалось проверить ответ.');
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <article className="rounded-[1.75rem] border border-neutral-200/80 bg-white p-5 shadow-soft dark:border-neutral-800 dark:bg-neutral-900">
      <div className="mb-2 text-xs font-bold uppercase tracking-wide text-brand-700 dark:text-brand-300">
        {taskInfo.sectionCode || task.sectionCode} · сложность {taskInfo.difficulty || task.difficulty || 1}
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
          {explanation.text && <p className="mt-2 leading-7">{explanation.text}</p>}
        </div>
      )}
    </article>
  );
}

function RandomTasksBlock({ sectionCode }) {
  const [allTasks, setAllTasks] = useState([]);
  const [visibleTasks, setVisibleTasks] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  const chooseRandom = (items) => setVisibleTasks(shuffle(items).slice(0, RANDOM_TASKS_COUNT));

  useEffect(() => {
    let cancelled = false;
    async function load() {
      setLoading(true);
      setError('');
      try {
        const tasks = await getQuizTasks({ subjectCode: SUBJECT_CODE, examCode: EXAM_CODE, sectionCode });
        if (cancelled) return;
        setAllTasks(tasks || []);
        chooseRandom(tasks || []);
      } catch (e) {
        if (!cancelled) setError(e?.userMessage || e?.message || 'Не удалось загрузить задания.');
      } finally {
        if (!cancelled) setLoading(false);
      }
    }
    load();
    return () => { cancelled = true; };
  }, [sectionCode]);

  return (
    <section className="mt-8 rounded-[2rem] border border-neutral-200/80 bg-neutral-50 p-4 dark:border-neutral-800 dark:bg-neutral-950/40 md:p-6">
      <div className="mb-5 flex flex-wrap items-center justify-between gap-3">
        <div>
          <h2 className="text-3xl font-black tracking-tight">Случайные задания</h2>
          <p className="mt-1 text-neutral-600 dark:text-neutral-300">Практика по {sectionCode} сразу после конспекта.</p>
        </div>
        <button type="button" onClick={() => chooseRandom(allTasks)} disabled={allTasks.length === 0 || loading} className="btn-outline inline-flex items-center gap-2 disabled:opacity-60">
          <RefreshCcw size={18} /> Другие
        </button>
      </div>

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
          {visibleTasks.map((task) => <RandomTaskCard key={task.id} task={task} />)}
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
  const [details, setDetails] = useState(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

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
        const data = await getLearningConspect(first.slug || first.id);
        if (!cancelled) setDetails(data);
      } catch (e) {
        if (!cancelled) setError(e?.userMessage || e?.message || 'Не удалось загрузить конспект.');
      } finally {
        if (!cancelled) setLoading(false);
      }
    }
    load();
    return () => { cancelled = true; };
  }, [normalizedSectionCode]);

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
