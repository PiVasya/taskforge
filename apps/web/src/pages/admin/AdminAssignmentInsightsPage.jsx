import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { Badge, Button, Card } from '../../components/ui';
import { createAssignmentAnalyticsConnection, getAdminAssignmentInsights, getAdminAssignmentTimeline } from '../../api/adminAssignmentInsights';
import { handleApiError } from '../../utils/handleApiError';
import { useNotify } from '../../components/notify/NotifyProvider';
import AppErrorPanel from '../../components/AppErrorPanel';
import { ArrowLeft, BarChart3, Eye, FileCode2, RefreshCcw, Search, ShieldAlert, Users, X } from 'lucide-react';
import useAdminSolutionLiveFeed from '../../features/admin-solutions/useAdminSolutionLiveFeed';
import AssignmentAnalyticsCharts from '../../features/assignment-analytics/AssignmentAnalyticsCharts';
import AssignmentLiveFeed from '../../features/assignment-analytics/AssignmentLiveFeed';
import { analyticsModeLabels, behaviorLabel, riskLabel, riskReasonLabel, statusLabel, typeLabel } from '../../features/assignment-analytics/assignmentInsightsModel';

const tabs = [
  { key: 'overview', label: 'Обзор и графики' },
  { key: 'live', label: 'Лента решений' },
  { key: 'users', label: 'Пользователи' },
  { key: 'attempts', label: 'История попыток' },
  { key: 'proctoring', label: 'Контроль поведения' },
  { key: 'similarity', label: 'Совпадения кода' },
];

function fmtDate(v) {
  if (!v) return '—';
  try {
    const d = new Date(v);
    return Number.isNaN(d.getTime()) ? '—' : d.toLocaleString();
  } catch { return '—'; }
}

function fmtDuration(ms) {
  const n = Number(ms || 0);
  if (!Number.isFinite(n) || n <= 0) return '—';
  const sec = Math.round(n / 1000);
  if (sec < 60) return `${sec}с`;
  const min = Math.floor(sec / 60);
  const rest = sec % 60;
  if (min < 60) return `${min}м ${rest}с`;
  const h = Math.floor(min / 60);
  return `${h}ч ${min % 60}м`;
}

function pct(num, den) {
  const a = Number(num || 0);
  const b = Number(den || 0);
  if (!b) return 0;
  return Math.round((a * 1000) / b) / 10;
}

function riskIntent(levelOrScore) {
  const value = typeof levelOrScore === 'number' ? levelOrScore : String(levelOrScore || '').toLowerCase();
  if (value === 'critical' || value >= 75) return 'danger';
  if (value === 'high' || value >= 50) return 'danger';
  if (value === 'medium' || value >= 25) return 'warning';
  return 'success';
}

function riskText(score, level) {
  return `${Number(score || 0)} · ${riskLabel(level || (Number(score || 0) >= 75 ? 'critical' : Number(score || 0) >= 50 ? 'high' : Number(score || 0) >= 25 ? 'medium' : 'low'))}`;
}

function StatCard({ label, value, hint, icon: Icon }) {
  return (
    <Card className="min-h-[110px]">
      <div className="flex items-start justify-between gap-3">
        <div>
          <div className="text-sm opacity-70">{label}</div>
          <div className="text-3xl font-semibold mt-2">{value}</div>
          {hint ? <div className="text-xs opacity-60 mt-2">{hint}</div> : null}
        </div>
        {Icon ? <Icon size={22} className="opacity-50" /> : null}
      </div>
    </Card>
  );
}

function CodeModal({ activity, onClose }) {
  if (!activity) return null;
  const code = activity.fullCode || activity.codeSample || '';
  return (
    <div className="fixed inset-0 z-50 bg-black/60 backdrop-blur-sm p-4 flex items-center justify-center" onClick={onClose}>
      <div className="w-full max-w-6xl max-h-[90vh] overflow-hidden rounded-3xl border border-border bg-card shadow-2xl" onClick={(e) => e.stopPropagation()}>
        <div className="flex items-start justify-between gap-4 border-b border-border px-6 py-4">
          <div>
            <div className="text-lg font-semibold">Код решения</div>
            <div className="text-sm opacity-70 mt-1">{activity.fullName || activity.displayName || activity.email || 'Пользователь'} · {activity.email || 'без email'}</div>
            <div className="flex flex-wrap gap-2 mt-3">
              <Badge intent={activity.status === 'passed' ? 'success' : activity.status === 'failed' ? 'danger' : 'secondary'}>{statusLabel(activity.status)}</Badge>
              {activity.language ? <Badge intent="outline">{activity.language}</Badge> : null}
              <Badge intent="outline">{typeLabel(activity.sourceKind || activity.kind)}</Badge>
              <Badge intent="outline">{fmtDate(activity.createdAtUtc)}</Badge>
              {activity.codeLength ? <Badge intent="outline">{activity.codeLength} символов</Badge> : null}
            </div>
          </div>
          <Button variant="ghost" onClick={onClose}><X size={18} /></Button>
        </div>
        <div className="p-6 overflow-auto max-h-[calc(90vh-110px)]">
          <pre className="rounded-2xl border border-border bg-muted/40 p-4 text-sm leading-6 overflow-x-auto whitespace-pre-wrap break-words font-mono">{code || 'Код решения отсутствует. Возможно, сохранение кода отключено в настройках задания.'}</pre>
        </div>
      </div>
    </div>
  );
}

function TimelineModal({ user, rows, loading, onClose }) {
  if (!user) return null;
  return (
    <div className="fixed inset-0 z-50 bg-black/60 backdrop-blur-sm p-4 flex items-center justify-center" onClick={onClose}>
      <div className="w-full max-w-4xl max-h-[90vh] overflow-hidden rounded-3xl border border-border bg-card shadow-2xl" onClick={(e) => e.stopPropagation()}>
        <div className="flex items-start justify-between gap-4 border-b border-border px-6 py-4">
          <div>
            <div className="text-lg font-semibold">История действий пользователя</div>
            <div className="text-sm opacity-70 mt-1">{user.fullName || user.displayName || user.email || 'Пользователь'}</div>
          </div>
          <Button variant="ghost" onClick={onClose}><X size={18} /></Button>
        </div>
        <div className="p-6 overflow-auto max-h-[calc(90vh-100px)] space-y-2">
          {loading ? <div className="text-sm opacity-60">Загрузка истории действий…</div> : null}
          {!loading && !rows.length ? <div className="text-sm opacity-60">Событий пока нет.</div> : null}
          {rows.map((x, idx) => (
            <div key={`${x.id || idx}`} className="rounded-2xl border border-border px-4 py-3 text-sm">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <b>{behaviorLabel(x.eventType)}</b>
                <span className="text-xs opacity-60">{fmtDate(x.createdAt || x.createdAtUtc || x.clientTime)}</span>
              </div>
              <div className="mt-1 flex flex-wrap gap-2 text-xs opacity-70">
                {x.codeLength != null ? <span>длина кода: {x.codeLength}</span> : null}
                {x.codeDelta != null ? <span>изменение кода: {x.codeDelta}</span> : null}
                {x.textLength != null ? <span>символов: {x.textLength}</span> : null}
                {x.language ? <span>{x.language}</span> : null}
                {x.riskPoints ? <span>риск {x.riskPoints}: {riskReasonLabel(x.riskReason)}</span> : null}
              </div>
              {x.textSample ? <pre className="mt-2 rounded-xl bg-muted/40 p-2 whitespace-pre-wrap break-words text-xs">{x.textSample}</pre> : null}
            </div>
          ))}
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
  const [selectedUser, setSelectedUser] = useState(null);
  const [timelineRows, setTimelineRows] = useState([]);
  const [timelineLoading, setTimelineLoading] = useState(false);
  const [activeTab, setActiveTab] = useState('overview');
  const [liveEvents, setLiveEvents] = useState([]);
  const [liveStatus, setLiveStatus] = useState('connecting');
  const { items: solutionEvents, state: solutionState } = useAdminSolutionLiveFeed(assignmentId);

  const load = useCallback(async (silent = false) => {
    try {
      if (!silent) setLoading(true);
      setData(await getAdminAssignmentInsights(assignmentId));
      setPageError(null);
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось загрузить аналитику задания');
      setPageError(parsed);
    } finally {
      if (!silent) setLoading(false);
    }
  }, [assignmentId, notify]);

  useEffect(() => { load(); }, [load]);

  // Keep graphs in sync with persisted verdicts without forcing a full
  // analytics query for every SSE notification.
  useEffect(() => {
    if (solutionState !== 'live') return undefined;
    const timer = setInterval(() => load(true), 60000);
    return () => clearInterval(timer);
  }, [load, solutionState]);

  useEffect(() => {
    setLiveStatus('connecting');
    const connection = createAssignmentAnalyticsConnection(assignmentId, (items) => {
      setLiveEvents((prev) => [...items, ...prev].slice(0, 80));
    }, setLiveStatus);
    return () => { connection?.stop?.().catch(() => {}); };
  }, [assignmentId]);

  const cards = useMemo(() => {
    const attempts = (data?.codeAttempts ?? 0) + (data?.testAttempts ?? 0) + (data?.mathAttempts ?? 0) + (data?.imageAttempts ?? 0);
    const solved = (data?.passedCodeAttempts ?? 0) + (data?.passedTests ?? 0) + (data?.passedMath ?? 0) + (data?.passedImages ?? 0);
    return {
      openedUsers: data?.openedUsers ?? 0,
      uniqueUsers: data?.uniqueUsers ?? 0,
      successUsers: data?.successUsers ?? 0,
      attempts,
      solved,
      successRate: pct(solved, attempts),
      avgReviewSeconds: data?.avgReviewSeconds ?? null,
      risk: data?.proctoringSummary?.maxRisk ?? 0,
    };
  }, [data]);

  const filteredSolvers = useMemo(() => {
    const q = solverQuery.trim().toLowerCase();
    const rows = data?.solvers || [];
    if (!q) return rows;
    return rows.filter(x => [x.fullName, x.displayName, x.email, x.language, x.riskLevel].filter(Boolean).some(v => String(v).toLowerCase().includes(q)));
  }, [data, solverQuery]);

  const filteredActivity = useMemo(() => {
    const q = activityQuery.trim().toLowerCase();
    const rows = data?.recentActivity || [];
    if (!q) return rows;
    return rows.filter(x => [x.fullName, x.displayName, x.email, x.language, x.sourceKind, x.status].filter(Boolean).some(v => String(v).toLowerCase().includes(q)));
  }, [data, activityQuery]);

  const openTimeline = async (user) => {
    setSelectedUser(user);
    setTimelineRows([]);
    setTimelineLoading(true);
    try {
      const rows = await getAdminAssignmentTimeline(assignmentId, user.userId);
      setTimelineRows(Array.isArray(rows) ? rows : []);
    } catch (e) {
      notify.error('Не удалось загрузить timeline пользователя');
    } finally {
      setTimelineLoading(false);
    }
  };

  const proctoring = data?.proctoringSummary || {};

  return (
    <>
      <div className="space-y-6">
        {pageError ? <AppErrorPanel error={pageError} title="Не удалось загрузить админ-раздел" /> : null}

        <div className="flex flex-col lg:flex-row lg:items-center lg:justify-between gap-3">
          <div>
            <div className="flex items-center gap-2">
              <Link to={`/assignment/${assignmentId}`} className="btn-outline"><ArrowLeft size={16} /> <span className="ml-1">К заданию</span></Link>
            </div>
            <h1 className="text-2xl font-semibold flex items-center gap-2 mt-3"><BarChart3 size={22} /> {data?.title || 'Аналитика задания'}</h1>
            <p className="text-sm text-muted-foreground mt-2">{data?.courseTitle || '—'} · {typeLabel(data?.type)} · аналитика: {analyticsModeLabels[data?.analyticsSettings?.mode] || 'Базовая'} · рейтинг: {data?.rating ?? '—'}</p>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <Badge intent={solutionState === 'live' ? 'success' : 'warning'}>SSE: {solutionState === 'live' ? 'подключено' : solutionState === 'reconnecting' ? 'переподключение' : 'подключение'}</Badge>
            <Button onClick={() => load()}><RefreshCcw size={16} /> <span className="ml-1">Обновить</span></Button>
          </div>
        </div>

        {loading && <div className="text-muted-foreground">Загрузка…</div>}

        {data && (
          <>
            <div className="grid sm:grid-cols-2 xl:grid-cols-6 gap-4">
              <StatCard label="Открывали" value={cards.openedUsers} hint="по событиям просмотра" icon={Eye} />
              <StatCard label="Участников" value={cards.uniqueUsers} hint="решения и действия" icon={Users} />
              <StatCard label="Попыток" value={cards.attempts} hint={`${cards.solved} успешных`} icon={FileCode2} />
              <StatCard label="Успешность" value={`${cards.successRate}%`} hint="по всем типам попыток" />
              <StatCard label="Среднее время" value={cards.avgReviewSeconds == null ? '—' : fmtDuration(cards.avgReviewSeconds * 1000)} hint="тесты и математика" />
              <StatCard label="Макс. риск" value={cards.risk} hint={`${proctoring.criticalSessions || 0} критических сессий`} icon={ShieldAlert} />
            </div>

            <div className="flex flex-wrap gap-2">
              {tabs.map((t) => (
                <button key={t.key} type="button" onClick={() => setActiveTab(t.key)} className={`rounded-2xl border px-4 py-2 text-sm ${activeTab === t.key ? 'bg-[rgb(var(--accent))] text-[rgb(var(--accent-foreground))] border-transparent' : 'border-border bg-card hover:bg-muted/40'}`}>
                  {t.label}
                </button>
              ))}
            </div>

            {activeTab === 'overview' && (
              <div className="space-y-6">
                <AssignmentAnalyticsCharts data={data} />
                <AssignmentLiveFeed assignmentId={assignmentId} data={data} liveItems={solutionEvents} liveState={solutionState} compact />
                <Card>
                  <div className="font-medium mb-3">Популярные языки программирования</div>
                  <div className="flex flex-wrap gap-2">
                    {(data.languages || []).length ? data.languages.map((x, idx) => (
                      <Badge key={`${x.label || idx}`} intent="outline">{x.label || 'Не указан'}: {x.value ?? x.count ?? 0}</Badge>
                    )) : <span className="text-sm opacity-60">Данных по языкам пока нет.</span>}
                  </div>
                </Card>
              </div>
            )}

            {activeTab === 'live' && (
              <AssignmentLiveFeed assignmentId={assignmentId} data={data} liveItems={solutionEvents} liveState={solutionState} />
            )}

            {activeTab === 'users' && (
              <Card>
                <div className="flex flex-col md:flex-row md:items-center md:justify-between gap-3 mb-4">
                  <div className="font-medium">Пользователи и риск</div>
                  <label className="relative w-full max-w-sm">
                    <Search size={14} className="absolute left-3 top-1/2 -translate-y-1/2 opacity-60" />
                    <input value={solverQuery} onChange={(e) => setSolverQuery(e.target.value)} placeholder="Поиск по имени, почте, языку" className="h-10 w-full rounded-2xl border border-border bg-muted/30 pl-9 pr-3 text-sm outline-none" />
                  </label>
                </div>
                <div className="overflow-x-auto">
                  <table className="w-full text-sm">
                    <thead className="text-left opacity-60">
                      <tr><th className="py-2">Пользователь</th><th>Статус</th><th>Попытки</th><th>Время</th><th>Поведение</th><th>Риск</th><th></th></tr>
                    </thead>
                    <tbody>
                      {filteredSolvers.map((x) => (
                        <tr key={x.userId} className="border-t border-border align-top">
                          <td className="py-3 pr-3"><div className="font-medium">{x.fullName || x.displayName || x.email || 'Пользователь'}</div><div className="text-xs opacity-60">{x.email || x.userId}</div></td>
                          <td className="py-3 pr-3"><Badge intent={(x.successfulAttempts || x.passed) ? 'success' : 'secondary'}>{(x.successfulAttempts || x.passed) ? 'есть успех' : 'без успеха'}</Badge><div className="text-xs opacity-60 mt-1">{x.language || '—'}</div></td>
                          <td className="py-3 pr-3">{x.totalAttempts ?? x.attempts ?? 0}<div className="text-xs opacity-60">успешных: {x.successfulAttempts ?? x.passed ?? 0}</div></td>
                          <td className="py-3 pr-3">{fmtDuration(x.totalDurationMs)}<div className="text-xs opacity-60">активно: {fmtDuration(x.activeDurationMs)}</div></td>
                          <td className="py-3 pr-3 text-xs opacity-80">вставок: {x.pasteCount || 0} · смен фокуса: {x.blurCount || 0} · скрытий: {x.hiddenCount || 0} · выходов из полного экрана: {x.fullscreenExitCount || 0}</td>
                          <td className="py-3 pr-3"><Badge intent={riskIntent(x.riskScore)}>{riskText(x.riskScore, x.riskLevel)}</Badge>{(x.riskReasons || []).length ? <div className="text-xs opacity-60 mt-1">{x.riskReasons.slice(0, 2).map(riskReasonLabel).join('; ')}</div> : null}</td>
                          <td className="py-3 text-right"><Button variant="outline" onClick={() => openTimeline(x)}>История</Button></td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                  {!filteredSolvers.length ? <div className="text-sm opacity-60 py-4">Никого не найдено.</div> : null}
                </div>
              </Card>
            )}

            {activeTab === 'attempts' && (
              <Card>
                <div className="flex flex-col md:flex-row md:items-center md:justify-between gap-3 mb-4">
                  <div className="font-medium">Последние попытки</div>
                  <label className="relative w-full max-w-sm">
                    <Search size={14} className="absolute left-3 top-1/2 -translate-y-1/2 opacity-60" />
                    <input value={activityQuery} onChange={(e) => setActivityQuery(e.target.value)} placeholder="Фильтр по активности" className="h-10 w-full rounded-2xl border border-border bg-muted/30 pl-9 pr-3 text-sm outline-none" />
                  </label>
                </div>
                <div className="space-y-3 max-h-[760px] overflow-y-auto pr-1">
                  {filteredActivity.length ? filteredActivity.map((x, idx) => (
                    <div key={`${x.userId}-${x.createdAtUtc}-${idx}`} className="rounded-2xl border px-4 py-3">
                      <div className="flex items-start justify-between gap-3">
                        <div><div className="font-medium">{x.fullName || x.displayName || x.email || 'Пользователь'}</div><div className="text-xs opacity-60 mt-1">{x.email}</div></div>
                        <div className="text-right text-xs opacity-60 shrink-0"><div>{fmtDate(x.createdAtUtc)}</div><div className="mt-1">{typeLabel(x.sourceKind)}</div></div>
                      </div>
                      <div className="flex flex-wrap gap-2 mt-3">
                        <Badge intent={x.status === 'passed' || x.passed ? 'success' : x.status === 'failed' ? 'danger' : 'secondary'}>{statusLabel(x.status, x.passed)}</Badge>
                        {x.language ? <Badge intent="outline">{x.language}</Badge> : null}
                        {x.scorePercent != null ? <Badge intent="outline">результат: {x.scorePercent}%</Badge> : null}
                        {x.durationSeconds != null ? <Badge intent="outline">{x.durationSeconds} с</Badge> : null}
                        {x.codeLength ? <Badge intent="outline">код: {x.codeLength} симв.</Badge> : null}
                      </div>
                      {x.hasCode ? <div className="mt-3"><Button variant="outline" onClick={() => setSelectedActivity(x)}>Показать код</Button></div> : null}
                    </div>
                  )) : <div className="text-sm opacity-60">Активность не найдена.</div>}
                </div>
              </Card>
            )}

            {activeTab === 'proctoring' && (
              <div className="space-y-6">
                <Card>
                  <div className="flex items-center justify-between gap-3"><div><div className="font-medium">Контроль поведения</div><div className="text-sm opacity-60 mt-1">Режим: {analyticsModeLabels[data.analyticsSettings?.mode] || 'Базовая'}</div></div><Badge intent={riskIntent(proctoring.maxRisk || 0)}>макс. риск: {proctoring.maxRisk || 0}</Badge></div>
                  <div className="mt-4 grid sm:grid-cols-2 lg:grid-cols-4 xl:grid-cols-8 gap-3 text-sm">
                    <div className="rounded-2xl border px-3 py-2">Событий: <b>{proctoring.events || 0}</b></div>
                    <div className="rounded-2xl border px-3 py-2">Сессий: <b>{proctoring.sessions || 0}</b></div>
                    <div className="rounded-2xl border px-3 py-2">Вставок: <b>{proctoring.pasteEvents || 0}</b></div>
                    <div className="rounded-2xl border px-3 py-2">Копирования/вырезания: <b>{(proctoring.copyEvents || 0) + (proctoring.cutEvents || 0)}</b></div>
                    <div className="rounded-2xl border px-3 py-2">Потерь фокуса: <b>{proctoring.blurEvents || 0}</b></div>
                    <div className="rounded-2xl border px-3 py-2">Скрытий: <b>{proctoring.hiddenEvents || 0}</b></div>
                    <div className="rounded-2xl border px-3 py-2">Выходов из полного экрана: <b>{proctoring.fullscreenExitEvents || 0}</b></div>
                    <div className="rounded-2xl border px-3 py-2">Резких изменений кода: <b>{proctoring.codeJumpEvents || 0}</b></div>
                    <div className="rounded-2xl border px-3 py-2">Активное время: <b>{fmtDuration(proctoring.activeDurationMs)}</b></div>
                    <div className="rounded-2xl border px-3 py-2">Скрытий: <b>{fmtDuration(proctoring.hiddenDurationMs)}</b></div>
                    <div className="rounded-2xl border px-3 py-2">Время вне окна: <b>{fmtDuration(proctoring.blurDurationMs)}</b></div>
                  </div>
                </Card>

                <Card>
                  <div className="flex flex-wrap items-center justify-between gap-3 mb-3">
                    <div><h2 className="font-semibold">События поведения в реальном времени</h2><p className="text-xs opacity-60 mt-1">Внутренний канал прокторинга; новые решения отображаются отдельно во вкладке «Лента решений».</p></div>
                    <Badge intent={liveStatus === 'connected' ? 'success' : 'warning'}>{liveStatus === 'connected' ? 'На связи' : 'Подключение к событиям'}</Badge>
                  </div>
                  {liveEvents.length ? <div className="grid md:grid-cols-2 gap-2 max-h-80 overflow-y-auto">
                    {liveEvents.map((x, idx) => <div key={x.eventId || `${x.createdAtUtc}-${idx}`} className="rounded-xl border border-border p-3 text-sm">
                      <div className="flex justify-between gap-2"><b>{behaviorLabel(x.eventType)}</b><span className="text-xs opacity-60">{fmtDate(x.createdAtUtc)}</span></div>
                      <div className="mt-1 text-xs opacity-70">Риск: {x.riskPoints || 0}{x.riskReason ? ` · ${riskReasonLabel(x.riskReason)}` : ''}</div>
                    </div>)}
                  </div> : <div className="text-sm opacity-60">Новых событий поведения после открытия страницы нет.</div>}
                </Card>

                <div className="grid xl:grid-cols-2 gap-6">
                  <Card>
                    <div className="font-medium mb-3">Подозрительные сессии</div>
                    <div className="space-y-2 max-h-[520px] overflow-y-auto pr-1">
                      {(data.suspiciousSessions || []).length ? data.suspiciousSessions.map((x) => (
                        <div key={x.id || `${x.userId}-${x.sessionId}`} className="rounded-2xl border px-3 py-2 text-sm">
                          <div className="flex items-center justify-between gap-2"><b>{x.displayName || x.email || 'Пользователь'}</b><Badge intent={riskIntent(x.riskScore)}>{riskText(x.riskScore, x.riskLevel)}</Badge></div>
                          <div className="text-xs opacity-70 mt-1">вставок: {x.pasteCount} · смен фокуса: {x.blurCount} · скрытий: {x.hiddenCount} · отправок: {x.submitCount} · активно: {fmtDuration(x.activeDurationMs)}</div>
                          {(x.riskReasons || []).length ? <div className="text-xs opacity-60 mt-1">{x.riskReasons.map(riskReasonLabel).join('; ')}</div> : null}
                        </div>
                      )) : <div className="text-sm opacity-60">Подозрительных сессий пока нет.</div>}
                    </div>
                  </Card>

                  <Card>
                    <div className="font-medium mb-3">Подозрительные события</div>
                    <div className="space-y-2 max-h-[520px] overflow-y-auto pr-1">
                      {(data.suspiciousEvents || []).length ? data.suspiciousEvents.map((x, idx) => (
                        <div key={`${x.eventId || idx}`} className="rounded-2xl border border-amber-300/50 bg-amber-500/5 px-3 py-2 text-sm">
                          <div className="flex flex-wrap items-center justify-between gap-2"><b>{x.displayName || x.email || 'Пользователь'}</b><span className="text-xs opacity-60">{fmtDate(x.createdAtUtc)}</span></div>
                          <div className="mt-1 opacity-80">{behaviorLabel(x.eventType)} · риск {x.riskPoints}: {riskReasonLabel(x.riskReason) || 'подозрительное событие'}</div>
                          {x.textLength ? <div className="text-xs opacity-60 mt-1">длина текста: {x.textLength}</div> : null}
                          {x.textSample ? <pre className="mt-2 rounded-xl bg-muted/40 p-2 whitespace-pre-wrap break-words text-xs">{x.textSample}</pre> : null}
                        </div>
                      )) : <div className="text-sm opacity-60">Подозрительных событий пока нет.</div>}
                    </div>
                  </Card>
                </div>
              </div>
            )}

            {activeTab === 'similarity' && (
              <Card>
                <div className="font-medium mb-3">Совпадения кода по отпечатку</div>
                {(data.similarHashes || []).length ? (
                  <div className="space-y-3">
                    {data.similarHashes.map((x) => (
                      <div key={x.codeHash} className="rounded-2xl border px-4 py-3 text-sm">
                        <div className="flex flex-wrap items-center justify-between gap-2"><b>{x.users} пользователей · {x.snapshots} сохранённых версий</b><Badge intent="danger">Одинаковый отпечаток</Badge></div>
                        <div className="mt-2 text-xs opacity-70">{x.codeHash} · максимальная длина кода: {x.maxCodeLength} · {fmtDate(x.firstSeenAt)} — {fmtDate(x.lastSeenAt)}</div>
                        <div className="mt-2 flex flex-wrap gap-2">{(x.userNames || []).map((name, idx) => <Badge key={`${name}-${idx}`} intent="outline">{name || 'Пользователь'}</Badge>)}</div>
                      </div>
                    ))}
                  </div>
                ) : <div className="text-sm opacity-60">Совпадений пока нет. Для поиска похожего кода включите сохранение отпечатков и версий в настройках задания.</div>}
              </Card>
            )}
          </>
        )}
      </div>
      <CodeModal activity={selectedActivity} onClose={() => setSelectedActivity(null)} />
      <TimelineModal user={selectedUser} rows={timelineRows} loading={timelineLoading} onClose={() => setSelectedUser(null)} />
    </>
  );
}
