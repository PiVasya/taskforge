import React, { useEffect, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import Layout from '../components/Layout';
import { Button, Card } from '../components/ui';
import { getAssignment, getTopSolutions } from '../api/assignments';
import { getApiErrorMessage } from '../api/http';
import { formatDateTime, getSolutionCode, getSolutionCounts, getSolutionSubmittedAt } from '../utils/solutionsView';


export default function AssignmentTopSolutionsPage() {
  const { assignmentId } = useParams();
  const nav = useNavigate();
  const [assignment, setAssignment] = useState(null);
  const [solutions, setSolutions] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  useEffect(() => {
    (async () => {
      try {
        setLoading(true);
        
        const [aData, sData] = await Promise.all([
          getAssignment(assignmentId),
          getTopSolutions(assignmentId),
        ]);
        setAssignment(aData);
        setSolutions(sData);
      } catch (e) {
        setError(getApiErrorMessage(e, 'Не удалось загрузить данные')); 
      } finally {
        setLoading(false);
      }
    })();
  }, [assignmentId]);

  if (loading) {
    return (
      <Layout>
        <div className="text-neutral-500">Загрузка…</div>
      </Layout>
    );
  }
  if (error) {
    return (
      <Layout>
        <div className="text-red-500">{error}</div>
      </Layout>
    );
  }

  return (
    <Layout>
      <div className="mb-6">
        <Button variant="outline" onClick={() => nav(`/assignment/${assignmentId}`)}>
          ← Назад к заданию
        </Button>
      </div>
      <Card>
        <h1 className="text-2xl font-semibold mb-4">
          Топ решений — {assignment?.title}
        </h1>
        {solutions.length === 0 ? (
          <div className="text-neutral-500">Решений пока нет.</div>
        ) : (
          <div className="overflow-x-auto">
            <table className="min-w-full divide-y divide-neutral-200 dark:divide-neutral-700 text-sm">
              <thead className="bg-neutral-50 dark:bg-neutral-800">
                <tr>
                  <th className="px-3 py-2 text-left font-medium text-neutral-500">#</th>
                  <th className="px-3 py-2 text-left font-medium text-neutral-500">Пользователь</th>
                  <th className="px-3 py-2 text-left font-medium text-neutral-500">Успешно</th>
                  <th className="px-3 py-2 text-left font-medium text-neutral-500">Провалено</th>
                  <th className="px-3 py-2 text-left font-medium text-neutral-500">Отправлено</th>
                  <th className="px-3 py-2 text-left font-medium text-neutral-500">Язык</th>
                  <th className="px-3 py-2 text-left font-medium text-neutral-500">Код</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-neutral-200 dark:divide-neutral-700">
                {solutions.map((sol, i) => {
                  const counts = getSolutionCounts(sol);
                  const code = getSolutionCode(sol);
                  const userLabel = sol.userName || sol.displayName || sol.email || sol.userId || 'Пользователь';
                  return (
                    <tr key={sol.id || sol.Id || i}>
                      <td className="px-3 py-2">{i + 1}</td>
                      <td className="px-3 py-2">{userLabel}</td>
                      <td className="px-3 py-2">{counts.passed ?? '—'}</td>
                      <td className="px-3 py-2">{counts.failed ?? '—'}</td>
                      <td className="px-3 py-2">{formatDateTime(getSolutionSubmittedAt(sol))}</td>
                      <td className="px-3 py-2">{sol.language || sol.Language || '—'}</td>
                      <td className="px-3 py-2 max-w-xl whitespace-pre-wrap">
                        {code ? (
                          <pre className="overflow-auto">{code}</pre>
                        ) : (
                          <span className="text-neutral-500">Код недоступен</span>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </Card>
    </Layout>
  );
}
