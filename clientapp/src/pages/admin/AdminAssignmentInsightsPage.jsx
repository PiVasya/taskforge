import React, { useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import Layout from '../../components/Layout';
import { Badge, Button, Card } from '../../components/ui';
import { getAdminAssignmentInsights } from '../../api/adminAssignmentInsights';
import { handleApiError } from '../../utils/handleApiError';
import { useNotify } from '../../components/notify/NotifyProvider';
import { AlertTriangle, ArrowLeft, BarChart3, RefreshCcw } from 'lucide-react';

export default function AdminAssignmentInsightsPage() {
  const { assignmentId } = useParams();
  const notify = useNotify();
  const [data, setData] = useState(null);
  const [loading, setLoading] = useState(true);
  const [pageError, setPageError] = useState('');

  const load = async () => {
    try {
      setLoading(true);
      setData(await getAdminAssignmentInsights(assignmentId));
      setPageError('');
    } catch (e) {
      setPageError(e?.message || 'Не удалось загрузить аналитику задания');
      handleApiError(e, notify, 'Не удалось загрузить аналитику задания');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { load(); }, [assignmentId]); // eslint-disable-line

  const cards = useMemo(() => ({
    uniqueUsers: data?.uniqueUsers ?? 0,
    successUsers: data?.successUsers ?? 0,
    attempts: (data?.codeAttempts ?? 0) + (data?.testAttempts ?? 0) + (data?.imageAttempts ?? 0),
    avgReviewSeconds: data?.avgReviewSeconds ?? null,
  }), [data]);

  return (
    <Layout>
      <div className="space-y-6">
        {pageError ? (
          <Card className="border-rose-300 bg-rose-50 text-rose-700">
            <div className="flex items-start gap-2">
              <AlertTriangle size={18} className="mt-0.5" />
              <div>
                <div className="font-medium">Ошибка админ-раздела</div>
                <div className="text-sm mt-1 whitespace-pre-wrap">{pageError}</div>
              </div>
            </div>
          </Card>
        ) : null}

        <div className="flex flex-col lg:flex-row lg:items-center lg:justify-between gap-3">
          <div>
            <div className="flex items-center gap-2">
              <Link to={`/assignment/${assignmentId}`} className="btn-outline"><ArrowLeft size={16} /> <span className="ml-1">К заданию</span></Link>
            </div>
            <h1 className="text-2xl font-semibold flex items-center gap-2 mt-3"><BarChart3 size={22} /> {data?.title || 'Аналитика задания'}</h1>
            <p className="text-sm text-neutral-500 mt-2">{data?.courseTitle || '—'} · тип: {data?.type || '—'} · рейтинг: {data?.rating ?? '—'} · сложность: {data?.difficulty ?? '—'}</p>
          </div>
          <Button onClick={load}><RefreshCcw size={16} /> <span className="ml-1">Обновить</span></Button>
        </div>

        {loading && <div className="text-neutral-500">Загрузка…</div>}

        {data && (
          <>
            <div className="grid md:grid-cols-4 gap-4">
              <Card><div className="text-sm opacity-70">Уникальных пользователей</div><div className="text-3xl font-semibold mt-2">{cards.uniqueUsers}</div></Card>
              <Card><div className="text-sm opacity-70">Успешно решили</div><div className="text-3xl font-semibold mt-2">{cards.successUsers}</div></Card>
              <Card><div className="text-sm opacity-70">Всего попыток</div><div className="text-3xl font-semibold mt-2">{cards.attempts}</div></Card>
              <Card><div className="text-sm opacity-70">Среднее время (test)</div><div className="text-3xl font-semibold mt-2">{cards.avgReviewSeconds == null ? '—' : `${cards.avgReviewSeconds}s`}</div></Card>
            </div>

            <div className="grid xl:grid-cols-[0.9fr,1.1fr] gap-6">
              <div className="space-y-4">
                <Card>
                  <div className="font-medium mb-3">Структура попыток</div>
                  <div className="grid grid-cols-2 gap-3 text-sm">
                    <div className="rounded-2xl border px-4 py-3">Code: <b>{data.codeAttempts}</b> / passed <b>{data.passedCodeAttempts}</b></div>
                    <div className="rounded-2xl border px-4 py-3">Test: <b>{data.testAttempts}</b> / passed <b>{data.passedTests}</b></div>
                    <div className="rounded-2xl border px-4 py-3 col-span-2">Image: <b>{data.imageAttempts}</b> / passed <b>{data.passedImages}</b></div>
                  </div>
                  {data.avgReviewNote && <div className="text-xs opacity-60 mt-3">{data.avgReviewNote}</div>}
                </Card>

                <Card>
                  <div className="font-medium mb-3">Языки</div>
                  <div className="flex flex-wrap gap-2">
                    {(data.languages || []).length ? data.languages.map((x) => <Badge key={x.language} intent="outline">{x.language}: {x.count}</Badge>) : <span className="text-sm opacity-60">Данных пока нет</span>}
                  </div>
                </Card>

                <Card>
                  <div className="font-medium mb-3">Кто решал</div>
                  <div className="space-y-3 max-h-[520px] overflow-y-auto pr-1">
                    {(data.solvers || []).map((x) => (
                      <div key={x.userId} className="rounded-2xl border px-4 py-3">
                        <div className="font-medium">{x.fullName || x.email}</div>
                        <div className="text-sm opacity-70">{x.email}</div>
                        <div className="text-xs opacity-60 mt-2">Попыток: {x.totalAttempts} · Успешных: {x.successfulAttempts}</div>
                        <div className="text-xs opacity-60 mt-1">Первое действие: {new Date(x.firstActivityAtUtc).toLocaleString()}</div>
                        <div className="text-xs opacity-60 mt-1">Последнее действие: {new Date(x.lastActivityAtUtc).toLocaleString()}</div>
                      </div>
                    ))}
                  </div>
                </Card>
              </div>

              <Card className="p-4 min-h-[760px] flex flex-col">
                <div className="font-medium mb-3">Последняя активность и как именно решали</div>
                <div className="flex-1 overflow-y-auto space-y-3 pr-1">
                  {(data.recentActivity || []).map((x, idx) => (
                    <div key={`${x.userId}-${x.createdAtUtc}-${idx}`} className="rounded-2xl border px-4 py-3">
                      <div className="flex items-start justify-between gap-3">
                        <div>
                          <div className="font-medium">{x.fullName || x.email}</div>
                          <div className="text-xs opacity-60 mt-1">{x.email}</div>
                        </div>
                        <div className="text-right text-xs opacity-60">
                          <div>{new Date(x.createdAtUtc).toLocaleString()}</div>
                          <div className="mt-1">{x.sourceKind}</div>
                        </div>
                      </div>
                      <div className="flex flex-wrap gap-2 mt-3">
                        <Badge intent={x.status === 'passed' ? 'success' : x.status === 'failed' ? 'danger' : 'secondary'}>{x.status}</Badge>
                        {x.language ? <Badge intent="outline">{x.language}</Badge> : null}
                        {x.scorePercent != null ? <Badge intent="outline">score {x.scorePercent}%</Badge> : null}
                        {x.similarityPercent != null ? <Badge intent="outline">sim {Number(x.similarityPercent).toFixed(1)}%</Badge> : null}
                        {x.durationSeconds != null ? <Badge intent="outline">{x.durationSeconds}s</Badge> : null}
                        {x.passedCount != null || x.failedCount != null ? <Badge intent="outline">tests {x.passedCount ?? 0}/{(x.passedCount ?? 0) + (x.failedCount ?? 0)}</Badge> : null}
                      </div>
                      {x.preview ? <div className="mt-3 text-sm opacity-80 whitespace-pre-wrap break-words">{x.preview}</div> : null}
                    </div>
                  ))}
                </div>
              </Card>
            </div>
          </>
        )}
      </div>
    </Layout>
  );
}
