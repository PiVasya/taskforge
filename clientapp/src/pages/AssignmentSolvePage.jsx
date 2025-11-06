// clientapp/src/pages/AssignmentSolvePage.jsx
import React, { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import Layout from '../components/Layout';
import { Card, Button, Textarea, Select, Badge } from '../components/ui';
import { getAssignment, submitSolution } from '../api/assignments';
import { compileRun } from '../api/compiler';
import { ArrowLeft, Play, CheckCircle2, XCircle } from 'lucide-react';
import IfEditor from '../components/IfEditor';
import CodeEditor from '../components/CodeEditor';
import { useNotify } from '../components/notify/NotifyProvider';

/**
 * Страница решения задания.
 * Перед отправкой решения выполняется компиляция/запуск без входа,
 * чтобы перехватить ошибки компиляции или выполнения. На их основе
 * выводится сообщение в интерфейсе, и решение не отправляется.
 */
export default function AssignmentSolvePage() {
  // Доступные языки. Значение совпадает с тем, что принимает бэкенд.
  const LANGS = [
    { label: 'C++', value: 'cpp' },
    { label: 'C#', value: 'csharp' },
    { label: 'Python', value: 'python' },
  ];

  const { assignmentId } = useParams();
  const notify = useNotify();

  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [assignment, setAssignment] = useState(null);

  const [language, setLanguage] = useState('cpp');
  const [code, setCode] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [result, setResult] = useState(null);
  // Детальный вывод ошибки (stderr/compileStderr). Показывается в отдельном блоке.
  const [errorDetail, setErrorDetail] = useState('');
  // plainMode = true → используем Textarea; false → Monaco editor
  const [plainMode, setPlainMode] = useState(false);

  // Приведение строк результата к единому виду без CR/LF в конце
  const displayClean = (s) => (s ?? '').replace(/\r\n|\r/g, '\n').replace(/\n+$/, '');
  const normalizeNewlines = (s) =>
    (s ?? '')
      .replace(/\r\n|\r/g, '\n')
      .replace(/[ \t]+(?=\n|$)/g, '')
      .replace(/\n+$/, '');
  const onlyNewlineDiffers = (a, b) => normalizeNewlines(a) === normalizeNewlines(b);

  // Загрузка данных о задании при монтировании
  useEffect(() => {
    (async () => {
      try {
        setError('');
        setLoading(true);
        const dto = await getAssignment(assignmentId);
        setAssignment(dto);
      } catch (e) {
        setError(e.message || 'Не удалось загрузить задание');
      } finally {
        setLoading(false);
      }
    })();
  }, [assignmentId]);

  // При смене языка загружаем шаблон, если пользователь ещё не ввёл код
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

  /**
   * Унифицированный обработчик ошибок. Определяет текст базового сообщения
   * в зависимости от типа ошибки и сохраняет детальное описание в состояние.
   * Всплывающее уведомление не используется, чтобы избежать дублирования.
   * @param {string} kind - compile_error | runtime_error | time_limit | other
   * @param {string} detail - полный текст ошибки
   */
  const handleError = (kind, detail) => {
    let base;
    if (kind === 'compile_error') base = 'Ошибка компиляции';
    else if (kind === 'runtime_error') base = 'Ошибка выполнения';
    else if (kind === 'time_limit') base = 'Превышен лимит времени';
    else base = 'Произошла ошибка';
    setError(base);
    setErrorDetail(detail || '');
    // Не вызываем notify.error(base) — сообщение будет показано в самом интерфейсе
  };

  /**
   * Отправка решения: сначала компиляция/запуск без ввода, затем, если
   * всё прошло успешно, отправка на проверку тестов. Ошибки компиляции и
   * выполнения перехватываются и выводятся пользователю.
   */
  const onSubmit = async () => {
    setSubmitting(true);
    setError('');
    setErrorDetail('');
    setResult(null);

    try {
      let compileResp;
      try {
        // Компилируем и запускаем без входных данных, чтобы поймать
        // ошибки компиляции или выполнения до отправки решения.
        compileResp = await compileRun({ language, code, input: '' });
      } catch (compileErr) {
        // Ошибка HTTP 400 обычно означает проблему компиляции (например, C#).
        const data = compileErr?.response?.data || {};
        const detail =
          data.compileStderr || data.stderr || data.error || data.message || '';
        handleError('compile_error', detail);
        return;
      }

      // Разбираем ответ: статус (для C++), exitCode, stderr и error (для C#)
      const status  = compileResp?.status;
      const exit    = compileResp?.exitCode;
      const stderr  = compileResp?.stderr;
      const compileStderr = compileResp?.compileStderr;
      const errorField = compileResp?.error; // поле 'error' приходит у C# при ошибке компиляции
      const msg     = compileResp?.message;

      // Определяем факт ошибки: статус отличен от ok, либо
      // присутствует errorField (C#), либо exitCode != 0 и есть stderr/compileStderr
      const hasRuntimeError = typeof exit === 'number' && exit !== 0 && (stderr || compileStderr);
      if ((status && status !== 'ok') || errorField || hasRuntimeError) {
        let kind;
        if (status && status !== 'ok') kind = status;
        else if (errorField) kind = 'compile_error';
        else kind = 'runtime_error';
        const detail = compileStderr || stderr || errorField || msg || '';
        handleError(kind, detail);
        return;
      }

      // Если всё в порядке, отправляем решение на проверку тестов
      const r = await submitSolution(assignmentId, { language, code });
      setResult(r);
      // Можно по желанию уведомить об успешной отправке
      if (r.passedAll || r.passedAllTests) {
        notify.success('Решение успешно: все тесты пройдены');
      } else {
        notify.info('Решение отправлено. Проверьте результаты тестов.');
      }
    } catch (e) {
      setError(e.message || 'Не удалось отправить решение');
    } finally {
      setSubmitting(false);
    }
  };

  // Отображение загрузки
  if (loading) {
    return (
      <Layout>
        <div className="text-slate-500">Загрузка…</div>
      </Layout>
    );
  }
  // Если задание не найдено
  if (!assignment) {
    return (
      <Layout>
        <div className="text-red-500">{error || 'Задание не найдено'}</div>
      </Layout>
    );
  }

  // Публичные тесты (видны всем)
  const publicTests = (assignment.testCases || []).filter((t) => !t.isHidden);

  return (
    <Layout>
      <div className="flex items-center justify-between mb-6">
        <div className="flex items-center gap-2">
          <Link to={`/course/${assignment.courseId}`} className="text-brand-600 hover:underline">
            <ArrowLeft size={16} /> к заданиям курса
          </Link>
        </div>
        {/* В редакторском режиме показываем ссылку на правку */}
        <div className="flex items-center gap-2">
          <IfEditor>
            <Link to={`/assignment/${assignment.id}/edit`} className="btn-outline">
              Редактировать
            </Link>
          </IfEditor>
          {/* Ссылка на топ решений доступна всегда */}
          <Link to={`/assignment/${assignment.id}/top`} className="btn-outline">
            Топ решений
          </Link>
        </div>
      </div>

      <div className="grid lg:grid-cols-3 gap-6">
        {/* Левая колонка: описание и тесты */}
        <div className="lg:col-span-2 space-y-5">
          <Card>
            <h1 className="text-2xl font-semibold mb-1">{assignment.title}</h1>
            {assignment.tags && (
              <div className="flex flex-wrap gap-2 mb-3">
                {assignment.tags
                  .split(',')
                  .filter(Boolean)
                  .map((t) => (
                    <Badge key={t.trim()}>{t.trim()}</Badge>
                  ))}
              </div>
            )}
            <div className="prose prose-slate dark:prose-invert max-w-none">
              {/* Выводим описание задания с сохранением форматирования */}
              {assignment.description ? (
                <pre className="whitespace-pre-wrap">{assignment.description}</pre>
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

        {/* Правая колонка: редактор кода и вывод результатов */}
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
                </Select>
              </div>

              {/* Выбор режима ввода: графический редактор (Monaco) или простой текст */}
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
              {/* Общая ошибка */}
              {error && <div className="text-red-500 text-sm whitespace-pre-wrap">{error}</div>}
              {/* Детальный текст ошибки показываем только если он отличается от общего */}
              {errorDetail && errorDetail.trim() !== error.trim() && (
                <div className="text-xs mt-2 whitespace-pre-wrap max-h-60 overflow-auto border border-red-200 dark:border-red-800 rounded p-2 bg-red-50 dark:bg-red-900/20 text-red-800 dark:text-red-100">
                  {errorDetail}
                </div>
              )}
            </div>
          </Card>

          {result && (
            <Card>
              {/* Итоговая информация: все тесты пройдены или нет */}
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

              {/* Отображение результатов по каждому тесту */}
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
                          <pre className="whitespace-pre-wrap text-sm">{displayClean(c.expected)}</pre>
                        </div>
                        <div>
                          <div className="text-xs text-slate-500 mb-1">Actual</div>
                          <pre className="whitespace-pre-wrap text-sm">{displayClean(c.actual)}</pre>
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
