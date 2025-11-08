import React, { useEffect, useState } from 'react';
import { Link, useParams, useNavigate, useSearchParams } from 'react-router-dom';
import Layout from '../components/Layout';
import { Card, Button, Badge } from '../components/ui';
import { getAssignment } from '../api/assignments';
import { ArrowLeft, RotateCcw } from 'lucide-react';

function displayClean(s) {
  if (s == null) return '';
  return String(s);
}

export default function AssignmentResultsPage() {
  const { assignmentId } = useParams();
  const nav = useNavigate();
  const [searchParams] = useSearchParams();
  const view = searchParams.get('view') || 'full';

  const [a, setA] = useState(null);
  const [loading, setLoading] = useState(true);
  const [res, setRes] = useState(null);

  useEffect(() => {
    let alive = true;
    (async () => {
      setLoading(true);
      try {
        const data = await getAssignment(assignmentId);
        if (!alive) return;
        setA(data);
      } catch {
        if (alive) setA(null);
      } finally {
        if (alive) setLoading(false);
      }
    })();
    return () => { alive = false; };
  }, [assignmentId]);

  useEffect(() => {
    const raw = localStorage.getItem(`results:${assignmentId}`);
    if (raw) {
      try {
        const { result } = JSON.parse(raw);
        setRes(result);
      } catch {
        setRes(null);
      }
    }
  }, [assignmentId]);

  const handleBack = (e) => {
    e.preventDefault();
    // если вкладка открыта скриптом, закроется; если нельзя закрыть — fallback
    window.close();
    // небольшой резерв: если закрытие блокируется — просто уйти на страницу решения
    setTimeout(() => {
      try { if (!window.closed) nav(`/assignment/${assignmentId}`); } catch {}
    }, 50);
  };

  if (loading) {
    return (
      <Layout>
        <div className="text-slate-500">Загрузка…</div>
      </Layout>
    );
  }

  if (!res) {
    return (
      <Layout>
        <div className="flex items-center justify-between mb-6">
          <div className="flex items-center gap-2">
            <a href={`/assignment/${assignmentId}`} onClick={handleBack} className="text-brand-600 hover:underline">
              <ArrowLeft size={16} /> назад к решению
            </a>
          </div>
          <div className="flex items-center gap-2">
            <Button onClick={() => nav(0)} variant="outline">
              <RotateCcw size={16} className="mr-1" /> Обновить
            </Button>
          </div>
        </div>
        <Card>
          <div className="text-slate-500 p-3">Нет данных для отображения.</div>
        </Card>
      </Layout>
    );
  }

  const cases = res.cases ?? res.testCases ?? res.results ?? [];
  const passedAll =
    (res.passedAll === true) ||
    (res.passedAllTests === true) ||
    (Array.isArray(cases) && cases.length > 0 && cases.every(c => c?.passed === true || c?.status === 'OK'));

  return (
    <Layout>
      <div className="flex items-center justify-between mb-6">
        <div className="flex items-center gap-2">
          <a href={`/assignment/${assignmentId}`} onClick={handleBack} className="text-brand-600 hover:underline">
            <ArrowLeft size={16} /> назад к решению
          </a>
          {view === 'smoke' && <Badge>Пробный прогон</Badge>}
        </div>
        <div className="flex items-center gap-2">
          <Button onClick={() => nav(0)} variant="outline">
            <RotateCcw size={16} className="mr-1" /> Обновить
          </Button>
        </div>
      </div>

      <Card>
        <div className="p-4">
          <div className="mb-3">
            {passedAll ? (
              <div className="text-emerald-700 font-medium">Все тесты пройдены</div>
            ) : (
              <div className="text-red-700 font-medium">Не все тесты пройдены</div>
            )}
          </div>

          <div className="space-y-4">
            {cases.map((c, i) => {
              const expectedText = c.expected ?? c.expectedOutput ?? c.ExpectedOutput ?? '';
              const actualText   = c.actual   ?? c.actualOutput   ?? c.ActualOutput   ?? '';
              return (
                <div key={i} className="rounded border p-3">
                  <div className="flex items-center justify-between mb-2">
                    <div className="text-sm font-medium">Тест #{i + 1}</div>
                    <div className={`text-xs px-2 py-0.5 rounded ${c.passed || c.status === 'OK' ? 'bg-emerald-100 text-emerald-700' : 'bg-red-100 text-red-700'}`}>
                      {c.passed || c.status === 'OK' ? 'OK' : 'FAIL'}
                    </div>
                  </div>

                  {'input' in c || 'Input' in c ? (
                    <>
                      <div className="text-xs text-slate-500 mb-1">Ввод</div>
                      <pre className="whitespace-pre-wrap text-sm">{displayClean(c.input ?? c.Input)}</pre>
                    </>
                  ) : null}

                  {(expectedText ?? '') !== '' && (
                    <>
                      <div className="text-xs text-slate-500 mt-2 mb-1">Ожидаемый вывод</div>
                      <pre className="whitespace-pre-wrap text-sm">{displayClean(expectedText)}</pre>
                    </>
                  )}

                  {(actualText ?? '') !== '' && (
                    <>
                      <div className="text-xs text-slate-500 mt-2 mb-1">Фактически</div>
                      <pre className="whitespace-pre-wrap text-sm">{displayClean(actualText)}</pre>
                    </>
                  )}

                  {(c.compileStderr || c.stderr || c.error) && (
                    <div className="mt-2">
                      <div className="text-xs text-slate-500 mb-1">Ошибки</div>
                      <pre className="whitespace-pre-wrap text-xs text-red-600">
                        {displayClean(c.compileStderr || c.stderr || c.error)}
                      </pre>
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        </div>
      </Card>
    </Layout>
  );
}
