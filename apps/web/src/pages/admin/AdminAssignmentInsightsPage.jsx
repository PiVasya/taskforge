import React, { useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import Layout from '../../components/Layout';
import { Badge, Button, Card } from '../../components/ui';
import { getAdminAssignmentInsights } from '../../api/adminAssignmentInsights';
import { handleApiError } from '../../utils/handleApiError';
import { useNotify } from '../../components/notify/NotifyProvider';
import AppErrorPanel from '../../components/AppErrorPanel';
import { ArrowLeft, BarChart3, RefreshCcw, Search, X } from 'lucide-react';

const typeLabels = {
  code: 'Код',
  test: 'Тест',
  image: 'Изображение',
  'image-trial': 'Пробный image',
};

function fmtDate(v) {
  try { return new Date(v).toLocaleString(); } catch { return '—'; }
}

function CodeModal({ activity, onClose }) {
  if (!activity) return null;
  return (
    <div className="fixed inset-0 z-50 bg-black/60 backdrop-blur-sm p-4 flex items-center justify-center" onClick={onClose}>
      <div className="w-full max-w-5xl max-h-[90vh] overflow-hidden rounded-3xl border border-border bg-card shadow-2xl" onClick={(e) => e.stopPropagation()}>
        <div className="flex items-start justify-between gap-4 border-b border-border px-6 py-4">
          <div>
            <div className="text-lg font-semibold">Решение пользователя</div>
            <div className="text-sm opacity-70 mt-1">{activity.fullName || activity.email} · {activity.email}</div>
            <div className="flex flex-wrap gap-2 mt-3">
              <Badge intent={activity.status === 'passed' ? 'success' : activity.status === 'failed' ? 'danger' : 'secondary'}>{activity.status}</Badge>
              {activity.language ? <Badge intent="outline">{activity.language}</Badge> : null}
              <Badge intent="outline">{typeLabels[activity.sourceKind] || activity.sourceKind}</Badge>
              <Badge intent="outline">{fmtDate(activity.createdAtUtc)}</Badge>
            </div>
          </div>
          <Button variant="ghost" onClick={onClose}><X size={18} /></Button>
        </div>
        <div className="p-6 overflow-auto max-h-[calc(90vh-96px)]">
          <pre className="rounded-2xl border border-border bg-muted/40 p-4 text-sm leading-6 overflow-x-auto whitespace-pre-wrap break-words font-mono">{activity.fullCode || 'Код решения отсутствует'}</pre>
        </div>
      </div>
    </div>
  );
}

export default function AdminAssignmentInsightsPage() {
  const { assignmentId } = useParams();
  const notify = useNotify();
  const [data, setData] = useState(null);
  const [loading, setLoading] = useState(true);
  const [pageError, setPageError] = useState(null);
  const [solverQuery, setSolverQuery] = useState('');
  const [activityQuery, setActivityQuery] = useState('');
  const [selectedActivity, setSelectedActivity] = useState(null);

  const load = async () => {
    try {
      setLoading(true);
      setData(await getAdminAssignmentInsights(assignmentId));
      setPageError(null);
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось загрузить аналитику задания');
      setPageError(parsed);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { load(); }, [assignmentId]); 

  const cards = useMemo(() => ({
    uniqueUsers: data?.uniqueUsers ?? 0,
    successUsers: data?.successUsers ?? 0,
    attempts: (data?.codeAttempts ?? 0) + (data?.testAttempts ?? 0) + (data?.imageAttempts ?? 0),
    avgReviewSeconds: data?.avgReviewSeconds ?? null,
  }), [data]);

  const attemptBlocks = useMemo(() => {
    if (!data) return [];
    return [
      { key: 'code', label: 'Code', total: data.codeAttempts ?? 0, passed: data.passedCodeAttempts ?? 0 },
      { key: 'test', label: 'Test', total: data.testAttempts ?? 0, passed: data.passedTests ?? 0 },
      { key: 'image', label: 'Image', total: data.imageAttempts ?? 0, passed: data.passedImages ?? 0 },
    ].filter(x => x.total > 0 || (data.type === x.key || (data.type === 'image-test' && x.key === 'image')));
  }, [data]);

  const filteredSolvers = useMemo(() => {
    const q = solverQuery.trim().toLowerCase();
    const rows = data?.solvers || [];
    if (!q) return rows;
    return rows.filter(x => [x.fullName, x.email].filter(Boolean).some(v => String(v).toLowerCase().includes(q)));
  }, [data, solverQuery]);

  const filteredActivity = useMemo(() => {
    const q = activityQuery.trim().toLowerCase();
    const rows = data?.recentActivity || [];
    if (!q) return rows;
    return rows.filter(x => [x.fullName, x.email, x.language, x.sourceKind, x.status].filter(Boolean).some(v => String(v).toLowerCase().includes(q)));
  }, [data, activityQuery]);

  return (
    <Layout>
      <div className="space-y-6">
        {pageError ? <AppErrorPanel error={pageError} title="Не удалось загрузить админ-раздел" /> : null}

        <div className="flex flex-col lg:flex-row lg:items-center lg:justify-between gap-3">
          <div>
            <div className="flex items-center gap-2">
              <Link to={`/assignment/${assignmentId}`} className="btn-outline"><ArrowLeft size={16} /> <span className="ml-1">К заданию</span></Link>
            </div>
            <h1 className="text-2xl font-semibold flex items-center gap-2 mt-3"><BarChart3 size={22} /> {data?.title || 'Аналитика задания'}</h1>
            <p className="text-sm text-muted-foreground mt-2">{data?.courseTitle || '—'} · тип: {data?.type || '—'} · рейтинг: {data?.rating ?? '—'} · сложность: {data?.difficulty ?? '—'}</p>
          </div>
          <Button onClick={load}><RefreshCcw size={16} /> <span className="ml-1">Обновить</span></Button>
        </div>

        {loading && <div className="text-muted-foreground">Загрузка…</div>}

        {data && (
          <>
            <div className="grid md:grid-cols-4 gap-4">
              <Card><div className="text-sm opacity-70">Уникальных пользователей</div><div className="text-3xl font-semibold mt-2">{cards.uniqueUsers}</div></Card>
              <Card><div className="text-sm opacity-70">Успешно решили</div><div className="text-3xl font-semibold mt-2">{cards.successUsers}</div></Card>
              <Card><div className="text-sm opacity-70">Всего попыток</div><div className="text-3xl font-semibold mt-2">{cards.attempts}</div></Card>
              <Card><div className="text-sm opacity-70">Среднее время (test)</div><div className="text-3xl font-semibold mt-2">{cards.avgReviewSeconds == null ? '—' : `${cards.avgReviewSeconds}s`}</div></Card>
            </div>

            <div className="grid xl:grid-cols-[0.9fr,1.1fr] gap-6">
              <div className="space-y-4 min-h-0">
                <Card>
                  <div className="font-medium mb-3">Структура попыток</div>
                  <div className="grid gap-3" style={{ gridTemplateColumns: `repeat(${Math.min(Math.max(attemptBlocks.length, 1), 2)}, minmax(0, 1fr))` }}>
                    {attemptBlocks.map((x) => (
                      <div key={x.key} className={`rounded-2xl border px-4 py-3 text-sm ${attemptBlocks.length % 2 === 1 && x === attemptBlocks[attemptBlocks.length - 1] ? 'xl:col-span-2' : ''}`}>
                        {x.label}: <b>{x.total}</b> / passed <b>{x.passed}</b>
                      </div>
                    ))}
                  </div>
                  {data.avgReviewNote ? <div className="text-xs opacity-60 mt-3">{data.avgReviewNote}</div> : null}
                </Card>

                <Card>
                  <div className="font-medium mb-3">Языки</div>
                  <div className="flex flex-wrap gap-2">
                    {(data.languages || []).length ? data.languages.map((x) => <Badge key={x.language} intent="outline">{x.language}: {x.count}</Badge>) : <span className="text-sm opacity-60">Данных пока нет</span>}
                  </div>
                </Card>

                <Card>
                  <div className="flex items-center justify-between gap-3 mb-3">
                    <div className="font-medium">Кто решал</div>
                    <label className="relative w-full max-w-xs">
                      <Search size={14} className="absolute left-3 top-1/2 -translate-y-1/2 opacity-60" />
                      <input value={solverQuery} onChange={(e) => setSolverQuery(e.target.value)} placeholder="Поиск по имени или почте" className="h-10 w-full rounded-2xl border border-[rgba(var(--border)/0.65)] bg-[rgba(var(--muted)/0.28)] pl-9 pr-3 text-sm text-foreground outline-none placeholder:text-muted-foreground focus:border-[rgb(var(--accent))] focus:bg-[rgba(var(--card)/0.95)]" />
                    </label>
                  </div>
                  <div className="space-y-3 max-h-[380px] overflow-y-auto pr-1">
                    {filteredSolvers.length ? filteredSolvers.map((x) => (
                      <div key={x.userId} className="rounded-2xl border px-4 py-3">
                        <div className="font-medium">{x.fullName || x.email}</div>
                        <div className="text-sm opacity-70">{x.email}</div>
                        <div className="text-xs opacity-60 mt-2">Попыток: {x.totalAttempts} · Успешных: {x.successfulAttempts}</div>
                        <div className="text-xs opacity-60 mt-1">Первое действие: {fmtDate(x.firstActivityAtUtc)}</div>
                        <div className="text-xs opacity-60 mt-1">Последнее действие: {fmtDate(x.lastActivityAtUtc)}</div>
                      </div>
                    )) : <div className="text-sm opacity-60">Никого не найдено.</div>}
                  </div>
                </Card>
              </div>

              <Card className="p-4 min-h-[620px] flex flex-col">
                <div className="flex items-center justify-between gap-3 mb-3">
                  <div className="font-medium">Последняя активность</div>
                  <label className="relative w-full max-w-xs">
                    <Search size={14} className="absolute left-3 top-1/2 -translate-y-1/2 opacity-60" />
                    <input value={activityQuery} onChange={(e) => setActivityQuery(e.target.value)} placeholder="Фильтр по активности" className="h-10 w-full rounded-2xl border border-[rgba(var(--border)/0.65)] bg-[rgba(var(--muted)/0.28)] pl-9 pr-3 text-sm text-foreground outline-none placeholder:text-muted-foreground focus:border-[rgb(var(--accent))] focus:bg-[rgba(var(--card)/0.95)]" />
                  </label>
                </div>
                <div className="flex-1 overflow-y-auto space-y-3 pr-1 max-h-[820px]">
                  {filteredActivity.length ? filteredActivity.map((x, idx) => (
                    <div key={`${x.userId}-${x.createdAtUtc}-${idx}`} className="rounded-2xl border px-4 py-3">
                      <div className="flex items-start justify-between gap-3">
                        <div>
                          <div className="font-medium">{x.fullName || x.email}</div>
                          <div className="text-xs opacity-60 mt-1">{x.email}</div>
                        </div>
                        <div className="text-right text-xs opacity-60 shrink-0">
                          <div>{fmtDate(x.createdAtUtc)}</div>
                          <div className="mt-1">{typeLabels[x.sourceKind] || x.sourceKind}</div>
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
                      {x.hasCode ? (
                        <div className="mt-3">
                          <Button variant="outline" onClick={() => setSelectedActivity(x)}>Показать решение</Button>
                        </div>
                      ) : null}
                    </div>
                  )) : <div className="text-sm opacity-60">Активность не найдена.</div>}
                </div>
              </Card>
            </div>
          </>
        )}
      </div>
      <CodeModal activity={selectedActivity} onClose={() => setSelectedActivity(null)} />
    </Layout>
  );
}
