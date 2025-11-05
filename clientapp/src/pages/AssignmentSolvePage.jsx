// clientapp/src/pages/AssignmentSolvePage.jsx
import React, { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import Layout from '../components/Layout';
import { Card, Button, Input, Textarea, Select, Badge } from '../components/ui';
import { getAssignment, submitSolution } from '../api/assignments';
import { compileRun } from '../api/compiler';
import { ArrowLeft, Play, CheckCircle2, XCircle } from 'lucide-react';
import IfEditor from '../components/IfEditor';
import CodeEditor from '../components/CodeEditor';
import { useNotify } from '../components/notify/NotifyProvider';

export default function AssignmentSolvePage() {
  const LANGS = [
    { label: 'C++', value: 'cpp' },
    { label: 'C#', value: 'csharp' },
    { label: 'Python', value: 'python' },
  ];

  const { assignmentId } = useParams();
  const notify = useNotify();

  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [a, setA] = useState(null);

  const [language, setLanguage] = useState('cpp');
  const [code, setCode] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [result, setResult] = useState(null);
  // detailed error output from compiler/runtime; useful for showing long errors
  const [errorDetail, setErrorDetail] = useState('');
  // plainMode = true → use Textarea; false → use Monaco editor
  const [plainMode, setPlainMode] = useState(false);

  // helpers to display clean text in results
  const displayClean = (s) => (s ?? '').replace(/\r\n|\r/g, '\n').replace(/\n+$/, '');
  const normalizeNewlines = (s) =>
    (s ?? '')
      .replace(/\r\n|\r/g, '\n')
      .replace(/[ \t]+(?=\n|$)/g, '')
      .replace(/\n+$/, '');
  const onlyNewlineDiffers = (a, b) => normalizeNewlines(a) === normalizeNewlines(b);

  // load assignment details on mount
  useEffect(() => {
    (async () => {
      try {
        setError('');
        setLoading(true);
        const dto = await getAssignment(assignmentId);
        setA(dto);
      } catch (e) {
        setError(e.message || 'Не удалось загрузить задание');
      } finally {
        setLoading(false);
      }
    })();
  }, [assignmentId]);

  // load template code for selected language if current code is empty
  useEffect(() => {
    if (code.trim()) return;
    if (language === 'python') {
      setCode('# write your solution here\n');
    } else if (language === 'cpp') {
      setCode(`#include <iostream>\nusing namespace std;\nint main(){ /* ... */ return 0; }`);
    } else if (language === 'csharp') {
      setCode(`using System;\nclass Program { static void Main(){ /* ... */ } }`);
    }
  }, [language, code]);

  const onSubmit = async () => {
    setSubmitting(true);
    setError('');
    setResult(null);
    setErrorDetail('');
    try {
      let compileResp;
      try {
        // компилируем и запускаем без ввода, чтобы поймать ошибки компиляции/выполнения
        compileResp = await compileRun({ language, code, input: '' });
      } catch (compileErr) {
        // сюда попадём, если CompilerController вернул 400 (compile_error)
        const data = compileErr?.response?.data || {};
        const errMsg =
          data.compileStderr ||
          data.stderr ||
          data.message ||
          'Ошибка компиляции';
        notify.error(errMsg);
        setError(errMsg);
        // сохраняем полный текст ошибки для отображения (если есть)
        if (typeof data.compileStderr === 'string' || typeof data.stderr === 'string') {
          setErrorDetail(data.compileStderr || data.stderr || '');
        }
        return;
      }

      // HTTP 200, но возможен статус runtime_error, time_limit или compile_error
      if (compileResp && compileResp.status && compileResp.status !== 'ok') {
        const errMsg =
          compileResp.compileStderr ||
          compileResp.stderr ||
          compileResp.message ||
          (compileResp.status === 'runtime_error'
            ? 'Ошибка выполнения'
            : compileResp.status === 'time_limit'
            ? 'Превышен лимит времени'
            : 'Произошла ошибка');
        notify.error(errMsg);
        setError(errMsg);
        // сохраняем детальную ошибку
        if (typeof compileResp.compileStderr === 'string' || typeof compileResp.stderr === 'string') {
          setErrorDetail(compileResp.compileStderr || compileResp.stderr || '');
        }
        return;
      }

      // если всё нормально – отправляем решение на проверку тестов
      const r = await submitSolution(assignmentId, { language, code });
      setResult(r);
    } catch (e) {
      setError(e.message || 'Не удалось отправить решение');
    } finally {
      setSubmitting(false);
    }
  };

  if (loading) {
    return (
      <Layout>
        <div className="text-slate-500">Загрузка…</div>
      </Layout>
    );
  }
  if (!a) {
    return (
      <Layout>
        <div className="text-red-500">{error || 'Задание не найдено'}</div>
      </Layout>
    );
  }

  const publicTests = (a.testCases || []).filter((t) => !t.isHidden);

  return (
    <Layout>
      <div className="flex items-center justify-between mb-6">
        <div className="flex items-center gap-2">
          <Link to={`/course/${a.courseId}`} className="text-brand-600 hover:underline">
            <ArrowLeft size={16} /> к заданиям курса
          </Link>
        </div>
        {/* в редакторском режиме дадим быстрый переход к правке */}
        <div className="flex items-center gap-2">
          <IfEditor>
            <Link to={`/assignment/${a.id}/edit`} className="btn-outline">
              Редактировать
            </Link>
          </IfEditor>
          {/* ссылка на топ решений — доступна всем пользователям */}
          <Link to={`/assignment/${a.id}/top`} className="btn-outline">
            Топ решений
          </Link>
        </div>
      </div>

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
            <div className="prose prose-slate dark:prose-invert max-w-none">
              {/* выводим описание задания как текст с сохранением < и > и переносов строк */}
              {a.description ? (
                <pre className="whitespace-pre-wrap">{a.description}</pre>
              ) : (
                <p className="text-slate-500">Описание не задано.</p>
              )}
            </div>
          </Card>
          <Card>
            <h2 className="text-lg font-semibold mb-3">Публичные тесты</h2>
            {publicTests.length === 0 ? (
              <div className="text-slate-500">Публичных тестов нет.</div>
            ) : (
              <div className="grid sm:grid-cols-2 gap-4">
                {publicTests.map((t, i) => (
                  <div
                    key={t.id ?? i}
                    className="rounded-xl border border-slate-200 dark:border-slate-800 p-3 bg-white/60 dark:bg-slate-900/40"
                  >
                    <div className="text-xs text-slate-500 mb-1">Input</div>
                    <pre className="whitespace-pre-wrap text-sm">{t.input}</pre>
                    <div className="text-xs text-slate-500 mt-2 mb-1">Expected Output</div>
                    <pre className="whitespace-pre-wrap text-sm">{t.expectedOutput}</pre>
                  </div>
                ))}
              </div>
            )}
          </Card>
        </div>

        <div className="space-y-4">
          <Card>
            <div className="grid gap-3">
              <div>
                <label className="label">Язык</label>
                <Select value={language} onChange={(e) => setLanguage(e.target.value)}>
                  {LANGS.map((l) => (
                    <option key={l.value} value={l.value}>
                      {l.label}
                    </option>
                  ))}
                  {/* добавь/удали языки по поддержке бэка */}
                </Select>
              </div>

              {/* выбор режима редактора: графический (Monaco) или простой текст */}
              <div>
                <label className="label">Режим ввода</label>
                <Select
                  value={plainMode ? 'plain' : 'editor'}
                  onChange={(e) => setPlainMode(e.target.value === 'plain')}
                >
                  <option value="editor">Графический редактор</option>
                  <option value="plain">Простой текст</option>
                </Select>
              </div>

              <div>
                <label className="label">Ваш код</label>
                {plainMode ? (
                  <Textarea
                    rows={14}
                    value={code}
                    onChange={(e) => setCode(e.target.value)}
                    placeholder="// Напишите решение..."
                  />
                ) : (
                  <CodeEditor language={language} value={code} onChange={setCode} />
                )}
              </div>

              <Button onClick={onSubmit} disabled={submitting || !code.trim()}>
                <Play size={16} /> {submitting ? 'Отправляю…' : 'Отправить решение'}
              </Button>
              {error && <div className="text-red-500 text-sm whitespace-pre-wrap">{error}</div>}
              {errorDetail && (
                <div className="text-xs mt-2 whitespace-pre-wrap max-h-60 overflow-auto border border-red-200 dark:border-red-800 rounded p-2 bg-red-50 dark:bg-red-900/20 text-red-800 dark:text-red-100">
                  {errorDetail}
                </div>
              )}
            </div>
          </Card>

          {result && (
            <Card>
              {/* Итоговая плашка */}
              <div className="flex items-center justify-between">
                <div className="flex items-center gap-2">
                  {result.passedAll || result.passedAllTests ? (
                    <CheckCircle2 className="text-green-600" size={18} />
                  ) : (
                    <XCircle className="text-red-600" size={18} />
                  )}
                  <div className="font-medium">
                    {result.passedAll || result.passedAllTests
                      ? 'Все тесты пройдены!'
                      : 'Не все тесты пройдены.'}
                  </div>
                </div>
                <div className="text-sm text-slate-500">
                  Успешно:{' '}
                  <span className="font-medium">
                    {result.passed ?? result.passedCount ?? 0}
                  </span>{' '}
                  · Провалено:{' '}
                  <span className="font-medium">
                    {result.failed ?? result.failedCount ?? 0}
                  </span>
                </div>
              </div>

              {/* Кейсы */}
              <div className="mt-4 grid sm:grid-cols-2 gap-4">
                {(result.cases ?? result.testCases ?? []).map((c, i) => {
                  const newlineOnly = !c.passed && onlyNewlineDiffers(c.expected, c.actual);
                  return (
                    <div
                      key={i}
                      className="rounded-xl border border-slate-200 dark:border-slate-800 p-3 bg-white/60 dark:bg-slate-900/40"
                    >
                      <div className="mb-2 flex items-center justify-between">
                        <div className="text-sm font-semibold">
                          Тест #{i + 1} {c.hidden ? '(скрытый)' : ''}
                        </div>
                        {c.passed ? (
                          <span className="text-green-600 text-sm">OK</span>
                        ) : (
                          <span className="text-red-600 text-sm">FAIL</span>
                        )}
                      </div>
                      <div className="text-xs text-slate-500 mb-1">Input</div>
                      <pre className="whitespace-pre-wrap text-sm">{c.input}</pre>
                      <div className="grid grid-cols-2 gap-3 mt-2">
                        <div>
                          <div className="text-xs text-slate-500 mb-1">Expected</div>
                          <pre className="whitespace-pre-wrap text-sm">
                            {displayClean(c.expected)}
                          </pre>
                        </div>
                        <div>
                          <div className="text-xs text-slate-500 mb-1">Actual</div>
                          <pre className="whitespace-pre-wrap text-sm">
                            {displayClean(c.actual)}
                          </pre>
                        </div>
                      </div>
                      {!c.passed && (
                        <div className="mt-2 text-xs text-amber-600">
                          {newlineOnly
                            ? 'Различие только в переводах строк (\\r\\n vs \\n). На сервере сейчас строгое сравнение — исправим это.'
                            : 'Вывод отличается от ожидаемого.'}
                        </div>
                      )}
                    </div>
                  );
                })}
              </div>
            </Card>
          )}
        </div>
      </div>
    </Layout>
  );
}
