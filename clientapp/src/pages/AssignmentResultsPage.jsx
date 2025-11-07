import React, { useEffect, useState } from 'react';
import { Link, useParams, useNavigate } from 'react-router-dom';
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

  const [a, setA] = useState(null);
  const [loading, setLoading] = useState(true);
  const [res, setRes] = useState(null);

  useEffect(() => {
    (async () => {
      try {
        setLoading(true);
        const data = await getAssignment(assignmentId);
        setA(data);
      } finally {
        setLoading(false);
      }
    })();
  }, [assignmentId]);

  useEffect(() => {
    // читаем из localStorage, куда положили после сабмита
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
        <div className="mb-4">
          <Link to={`/assignment/${assignmentId}`} className="text-brand-600 hover:underline">
            <ArrowLeft size={16} /> назад к решению
          </Link>
        </div>
        <div className="text-slate-500">Нет результатов для отображения.</div>
      </Layout>
    );
  }

  const cases = res.cases ?? res.testCases ?? res.results ?? [];
  const passedAll = res.passedAll || res.passedAllTests;

  return (
    <Layout>
      <div className="flex items-center justify-between mb-6">
        <div className="flex items-center gap-2">
          <Link to={`/assignment/${assignmentId}`} className="text-brand-600 hover:underline">
            <ArrowLeft size={16} /> назад к решению
          </Link>
        </div>
        <div className="flex items-center gap-2">
          <Button variant="outline" onClick={() => nav(`/assignment/${assignmentId}`)}>
            <RotateCcw size={16} /> Попробовать ещё
          </Button>
        </div>
      </div>

      <Card>
        <div className="flex items-center justify-between">
          <div className="text-xl font-semibold">
            {a?.title ?? 'Результаты тестов'}
          </div>
          <div className="text-sm">
            {passedAll ? (
              <span className="text-green-600 font-medium">Все тесты пройдены</span>
            ) : (
              <span className="text-red-600 font-medium">Есть непройденные тесты</span>
            )}
          </div>
        </div>

        <div className="mt-2 text-slate-500">
          Успешно: <span className="font-medium">{res.passed ?? res.passedCount ?? 0}</span>{' '}
          · Провалено:{' '}
          <span className="font-medium">{res.failed ?? res.failedCount ?? cases.filter(c => !c.passed).length}</span>
        </div>

        <div className="mt-6 grid sm:grid-cols-2 gap-4">
          {cases.map((c, i) => (
            <div
              key={i}
              className="rounded-xl border border-slate-200 dark:border-slate-800 p-3 bg-white/60 dark:bg-slate-900/40"
            >
              <div className="mb-2 flex items-center justify-between">
                <div className="text-sm font-semibold">
                  Тест #{i + 1} {c.hidden ? <Badge>скрытый</Badge> : null}
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
                  <div className="text-xs text-slate-500 mb-1">Ожидалось</div>
                  <pre className="whitespace-pre-wrap text-sm">{displayClean(c.expected ?? c.expectedOutput)}</pre>
                </div>
                <div>
                  <div className="text-xs text-slate-500 mb-1">Фактически</div>
                  <pre className="whitespace-pre-wrap text-sm">{displayClean(c.actual ?? c.actualOutput)}</pre>
                </div>
              </div>

              {(c.compileStderr || c.stderr) && (
                <div className="mt-2">
                  <div className="text-xs text-slate-500 mb-1">Ошибки</div>
                  <pre className="whitespace-pre-wrap text-xs text-red-600">
                    {displayClean(c.compileStderr || c.stderr)}
                  </pre>
                </div>
              )}
            </div>
          ))}
        </div>
      </Card>
    </Layout>
  );
}
