import React, { useEffect, useMemo, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { ArrowLeft, CheckCircle2, Loader2, PlayCircle, RotateCcw, XCircle } from 'lucide-react';
import Layout from '../components/Layout';
import { getQuizTask, getQuizTasks, submitQuizAttempt } from '../api/quiz';

function safeJson(value, fallback) {
  if (!value) return fallback;
  if (typeof value === 'object') return value;
  try {
    return JSON.parse(value);
  } catch {
    return fallback;
  }
}

export default function QuizTasksPage() {
  const [searchParams] = useSearchParams();
  const sectionCode = searchParams.get('sectionCode') || 'A1';
  const type = searchParams.get('type') || '';
  const taskSlug = searchParams.get('task') || '';
  const [tasks, setTasks] = useState([]);
  const [selectedTaskId, setSelectedTaskId] = useState('');
  const [details, setDetails] = useState(null);
  const [answer, setAnswer] = useState('');
  const [result, setResult] = useState(null);
  const [loading, setLoading] = useState(true);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => {
    let cancelled = false;

    async function loadList() {
      setLoading(true);
      setError('');
      try {
        const params = { subjectCode: 'russian', examCode: 'ct-ce-2026', sectionCode };
        if (type) params.type = type;
        const list = await getQuizTasks(params);
        if (cancelled) return;
        setTasks(list || []);
        const first = taskSlug || list?.[0]?.slug || list?.[0]?.id;
        setSelectedTaskId(first || '');
      } catch (e) {
        if (!cancelled) setError(e?.userMessage || e?.message || 'Не удалось загрузить задания.');
      } finally {
        if (!cancelled) setLoading(false);
      }
    }

    loadList();
    return () => {
      cancelled = true;
    };
  }, [sectionCode, type, taskSlug]);

  useEffect(() => {
    if (!selectedTaskId) return;
    let cancelled = false;

    async function loadTask() {
      setResult(null);
      setAnswer('');
      try {
        const data = await getQuizTask(selectedTaskId);
        if (!cancelled) setDetails(data);
      } catch (e) {
        if (!cancelled) setError(e?.userMessage || e?.message || 'Не удалось открыть задание.');
      }
    }

    loadTask();
    return () => {
      cancelled = true;
    };
  }, [selectedTaskId]);

  const taskData = useMemo(() => safeJson(details?.dataJson, {}), [details]);
  const explanation = useMemo(() => safeJson(result?.explanationJson || details?.explanationJson, {}), [result, details]);
  const options = Array.isArray(taskData.options) ? taskData.options : [];

  const handleSubmit = async () => {
    if (!details?.task?.id || !answer) return;
    setSubmitting(true);
    setError('');
    try {
      const res = await submitQuizAttempt(details.task.id, { selected: [answer] }, { clientAttemptId: crypto.randomUUID?.() });
      setResult(res);
    } catch (e) {
      setError(e?.userMessage || e?.message || 'Не удалось отправить ответ.');
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <Layout fullWidth>
      <div className="min-h-[calc(100vh-57px)] bg-neutral-50 dark:bg-neutral-950">
        <div className="container-app py-6 lg:py-8">
          <div className="mb-5 flex flex-wrap items-center justify-between gap-3">
            <div>
              <Link to="/" className="inline-flex items-center gap-2 text-sm font-medium text-brand-700 dark:text-brand-300 hover:underline">
                <ArrowLeft size={16} />
                Вернуться к конспекту
              </Link>
              <h1 className="mt-2 text-3xl font-bold tracking-tight">Задания к конспекту</h1>
              <p className="mt-1 text-neutral-600 dark:text-neutral-300">Фильтр: русский язык · ЦТ/ЦЭ · {sectionCode}{type ? ` · ${type}` : ''}</p>
            </div>
            <Link to="/admin/conspects" className="btn-outline">Редактор конспектов</Link>
          </div>

          {error && (
            <div className="mb-4 rounded-3xl border border-red-200 dark:border-red-900 bg-red-50 dark:bg-red-950/20 p-4 text-sm text-red-800 dark:text-red-100">
              {error}
            </div>
          )}

          {loading ? (
            <div className="rounded-[2rem] border border-neutral-200 dark:border-neutral-800 bg-white dark:bg-neutral-900 p-10 text-center shadow-soft">
              <Loader2 className="mx-auto animate-spin text-brand-600" />
              <div className="mt-3 text-neutral-600 dark:text-neutral-300">Загружаю задания...</div>
            </div>
          ) : (
            <div className="grid gap-6 lg:grid-cols-[320px_minmax(0,1fr)]">
              <aside className="space-y-2 lg:sticky lg:top-[77px] lg:self-start">
                {tasks.length === 0 && (
                  <div className="rounded-3xl border border-neutral-200 dark:border-neutral-800 bg-white dark:bg-neutral-900 p-5 text-sm text-neutral-600 dark:text-neutral-300">
                    Для этого фильтра пока нет опубликованных заданий.
                  </div>
                )}
                {tasks.map((task) => {
                  const active = task.id === details?.task?.id || task.slug === selectedTaskId;
                  return (
                    <button
                      key={task.id}
                      type="button"
                      onClick={() => setSelectedTaskId(task.slug || task.id)}
                      className={`w-full rounded-3xl border p-4 text-left transition ${active ? 'border-brand-400 bg-brand-50 dark:bg-brand-900/20' : 'border-neutral-200 dark:border-neutral-800 bg-white dark:bg-neutral-900 hover:border-brand-300'}`}
                    >
                      <div className="font-semibold">{task.title}</div>
                      <div className="mt-1 text-sm text-neutral-500 dark:text-neutral-400">Сложность: {task.difficulty}</div>
                    </button>
                  );
                })}
              </aside>

              <main>
                {details ? (
                  <section className="rounded-[2rem] border border-neutral-200/80 dark:border-neutral-800 bg-white dark:bg-neutral-900 shadow-soft p-6 md:p-8">
                    <div className="mb-2 text-sm font-semibold uppercase tracking-wide text-brand-700 dark:text-brand-300">
                      {details.task.sectionCode} · {details.task.type}
                    </div>
                    <h2 className="text-3xl font-bold tracking-tight">{details.task.title}</h2>
                    <p className="mt-4 text-2xl font-semibold">{details.task.prompt}</p>
                    {taskData.answerText && <p className="mt-2 text-neutral-500 dark:text-neutral-400">Полная форма после решения: {taskData.answerText}</p>}

                    <div className="mt-6 flex flex-wrap gap-3">
                      {options.length > 0 ? options.map((option) => (
                        <button
                          key={option}
                          type="button"
                          onClick={() => setAnswer(option)}
                          className={`rounded-2xl border px-6 py-4 text-xl font-bold transition ${answer === option ? 'border-brand-500 bg-brand-600 text-white' : 'border-neutral-200 dark:border-neutral-800 bg-neutral-50 dark:bg-neutral-950 hover:border-brand-300'}`}
                        >
                          {option}
                        </button>
                      )) : (
                        <input
                          value={answer}
                          onChange={(e) => setAnswer(e.target.value)}
                          placeholder="Введите ответ"
                          className="w-full rounded-2xl border border-neutral-200 dark:border-neutral-800 bg-neutral-50 dark:bg-neutral-950 px-4 py-3 outline-none focus:border-brand-400"
                        />
                      )}
                    </div>

                    <div className="mt-6 flex flex-wrap gap-3">
                      <button type="button" onClick={handleSubmit} disabled={!answer || submitting} className="btn-primary inline-flex items-center gap-2 disabled:opacity-60">
                        {submitting ? <Loader2 size={18} className="animate-spin" /> : <PlayCircle size={18} />}
                        Проверить
                      </button>
                      <button type="button" onClick={() => { setAnswer(''); setResult(null); }} className="btn-outline inline-flex items-center gap-2">
                        <RotateCcw size={18} />
                        Сбросить
                      </button>
                    </div>

                    {result && (
                      <div className={`mt-6 rounded-3xl border p-5 ${result.isCorrect ? 'border-emerald-200 bg-emerald-50 dark:border-emerald-900 dark:bg-emerald-950/20' : 'border-red-200 bg-red-50 dark:border-red-900 dark:bg-red-950/20'}`}>
                        <div className="flex items-center gap-2 font-semibold">
                          {result.isCorrect ? <CheckCircle2 size={20} /> : <XCircle size={20} />}
                          {result.isCorrect ? 'Верно' : 'Неверно'} · {result.scorePercent}%
                        </div>
                        {explanation.text && <p className="mt-2 leading-7">{explanation.text}</p>}
                      </div>
                    )}
                  </section>
                ) : (
                  <div className="rounded-[2rem] border border-neutral-200 dark:border-neutral-800 bg-white dark:bg-neutral-900 p-10 text-center shadow-soft text-neutral-600 dark:text-neutral-300">
                    Выбери задание слева.
                  </div>
                )}
              </main>
            </div>
          )}
        </div>
      </div>
    </Layout>
  );
}
