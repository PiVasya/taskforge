import React, { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';

import Layout from '../components/Layout';
import { Card, Button, Select, Badge, Textarea } from '../components/ui';
import { getAssignment } from '../api/assignments';
import { runTestsForAssignment } from '../api/solutions';
import { ArrowLeft, Play, CheckCircle2, XCircle } from 'lucide-react';
import IfEditor from '../components/IfEditor';

import CodeEditor from '../components/CodeEditor';
import CompileErrorPanel from '../components/runner/CompileErrorPanel';
import RuntimeErrorPanel from '../components/runner/RuntimeErrorPanel';

export default function AssignmentSolvePage() {
  const LANGS = [
    { label: 'C++', value: 'cpp' },
    { label: 'C#', value: 'csharp' },
    { label: 'Python', value: 'python' },
  ];

  const { assignmentId } = useParams();

  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [a, setA] = useState(null);

  const [language, setLanguage] = useState('cpp');
  const [code, setCode] = useState('');
  const [stdin, setStdin] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [result, setResult] = useState(null);

  const displayClean = (s) => (s ?? '').replace(/\r\n|\r/g, '\n').replace(/\n+$/, '');
  const normalizeNewlines = (s) =>
    (s ?? '').replace(/\r\n|\r/g, '\n').replace(/[ \t]+(?=\n|$)/g, '').replace(/\n+$/, '');
  const onlyNewlineDiffers = (aa, bb) => normalizeNewlines(aa) === normalizeNewlines(bb);

  useEffect(() => {
    (async () => {
      try {
        setError('');
        setLoading(true);
        const dto = await getAssignment(assignmentId);
        setA(dto);
      } catch (e) {
        setError(e?.message || 'Не удалось загрузить задание');
      } finally {
        setLoading(false);
      }
    })();
  }, [assignmentId]);

  useEffect(() => {
    if (code.trim()) return;
    if (language === 'python') {
      setCode('# write your solution here\n');
    } else if (language === 'cpp') {
      setCode(`#include <iostream>
using namespace std;
int main(){ /* ... */ return 0; }`);
    } else if (language === 'csharp') {
      setCode(`using System;
class Program { static void Main(){ /* ... */ } }`);
    }
  }, [language, code]);

  const onSubmit = async () => {
    setSubmitting(true);
    setError('');
    setResult(null);

    try {
      // Собираем ВСЕ тесты задания (и публичные, и скрытые — бэк их не подтягивает сам)
      // getAssignment уже отдаёт testCases
      const testCases = (a?.testCases || []).map((t) => ({
        input: t.input ?? '',
        expectedOutput: t.expectedOutput ?? '',
      }));

      if (testCases.length === 0) {
        setResult({ status: 'no_tests', tests: [] });
        setSubmitting(false);
        return;
      }

      const data = await runTestsForAssignment({
        language,
        source: code,
        testCases,
        // при необходимости передай лимиты:
        // timeLimitMs: 2000,
        // memoryLimitMb: 256,
      });

      // Приводим к формату, который уже ожидает UI ниже
      const tests = Array.isArray(data?.results) ? data.results : [];
      const passed = tests.filter((t) => t.passed).length;
      const status = passed === tests.length ? 'passed' : 'failed';

      setResult({ status, tests });
    } catch (e) {
      // если бэк вернул 400 на компиляционную ошибку — покажем как runtime/compile панель позже при необходимости
      setError(e?.message || 'Не удалось выполнить код');
    } finally {
      setSubmitting(false);
    }
  };

  if (loading) return <Layout><div className="text-slate-500">Загрузка…</div></Layout>;
  if (!a)      return <Layout><div className="text-red-500">{error || 'Задание не найдено'}</div></Layout>;

  const publicTests = (a.testCases || []).filter(t => !t.isHidden);

  return (
    <Layout>
      <div className="flex items-center justify-between mb-6">
        <div className="flex items-center gap-2">
          <Link to={`/course/${a.courseId}`} className="text-brand-600 hover:underline">
            <ArrowLeft size={16} /> к заданиям курса
          </Link>
        </div>
        <IfEditor>
          <Link to={`/assignment/${a.id}/edit`} className="btn-outline">Редактировать</Link>
        </IfEditor>
      </div>

      <div className="grid lg:grid-cols-3 gap-6">
        {/* левая колонка */}
        <div className="lg:col-span-2 space-y-5">
          <Card>
            <h1 className="text-2xl font-semibold mb-1">{a.title}</h1>
            {a.tags && (
              <div className="flex flex-wrap gap-2 mb-3">
                {a.tags.split(',').filter(Boolean).map(t => <Badge key={t.trim()}>{t.trim()}</Badge>)}
              </div>
            )}
            <div className="prose prose-slate dark:prose-invert max-w-none">
              {a.description ? (
                <div dangerouslySetInnerHTML={{ __html: a.description.replace(/\n/g, '<br/>') }} />
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
                  <div key={t.id ?? i} className="rounded-xl border border-slate-200 dark:border-slate-800 p-3 bg-white/60 dark:bg-slate-900/40">
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

        {/* правая колонка */}
        <div className="space-y-4">
          <Card>
            <div className="grid gap-3">
              <div>
                <label className="label">Язык</label>
                <Select value={language} onChange={e => setLanguage(e.target.value)}>
                  {LANGS.map(l => (
                    <option key={l.value} value={l.value}>{l.label}</option>
                  ))}
                </Select>
              </div>

              <div>
                <label className="label">Ваш код</label>
                <CodeEditor language={language} value={code} onChange={setCode} />
              </div>

              <details className="mt-2">
                <summary className="cursor-pointer select-none text-sm text-slate-500">stdin (опционально)</summary>
                <Textarea rows={4} value={stdin} onChange={(e) => setStdin(e.target.value)} placeholder="Ввод программы…" />
              </details>

              <Button onClick={onSubmit} disabled={submitting || !code.trim()}>
                <Play size={16} /> {submitting ? 'Выполняю…' : 'Отправить решение'}
              </Button>

              {error && <div className="text-red-500 text-sm">{error}</div>}
            </div>
          </Card>

          {/* Компиляция / рантайм / тесты */}
          {result && (
            <>
              {/* Оставляем панели — если когда-нибудь начнёшь возвращать их из /api/tests/run/tests */}
              {result.compile && <CompileErrorPanel compile={result.compile} source={code} />}
              {result.run && <RuntimeErrorPanel run={result.run} source={code} />}

              {(result.tests ?? []).length > 0 && (
                <Card>
                  <div className="flex items-center justify-between">
                    <div className="flex items-center gap-2">
                      {result.status === 'passed' ? (
                        <CheckCircle2 className="text-green-600" size={18} />
                      ) : (
                        <XCircle className="text-red-600" size={18} />
                      )}
                      <div className="font-medium">
                        {result.status === 'passed' ? 'Все тесты пройдены!' : 'Не все тесты пройдены.'}
                      </div>
                    </div>
                    <div className="text-sm text-slate-500">
                      Успешно: <span className="font-medium">{(result.tests || []).filter(t => t.passed).length}</span> ·
                      Провалено: <span className="font-medium">{(result.tests || []).filter(t => !t.passed).length}</span>
                    </div>
                  </div>

                  <div className="mt-4 grid sm:grid-cols-2 gap-4">
                    {(result.tests || []).map((c, i) => {
                      const newlineOnly = !c.passed && onlyNewlineDiffers(c.expected, c.actual);
                      return (
                        <div key={i} className="rounded-xl border border-slate-200 dark:border-slate-800 p-3 bg-white/60 dark:bg-slate-900/40">
                          <div className="mb-2 flex items-center justify-between">
                            <div className="text-sm font-semibold">Тест #{i + 1}</div>
                            {c.passed ? <span className="text-green-600 text-sm">OK</span> : <span className="text-red-600 text-sm">FAIL</span>}
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
                              {newlineOnly ? 'Различие только в переводах строк (\\r\\n vs \\n).' : 'Вывод отличается от ожидаемого.'}
                            </div>
                          )}
                        </div>
                      );
                    })}
                  </div>
                </Card>
              )}
            </>
          )}
        </div>
      </div>
    </Layout>
  );
}
