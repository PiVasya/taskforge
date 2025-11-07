// src/pages/AssignmentSolvePage.jsx
import React, { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';

import Layout from '../components/Layout';
import { Card, Button, Select, Textarea, Badge } from '../components/ui';
import IfEditor from '../components/IfEditor';
import CodeEditor from '../components/CodeEditor';

import { useNotify } from '../components/notify/NotifyProvider'; // правильный импорт
import { getAssignment } from '../api/assignments';               // как и было
import { submitSolution } from '../api/solutions';                // теперь путь на /api/assignments/{id}/submit

import { ArrowLeft, Play, CheckCircle2, XCircle } from 'lucide-react';

const LANGS = [
  { value: 'cpp', label: 'C++' },
  { value: 'python', label: 'Python' },
  { value: 'csharp', label: 'C#' },
];

function displayClean(s) {
  if (s == null) return '';
  return String(s);
}

function onlyNewlineDiffers(exp, act) {
  if (exp == null || act == null) return false;
  const a = String(exp).replace(/\r\n/g, '\n');
  const b = String(act).replace(/\r\n/g, '\n');
  return a !== b && a.trimEnd() === b.trimEnd();
}

export default function AssignmentSolvePage() {
  const { assignmentId } = useParams();
  const notify = useNotify();

  const [a, setA] = useState(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  const [language, setLanguage] = useState('cpp');
  const [plainMode, setPlainMode] = useState(false);
  const [code, setCode] = useState('');
  const [submitting, setSubmitting] = useState(false);

  const [result, setResult] = useState(null); // { passedAllTests, results: [...] }

  useEffect(() => {
    let alive = true;
    (async () => {
      try {
        setLoading(true);
        const data = await getAssignment(assignmentId);
        if (!alive) return;
        setA(data);
        // можно подставить язык по умолчанию из задания
      } catch (e) {
        if (!alive) return;
        setError(e?.message || 'Не удалось загрузить задание');
      } finally {
        if (alive) setLoading(false);
      }
    })();
    return () => { alive = false; };
  }, [assignmentId]);

  const onSubmit = async () => {
    if (!code.trim()) return;
    setSubmitting(true);
    setError('');
    setResult(null);
    try {
      const r = await submitSolution(assignmentId, { language, code });
      setResult(r);

      if (r?.passedAllTests) {
        notify.success('Все тесты пройдены!');
      } else if (r?.compileError) {
        notify.error('Ошибка компиляции');
      } else {
        notify.error('Не все тесты пройдены');
      }
    } catch (e) {
      const msg = e?.response?.data?.error || e?.message || 'Не удалось отправить решение';
      setError(msg);
      notify.error(msg);
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
      {/* верхняя панель */}
      <div className="flex items-center justify-between mb-6">
        <div className="flex items-center gap-2">
          <Link to={`/course/${a.courseId}`} className="text-brand-600 hover:underline flex items-center gap-1">
            <ArrowLeft size={16} /> к заданиям курса
          </Link>
        </div>
        <div className="flex items-center gap-2">
          <IfEditor>
            <Link to={`/assignment/${a.id}/edit`} className="btn-outline">
              Редактировать
            </Link>
          </IfEditor>
          {/* Кнопку «Топ решений» убрали по твоему запросу */}
        </div>
      </div>

      <div className="grid lg:grid-cols-3 gap-6">
        {/* левая часть: текст задачи + публичные тесты */}
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
            {/* аккуратный вывод описания с переносами и спецсимволами */}
            <pre className="whitespace-pre-wrap text-[15px] leading-relaxed">
              {a.description || 'Описание не задано.'}
            </pre>
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
                    <div className="text-xs text-slate-500 mb-1">Ввод:</div>
                    <pre className="whitespace-pre-wrap text-sm">{t.input}</pre>
                    <div className="text-xs text-slate-500 mt-2 mb-1">Ожидаемый вывод:</div>
                    <pre className="whitespace-pre-wrap text-sm">{t.expectedOutput}</pre>
                  </div>
                ))}
              </div>
            )}
          </Card>
        </div>

        {/* правая часть: редактор и результат */}
        <div className="space-y-4">
          <Card>
            <div className="grid gap-3">
              <div>
                <label className="label">Язык</label>
                <Select value={language} onChange={(e) => setLanguage(e.target.value)}>
                  {LANGS.map((l) => (
                    <option key={l.value} value={l.value}>{l.label}</option>
                  ))}
                </Select>
              </div>

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
                <Play size={16} /> {submitting ? 'Отправляю…' : 'Отправить'}
              </Button>
              {error && <div className="text-red-500 text-sm">{error}</div>}
            </div>
          </Card>

          {result && (
            <Card>
              {/* итоговая строка */}
              <div className="flex items-center justify-between">
                <div className="flex items-center gap-2">
                  {result.passedAllTests ? (
                    <CheckCircle2 className="text-green-600" size={18} />
                  ) : (
                    <XCircle className="text-red-600" size={18} />
                  )}
                  <div className="font-medium">
                    {result.passedAllTests ? 'Все тесты пройдены!' : 'Не все тесты пройдены.'}
                  </div>
                </div>
                <div className="text-sm text-slate-500">
                  Успешно:{' '}
                  <span className="font-medium">{result.passedCount ?? 0}</span>{' '}
                  · Провалено:{' '}
                  <span className="font-medium">{result.failedCount ?? 0}</span>
                </div>
              </div>

              {/* список кейсов — компактно */}
              <div className="mt-4 grid sm:grid-cols-2 gap-4">
                {(result.results ?? []).map((c, i) => {
                  const newlineOnly = !c.passed && onlyNewlineDiffers(c.expectedOutput, c.actualOutput);
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
                      <div className="text-xs text-slate-500 mb-1">Ввод</div>
                      <pre className="whitespace-pre-wrap text-sm">{displayClean(c.input)}</pre>
                      <div className="grid grid-cols-2 gap-3 mt-2">
                        <div>
                          <div className="text-xs text-slate-500 mb-1">Ожидаемый</div>
                          <pre className="whitespace-pre-wrap text-sm">{displayClean(c.expectedOutput)}</pre>
                        </div>
                        <div>
                          <div className="text-xs text-slate-500 mb-1">Фактический</div>
                          <pre className="whitespace-pre-wrap text-sm">{displayClean(c.actualOutput)}</pre>
                        </div>
                      </div>
                      {!c.passed && (
                        <div className="mt-2 text-xs text-amber-600">
                          {newlineOnly
                            ? 'Различие только в переводах строк (\\r\\n vs \\n).'
                            : c.stderr
                              ? `Runtime error: ${c.stderr}`
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
