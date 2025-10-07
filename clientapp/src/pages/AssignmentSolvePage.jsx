import React, { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';

import Layout from '../components/Layout';
import { Card, Button, Select, Badge } from '../components/ui';
import { getAssignment, submitSolution } from '../api/assignments';
import { ArrowLeft, Play, CheckCircle2, XCircle } from 'lucide-react';
import IfEditor from '../components/IfEditor';

// подключаем красивый редактор
import CodeEditor from '../components/CodeEditor';

// если уже есть панели парсинга ошибок — используем
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
      // если у тебя на бэке другой контракт — подправь поля ниже
      const r = await submitSolution(assignmentId, {
        language,
        code,            // или source: code
      });
      setResult(r);
    } catch (e) {
      setError(e?.message || 'Не удалось отправить решение');
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

              <Button onClick={onSubmit} disabled={submitting || !code.trim()}>
                <Play size={16} /> {submitting ? 'Отправляю…' : 'Отправить решение'}
              </Button>

              {error && <div className="text-red-500 text-sm">{error}</div>}
            </div>
          </Card>

          {/* выводим то, что пришло с бэка: компиляция/рантайм/тесты */}
          {result && (
            <>
              {result.compile && <CompileErrorPanel compile={result.compile} source={code} />}
              {result.run && <RuntimeErrorPanel run={result.run} source={code} />}
              {(result.cases || result.testCases) && (
                <Card>
                  <div className="flex items-center justify-between">
                    <div className="flex items-center gap-2">
                      {(result.passedAll || result.passedAllTests) ? (
                        <CheckCircle2 className="text-green-600" size={18} />
                      ) : (
                        <XCircle className="text-red-600" size={18} />
                      )}
                      <div className="font-medium">
                        {(result.passedAll || result.passedAllTests) ? 'Все тесты пройдены!' : 'Не все тесты пройдены.'}
                      </div>
                    </div>
                    <div className="text-sm text-slate-500">
                      Успешно: <span className="font-medium">{result.passed ?? result.passedCount ?? 0}</span> ·
                      Провалено: <span className="font-medium">{result.failed ?? result.failedCount ?? 0}</span>
                    </div>
                  </div>

                  <div className="mt-4 grid sm:grid-cols-2 gap-4">
                    {(result.cases ?? result.testCases ?? []).map((c, i) => {
                      const newlineOnly = !c.passed && onlyNewlineDiffers(c.expected, c.actual);
                      return (
                        <div key={i} className="rounded-xl border border-slate-200 dark:border-slate-800 p-3 bg-white/60 dark:bg-slate-900/40">
                          <div className="mb-2 flex items-center justify-between">
                            <div className="text-sm font-semibold">Тест #{i + 1} {c.hidden ? '(скрытый)' : ''}</div>
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
                                ? 'Различие только в переводах строк (\\r\\n vs \\n).'
                                : 'Вывод отличается от ожидаемого.'}
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
