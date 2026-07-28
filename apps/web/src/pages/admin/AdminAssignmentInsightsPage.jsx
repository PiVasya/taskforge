import React, { useEffect, useMemo, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { Badge, Button, Card } from '../../components/ui';
import { createAssignmentAnalyticsConnection, getAdminAssignmentInsights, getAdminAssignmentTimeline } from '../../api/adminAssignmentInsights';
import { handleApiError } from '../../utils/handleApiError';
import { useNotify } from '../../components/notify/NotifyProvider';
import AppErrorPanel from '../../components/AppErrorPanel';
import { ArrowLeft, BarChart3, Eye, FileCode2, RefreshCcw, Search, ShieldAlert, Users, X, Zap } from 'lucide-react';

const typeLabels = {
  code: 'Код',
  'code-test': 'Код',
  test: 'Тест',
  math: 'Math/B-text',
  image: 'Изображение',
  'image-trial': 'Пробный image',
};

const tabs = [
  { key: 'overview', label: 'Обзор' },
  { key: 'users', label: 'Пользователи' },
  { key: 'attempts', label: 'Попытки' },
  { key: 'proctoring', label: 'Proctoring' },
  { key: 'similarity', label: 'Похожесть' },
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
  return `${Number(score || 0)} · ${level || (Number(score || 0) >= 75 ? 'critical' : Number(score || 0) >= 50 ? 'high' : Number(score || 0) >= 25 ? 'medium' : 'low')}`;
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
              <Badge intent={activity.status === 'passed' ? 'success' : activity.status === 'failed' ? 'danger' : 'secondary'}>{activity.status || 'unknown'}</Badge>
              {activity.language ? <Badge intent="outline">{activity.language}</Badge> : null}
              <Badge intent="outline">{typeLabels[activity.sourceKind] || activity.sourceKind || activity.kind || 'activity'}</Badge>
              <Badge intent="outline">{fmtDate(activity.createdAtUtc)}</Badge>
              {activity.codeLength ? <Badge intent="outline">{activity.codeLength} символов</Badge> : null}
            </div>
          </div>
          <Button variant="ghost" onClick={onClose}><X size={18} /></Button>
        </div>
        <div className="p-6 overflow-auto max-h-[calc(90vh-110px)]">
          <pre className="rounded-2xl border border-border bg-muted/40 p-4 text-sm leading-6 overflow-x-auto whitespace-pre-wrap break-words font-mono">{code || 'Код решения отсутствует. Возможно, для задания выключено хранение кода/snapshot.'}</pre>
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
            <div className="text-lg font-semibold">Timeline пользователя</div>
            <div className="text-sm opacity-70 mt-1">{user.fullName || user.displayName || user.email || 'Пользователь'}</div>
          </div>
          <Button variant="ghost" onClick={onClose}><X size={18} /></Button>
        </div>
        <div className="p-6 overflow-auto max-h-[calc(90vh-100px)] space-y-2">
          {loading ? <div className="text-sm opacity-60">Загрузка timeline…</div> : null}
          {!loading && !rows.length ? <div className="text-sm opacity-60">Событий пока нет.</div> : null}
          {rows.map((x, idx) => (
            <div key={`${x.id || idx}`} className="rounded-2xl border border-border px-4 py-3 text-sm">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <b>{x.eventType}</b>
                <span className="text-xs opacity-60">{fmtDate(x.createdAt || x.createdAtUtc || x.clientTime)}</span>
              </div>
              <div className="mt-1 flex flex-wrap gap-2 text-xs opacity-70">
                {x.codeLength != null ? <span>code {x.codeLength}</span> : null}
                {x.codeDelta != null ? <span>delta {x.codeDelta}</span> : null}
                {x.textLength != null ? <span>text {x.textLength}</span> : null}
                {x.language ? <span>{x.language}</span> : null}
                {x.riskPoints ? <span>risk {x.riskPoints}: {x.riskReason}</span> : null}
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

  const attemptBlocks = useMemo(() => {
    if (!data) return [];
    return [
      { key: 'code', label: 'Code', total: data.codeAttempts ?? 0, passed: data.passedCodeAttempts ?? 0 },
      { key: 'test', label: 'Test', total: data.testAttempts ?? 0, passed: data.passedTests ?? 0 },
      { key: 'math', label: 'Math/B-text', total: data.mathAttempts ?? 0, passed: data.passedMath ?? 0 },
      { key: 'image', label: 'Image', total: data.imageAttempts ?? 0, passed: data.passedImages ?? 0 },
    ].filter(x => x.total > 0 || (data.type === x.key || (data.type === 'image-test' && x.key === 'image')));
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
            <p className="text-sm text-muted-foreground mt-2">{data?.courseTitle || '—'} · тип: {data?.type || '—'} · режим: {data?.analyticsSettings?.mode || 'basic'} · рейтинг: {data?.rating ?? '—'} · сложность: {data?.difficulty ?? '—'}</p>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            {liveEvents.length ? <Badge intent="danger"><Zap size={13} className="mr-1" /> live {liveEvents.length}</Badge> : <Badge intent={liveStatus === 'connected' ? 'success' : liveStatus === 'offline' ? 'warning' : 'outline'}>live: {liveStatus === 'connected' ? 'подключён' : liveStatus === 'reconnecting' ? 'переподключение' : liveStatus === 'offline' ? 'offline' : 'подключение'}</Badge>}
            <Button onClick={load}><RefreshCcw size={16} /> <span className="ml-1">Обновить</span></Button>
          </div>
        </div>

        {loading && <div className="text-muted-foreground">Загрузка…</div>}

        {data && (
          <>
            <div className="grid sm:grid-cols-2 xl:grid-cols-6 gap-4">
              <StatCard label="Открывали" value={cards.openedUsers} hint="по activity events" icon={Eye} />
              <StatCard label="Участников" value={cards.uniqueUsers} hint="решения + события" icon={Users} />
              <StatCard label="Попыток" value={cards.attempts} hint={`${cards.solved} успешных`} icon={FileCode2} />
              <StatCard label="Успешность" value={`${cards.successRate}%`} hint="по всем типам попыток" />
              <StatCard label="Среднее время" value={cards.avgReviewSeconds == null ? '—' : fmtDuration(cards.avgReviewSeconds * 1000)} hint="test/math попытки" />
              <StatCard label="Max risk" value={cards.risk} hint={`${proctoring.criticalSessions || 0} critical`} icon={ShieldAlert} />
            </div>

            <div className="flex flex-wrap gap-2">
              {tabs.map((t) => (
                <button key={t.key} type="button" onClick={() => setActiveTab(t.key)} className={`rounded-2xl border px-4 py-2 text-sm ${activeTab === t.key ? 'bg-[rgb(var(--accent))] text-[rgb(var(--accent-foreground))] border-transparent' : 'border-border bg-card hover:bg-muted/40'}`}>
                  {t.label}
                </button>
              ))}
            </div>

            {activeTab === 'overview' && (
              <div className="grid xl:grid-cols-[1fr_1fr] gap-6">
                <Card>
                  <div className="font-medium mb-3">Структура попыток</div>
                  <div className="space-y-3">
                    {attemptBlocks.map((x) => (
                      <div key={x.key} className="rounded-2xl border px-4 py-3 text-sm">
                        <div className="flex items-center justify-between gap-3">
                          <span>{x.label}</span>
                          <b>{x.total} / passed {x.passed}</b>
                        </div>
                        <div className="mt-2 h-2 overflow-hidden rounded-full bg-muted">
                          <div className="h-full rounded-full bg-current opacity-70" style={{ width: `${Math.min(100, pct(x.passed, x.total))}%` }} />
                        </div>
                      </div>
                    ))}
                    {!attemptBlocks.length ? <div className="text-sm opacity-60">Попыток пока нет.</div> : null}
                  </div>
                </Card>

                <Card>
                  <div className="font-medium mb-3">Языки и статусы</div>
                  <div className="flex flex-wrap gap-2 mb-4">
                    {(data.languages || []).length ? data.languages.map((x, idx) => <Badge key={`${x.label || idx}`} intent="outline">{x.label || 'unknown'}: {x.value ?? x.count ?? 0}</Badge>) : <span className="text-sm opacity-60">Данных по языкам пока нет</span>}
                  </div>
                  <div className="font-medium mb-3">Статусы</div>
                  <div className="flex flex-wrap gap-2">
                    {(data.statusStats || []).length ? data.statusStats.map((x, idx) => <Badge key={`${x.label || idx}`} intent="outline">{x.label}: {x.value}</Badge>) : <span className="text-sm opacity-60">Статусов пока нет</span>}
                  </div>
                </Card>

                <Card className="xl:col-span-2">
                  <div className="font-medium mb-3">Live события</div>
                  {liveEvents.length ? (
                    <div className="grid md:grid-cols-2 gap-2 max-h-72 overflow-y-auto pr-1">
                      {liveEvents.map((x, idx) => (
                        <div key={`${x.eventId || idx}`} className="rounded-2xl border px-3 py-2 text-sm">
                          <div className="flex items-center justify-between gap-2"><b>{x.eventType}</b><span className="text-xs opacity-60">{fmtDate(x.createdAtUtc)}</span></div>
                          <div className="text-xs opacity-70 mt-1">risk {x.riskPoints || 0} {x.riskReason ? `· ${x.riskReason}` : ''}</div>
                        </div>
                      ))}
                    </div>
                  ) : <div className="text-sm opacity-60">Новых live-событий после открытия страницы пока нет.</div>}
                </Card>
              </div>
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
                      <tr><th className="py-2">Пользователь</th><th>Статус</th><th>Попытки</th><th>Время</th><th>Поведение</th><th>Risk</th><th></th></tr>
                    </thead>
                    <tbody>
                      {filteredSolvers.map((x) => (
                        <tr key={x.userId} className="border-t border-border align-top">
                          <td className="py-3 pr-3"><div className="font-medium">{x.fullName || x.displayName || x.email || 'Пользователь'}</div><div className="text-xs opacity-60">{x.email || x.userId}</div></td>
                          <td className="py-3 pr-3"><Badge intent={(x.successfulAttempts || x.passed) ? 'success' : 'secondary'}>{(x.successfulAttempts || x.passed) ? 'есть успех' : 'без успеха'}</Badge><div className="text-xs opacity-60 mt-1">{x.language || '—'}</div></td>
                          <td className="py-3 pr-3">{x.totalAttempts ?? x.attempts ?? 0}<div className="text-xs opacity-60">успешных: {x.successfulAttempts ?? x.passed ?? 0}</div></td>
                          <td className="py-3 pr-3">{fmtDuration(x.totalDurationMs)}<div className="text-xs opacity-60">active {fmtDuration(x.activeDurationMs)}</div></td>
                          <td className="py-3 pr-3 text-xs opacity-80">paste {x.pasteCount || 0} · blur {x.blurCount || 0} · hidden {x.hiddenCount || 0} · full {x.fullscreenExitCount || 0}</td>
                          <td className="py-3 pr-3"><Badge intent={riskIntent(x.riskScore)}>{riskText(x.riskScore, x.riskLevel)}</Badge>{(x.riskReasons || []).length ? <div className="text-xs opacity-60 mt-1">{x.riskReasons.slice(0, 2).join('; ')}</div> : null}</td>
                          <td className="py-3 text-right"><Button variant="outline" onClick={() => openTimeline(x)}>Timeline</Button></td>
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
                        <div className="text-right text-xs opacity-60 shrink-0"><div>{fmtDate(x.createdAtUtc)}</div><div className="mt-1">{typeLabels[x.sourceKind] || x.sourceKind}</div></div>
                      </div>
                      <div className="flex flex-wrap gap-2 mt-3">
                        <Badge intent={x.status === 'passed' || x.passed ? 'success' : x.status === 'failed' ? 'danger' : 'secondary'}>{x.status || 'unknown'}</Badge>
                        {x.language ? <Badge intent="outline">{x.language}</Badge> : null}
                        {x.scorePercent != null ? <Badge intent="outline">score {x.scorePercent}%</Badge> : null}
                        {x.durationSeconds != null ? <Badge intent="outline">{x.durationSeconds}s</Badge> : null}
                        {x.codeLength ? <Badge intent="outline">code {x.codeLength}</Badge> : null}
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
                  <div className="flex items-center justify-between gap-3"><div><div className="font-medium">Proctoring / контроль поведения</div><div className="text-sm opacity-60 mt-1">Режим: {data.analyticsSettings?.mode || 'basic'}</div></div><Badge intent={riskIntent(proctoring.maxRisk || 0)}>max risk {proctoring.maxRisk || 0}</Badge></div>
                  <div className="mt-4 grid sm:grid-cols-2 lg:grid-cols-4 xl:grid-cols-8 gap-3 text-sm">
                    <div className="rounded-2xl border px-3 py-2">Событий: <b>{proctoring.events || 0}</b></div>
                    <div className="rounded-2xl border px-3 py-2">Сессий: <b>{proctoring.sessions || 0}</b></div>
                    <div className="rounded-2xl border px-3 py-2">Paste: <b>{proctoring.pasteEvents || 0}</b></div>
                    <div className="rounded-2xl border px-3 py-2">Copy/Cut: <b>{(proctoring.copyEvents || 0) + (proctoring.cutEvents || 0)}</b></div>
                    <div className="rounded-2xl border px-3 py-2">Blur: <b>{proctoring.blurEvents || 0}</b></div>
                    <div className="rounded-2xl border px-3 py-2">Hidden: <b>{proctoring.hiddenEvents || 0}</b></div>
                    <div className="rounded-2xl border px-3 py-2">Fullscreen: <b>{proctoring.fullscreenExitEvents || 0}</b></div>
                    <div className="rounded-2xl border px-3 py-2">Code jumps: <b>{proctoring.codeJumpEvents || 0}</b></div>
                    <div className="rounded-2xl border px-3 py-2">Active: <b>{fmtDuration(proctoring.activeDurationMs)}</b></div>
                    <div className="rounded-2xl border px-3 py-2">Hidden: <b>{fmtDuration(proctoring.hiddenDurationMs)}</b></div>
                    <div className="rounded-2xl border px-3 py-2">Blur time: <b>{fmtDuration(proctoring.blurDurationMs)}</b></div>
                  </div>
                </Card>

                <div className="grid xl:grid-cols-2 gap-6">
                  <Card>
                    <div className="font-medium mb-3">Подозрительные сессии</div>
                    <div className="space-y-2 max-h-[520px] overflow-y-auto pr-1">
                      {(data.suspiciousSessions || []).length ? data.suspiciousSessions.map((x) => (
                        <div key={x.id || `${x.userId}-${x.sessionId}`} className="rounded-2xl border px-3 py-2 text-sm">
                          <div className="flex items-center justify-between gap-2"><b>{x.displayName || x.email || 'Пользователь'}</b><Badge intent={riskIntent(x.riskScore)}>{riskText(x.riskScore, x.riskLevel)}</Badge></div>
                          <div className="text-xs opacity-70 mt-1">paste {x.pasteCount} · blur {x.blurCount} · hidden {x.hiddenCount} · submit {x.submitCount} · active {fmtDuration(x.activeDurationMs)}</div>
                          {(x.riskReasons || []).length ? <div className="text-xs opacity-60 mt-1">{x.riskReasons.join('; ')}</div> : null}
                        </div>
                      )) : <div className="text-sm opacity-60">Подозрительных сессий пока нет.</div>}
                    </div>
                  </Card>

                  <Card>
                    <div className="font-medium mb-3">Risk events</div>
                    <div className="space-y-2 max-h-[520px] overflow-y-auto pr-1">
                      {(data.suspiciousEvents || []).length ? data.suspiciousEvents.map((x, idx) => (
                        <div key={`${x.eventId || idx}`} className="rounded-2xl border border-amber-300/50 bg-amber-500/5 px-3 py-2 text-sm">
                          <div className="flex flex-wrap items-center justify-between gap-2"><b>{x.displayName || x.email || 'Пользователь'}</b><span className="text-xs opacity-60">{fmtDate(x.createdAtUtc)}</span></div>
                          <div className="mt-1 opacity-80">{x.eventType} · risk {x.riskPoints}: {x.riskReason || 'подозрительное событие'}</div>
                          {x.textLength ? <div className="text-xs opacity-60 mt-1">text length: {x.textLength}</div> : null}
                          {x.textSample ? <pre className="mt-2 rounded-xl bg-muted/40 p-2 whitespace-pre-wrap break-words text-xs">{x.textSample}</pre> : null}
                        </div>
                      )) : <div className="text-sm opacity-60">Risk-событий пока нет.</div>}
                    </div>
                  </Card>
                </div>
              </div>
            )}

            {activeTab === 'similarity' && (
              <Card>
                <div className="font-medium mb-3">Похожие / одинаковые решения по hash</div>
                {(data.similarHashes || []).length ? (
                  <div className="space-y-3">
                    {data.similarHashes.map((x) => (
                      <div key={x.codeHash} className="rounded-2xl border px-4 py-3 text-sm">
                        <div className="flex flex-wrap items-center justify-between gap-2"><b>{x.users} пользователей · {x.snapshots} snapshots</b><Badge intent="danger">same hash</Badge></div>
                        <div className="mt-2 text-xs opacity-70">{x.codeHash} · max code {x.maxCodeLength} · {fmtDate(x.firstSeenAt)} — {fmtDate(x.lastSeenAt)}</div>
                        <div className="mt-2 flex flex-wrap gap-2">{(x.userNames || []).map((name, idx) => <Badge key={`${name}-${idx}`} intent="outline">{name || 'Пользователь'}</Badge>)}</div>
                      </div>
                    ))}
                  </div>
                ) : <div className="text-sm opacity-60">Совпадений по hash пока нет. Для глубокой похожести нужно включить snapshots/hash в настройках задания.</div>}
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
