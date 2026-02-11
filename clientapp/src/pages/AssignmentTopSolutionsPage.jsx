import React, { useEffect, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import Layout from '../components/Layout';
import { Button, Card } from '../components/ui';
import { getAssignment, getTopSolutions } from '../api/assignments';

/**
 * Displays a leaderboard of the best solutions submitted for a particular assignment.
 * Users can navigate back to the assignment solve page and see who has passed the most tests.
 */
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
        // Fetch assignment details and top solutions concurrently
        const [aData, sData] = await Promise.all([
          getAssignment(assignmentId),
          getTopSolutions(assignmentId),
        ]);
        setAssignment(aData);
        setSolutions(sData);
      } catch (e) {
        setError(e.message || 'Не удалось загрузить данные');
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
                {solutions.map((sol, i) => (
                  <tr key={i}>
                    <td className="px-3 py-2">{i + 1}</td>
                    <td className="px-3 py-2">{sol.userName}</td>
                    <td className="px-3 py-2">{sol.passedCount}</td>
                    <td className="px-3 py-2">{sol.failedCount}</td>
                    <td className="px-3 py-2">
                      {new Date(sol.submittedAt).toLocaleString()}
                    </td>
                    <td className="px-3 py-2">{sol.language}</td>
                    <td className="px-3 py-2 max-w-xl whitespace-pre-wrap">
                      <pre className="overflow-auto">
                        {sol.code}
                      </pre>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>
    </Layout>
  );
}
