import React, { useEffect, useMemo, useState } from 'react';
import {
  BarChart3,
  Clock3,
  Database,
  LifeBuoy,
  RefreshCw,
  ShieldAlert,
  Sparkles,
  Users,
} from 'lucide-react';
import Layout from '../../components/Layout';
import AppErrorPanel from '../../components/AppErrorPanel';
import { Button, Card } from '../../components/ui';
import { getAdminAnalyticsOverview, getAdminAnalyticsUser } from '../../api/adminAnalytics';
import { handleApiError } from '../../utils/handleApiError';
import { useNotify } from '../../components/notify/NotifyProvider';

const PERIODS = [7, 14, 30, 90, 180, 365];

function cn(...parts) {
  return parts.filter(Boolean).join(' ');
}

function formatNumber(value) {
  return new Intl.NumberFormat('ru-RU').format(Number(value || 0));
}

function formatPercent(value) {
  return `${Number(value || 0).toFixed(1)}%`;
}

function formatMs(value) {
  return `${formatNumber(Math.round(Number(value || 0)))} мс`;
}

function formatMinutes(value) {
  const n = Number(value || 0);
  if (!n) return '—';
  if (n >= 1440) return `${(n / 1440).toFixed(1)} дн`;
  if (n >= 60) return `${(n / 60).toFixed(1)} ч`;
  return `${Math.round(n)} мин`;
}

function formatDateTime(value) {
  if (!value) return '—';
  return new Date(value).toLocaleString();
}

function MetricCard({ icon: Icon, label, value, hint, accent = 'from-brand-500/20 to-brand-300/5' }) {
  return (
    <Card className="relative overflow-hidden p-5">
      <div className={cn('pointer-events-none absolute inset-0 bg-gradient-to-br opacity-80', accent)} />
      <div className="relative flex items-start justify-between gap-4">
        <div>
          <div className="text-sm text-neutral-500 dark:text-neutral-400">{label}</div>
          <div className="mt-2 text-3xl font-semibold tracking-tight">{value}</div>
          {hint ? <div className="mt-2 text-xs text-neutral-500 dark:text-neutral-400">{hint}</div> : null}
        </div>
        {Icon ? <div className="rounded-2xl border border-white/50 bg-white/60 p-3 dark:border-white/10 dark:bg-white/5"><Icon size={20} /></div> : null}
      </div>
    </Card>
  );
}

function SectionTitle({ icon: Icon, title, subtitle, action }) {
  return (
    <div className="flex flex-wrap items-start justify-between gap-4">
      <div>
        <div className="flex items-center gap-3 text-2xl font-semibold">
          {Icon ? <Icon size={24} /> : null}
          <span>{title}</span>
        </div>
        {subtitle ? <p className="mt-2 text-sm text-neutral-500 dark:text-neutral-400 max-w-3xl">{subtitle}</p> : null}
      </div>
      {action}
    </div>
  );
}

function ChartCard({ title, subtitle, children, tall = false }) {
  return (
    <Card className={cn('p-5', tall && 'min-h-[26rem]')}>
      <div className="mb-4">
        <div className="text-base font-semibold">{title}</div>
        {subtitle ? <div className="mt-1 text-sm text-neutral-500 dark:text-neutral-400">{subtitle}</div> : null}
      </div>
      {children}
    </Card>
  );
}

function EmptyState() {
  return <div className="rounded-2xl border border-dashed border-neutral-200/70 px-4 py-10 text-center text-sm text-neutral-500 dark:border-neutral-800/70 dark:text-neutral-400">Недостаточно данных за выбранный период.</div>;
}

function LineAreaChart({ data = [], color = 'rgb(var(--brand-600))', height = 250, valueFormatter = formatNumber }) {
  if (!Array.isArray(data) || data.length === 0) return <EmptyState />;
  const values = data.map((d) => Number(d.value || 0));
  const max = Math.max(...values, 1);
  const width = 100;
  const padX = 4;
  const padY = 8;
  const innerW = width - padX * 2;
  const innerH = 100 - padY * 2;
  const pts = data.map((d, i) => {
    const x = padX + (data.length === 1 ? innerW / 2 : (i / (data.length - 1)) * innerW);
    const y = padY + innerH - (Number(d.value || 0) / max) * innerH;
    return [x, y];
  });
  const path = pts.map((p, i) => `${i === 0 ? 'M' : 'L'} ${p[0]} ${p[1]}`).join(' ');
  const area = `${path} L ${pts[pts.length - 1][0]} ${100 - padY} L ${pts[0][0]} ${100 - padY} Z`;
  const last = data[data.length - 1];
  const peak = Math.max(...values);

  return (
    <div className="space-y-4">
      <div className="grid gap-3 sm:grid-cols-3">
        <div className="rounded-2xl bg-neutral-50 px-4 py-3 dark:bg-neutral-900/60">
          <div className="text-xs text-neutral-500 dark:text-neutral-400">Последнее значение</div>
          <div className="mt-1 text-xl font-semibold">{valueFormatter(last?.value)}</div>
        </div>
        <div className="rounded-2xl bg-neutral-50 px-4 py-3 dark:bg-neutral-900/60">
          <div className="text-xs text-neutral-500 dark:text-neutral-400">Пик</div>
          <div className="mt-1 text-xl font-semibold">{valueFormatter(peak)}</div>
        </div>
        <div className="rounded-2xl bg-neutral-50 px-4 py-3 dark:bg-neutral-900/60">
          <div className="text-xs text-neutral-500 dark:text-neutral-400">Точек</div>
          <div className="mt-1 text-xl font-semibold">{formatNumber(data.length)}</div>
        </div>
      </div>
      <div className="relative overflow-hidden rounded-3xl border border-neutral-200/70 bg-[linear-gradient(to_bottom,rgba(var(--brand-500),0.08),transparent_60%)] dark:border-neutral-800/70" style={{ height }}>
        <svg viewBox="0 0 100 100" preserveAspectRatio="none" className="h-full w-full">
          {[0.25, 0.5, 0.75].map((n) => (
            <line key={n} x1="0" x2="100" y1={n * 100} y2={n * 100} stroke="rgba(148,163,184,0.18)" strokeWidth="0.5" />
          ))}
          <path d={area} fill={color} opacity="0.14" />
          <path d={path} fill="none" stroke={color} strokeWidth="2.2" strokeLinejoin="round" strokeLinecap="round" />
          {pts.map((p, i) => (
            <circle key={i} cx={p[0]} cy={p[1]} r="1.2" fill={color} opacity={i === pts.length - 1 ? 1 : 0.7} />
          ))}
        </svg>
      </div>
      <div className="flex items-center justify-between gap-3 text-xs text-neutral-500 dark:text-neutral-400">
        <span>{data[0]?.label || '—'}</span>
        <span>{data[Math.floor((data.length - 1) / 2)]?.label || '—'}</span>
        <span>{data[data.length - 1]?.label || '—'}</span>
      </div>
    </div>
  );
}

function BarChart({ data = [], color = 'rgb(var(--brand-600))', height = 260, valueFormatter = formatNumber }) {
  if (!Array.isArray(data) || data.length === 0) return <EmptyState />;
  const max = Math.max(...data.map((d) => Number(d.value || 0)), 1);
  return (
    <div className="space-y-3">
      <div className="grid gap-3">
        {data.map((item) => {
          const width = `${Math.max(4, (Number(item.value || 0) / max) * 100)}%`;
          return (
            <div key={item.label} className="space-y-1">
              <div className="flex items-center justify-between gap-3 text-sm">
                <div className="truncate text-neutral-700 dark:text-neutral-200">{item.label}</div>
                <div className="shrink-0 font-medium">{valueFormatter(item.value)}</div>
              </div>
              <div className="h-3 overflow-hidden rounded-full bg-neutral-100 dark:bg-neutral-900/80">
                <div className="h-full rounded-full" style={{ width, background: color }} />
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}

function DonutChart({ data = [], size = 220 }) {
  if (!Array.isArray(data) || data.length === 0 || data.every((x) => !Number(x.value))) return <EmptyState />;
  const total = data.reduce((sum, item) => sum + Number(item.value || 0), 0);
  const radius = 42;
  const stroke = 16;
  const circumference = 2 * Math.PI * radius;
  const palette = [
    'rgb(var(--brand-600))',
    'rgb(var(--brand-500))',
    'rgb(var(--brand-400))',
    'rgb(var(--brand-700))',
    'rgba(var(--brand-500),0.55)',
  ];
  let offset = 0;
  return (
    <div className="grid gap-6 md:grid-cols-[auto,1fr] md:items-center">
      <div className="mx-auto" style={{ width: size, height: size }}>
        <svg viewBox="0 0 120 120" className="h-full w-full -rotate-90">
          <circle cx="60" cy="60" r={radius} fill="none" stroke="rgba(148,163,184,0.15)" strokeWidth={stroke} />
          {data.map((item, idx) => {
            const value = Number(item.value || 0);
            const dash = (value / total) * circumference;
            const el = (
              <circle
                key={item.label}
                cx="60"
                cy="60"
                r={radius}
                fill="none"
                stroke={palette[idx % palette.length]}
                strokeWidth={stroke}
                strokeDasharray={`${dash} ${circumference - dash}`}
                strokeDashoffset={-offset}
                strokeLinecap="round"
              />
            );
            offset += dash;
            return el;
          })}
          <circle cx="60" cy="60" r="25" fill="rgb(var(--card))" />
          <text x="60" y="57" textAnchor="middle" className="fill-current text-[11px] font-semibold rotate-90 origin-center">{formatNumber(total)}</text>
          <text x="60" y="70" textAnchor="middle" className="fill-current text-[5px] rotate-90 origin-center">всего</text>
        </svg>
      </div>
      <div className="space-y-3">
        {data.map((item, idx) => {
          const value = Number(item.value || 0);
          return (
            <div key={item.label} className="flex items-center justify-between gap-4 rounded-2xl bg-neutral-50 px-4 py-3 dark:bg-neutral-900/60">
              <div className="flex items-center gap-3 min-w-0">
                <span className="h-3 w-3 shrink-0 rounded-full" style={{ background: palette[idx % palette.length] }} />
                <span className="truncate">{item.label}</span>
              </div>
              <div className="text-right shrink-0">
                <div className="font-semibold">{formatNumber(value)}</div>
                <div className="text-xs text-neutral-500 dark:text-neutral-400">{formatPercent(total ? (value / total) * 100 : 0)}</div>
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}

function RankedTable({ rows = [], columns = [], onRowClick, activeId }) {
  if (!Array.isArray(rows) || rows.length === 0) return <EmptyState />;
  return (
    <div className="overflow-hidden rounded-3xl border border-neutral-200/70 dark:border-neutral-800/70">
      <div className="overflow-auto">
        <table className="min-w-full text-sm">
          <thead className="bg-neutral-50/90 dark:bg-neutral-900/80">
            <tr>
              {columns.map((col) => (
                <th key={col.key} className="px-4 py-3 text-left font-medium text-neutral-500 dark:text-neutral-400">{col.label}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {rows.map((row, idx) => {
              const clickable = typeof onRowClick === 'function';
              const isActive = activeId && (row.userId === activeId || row.adminId === activeId);
              return (
                <tr
                  key={row.userId || row.assignmentId || row.label || idx}
                  className={cn('border-t border-neutral-200/60 dark:border-neutral-800/60', clickable && 'cursor-pointer hover:bg-neutral-50 dark:hover:bg-neutral-900/60', isActive && 'bg-brand-50/70 dark:bg-brand-900/10')}
                  onClick={clickable ? () => onRowClick(row) : undefined}
                >
                  {columns.map((col) => (
                    <td key={col.key} className="px-4 py-3 align-top">
                      {col.render ? col.render(row, idx) : row[col.key]}
                    </td>
                  ))}
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function UserSpotlight({ data, loading, error }) {
  if (loading) {
    return <Card className="p-6"><div className="animate-pulse text-sm text-neutral-500">Загружаю профиль активности пользователя…</div></Card>;
  }
  if (error) {
    return <AppErrorPanel error={error} title="Не удалось загрузить аналитику пользователя" />;
  }
  if (!data) {
    return (
      <Card className="p-6">
        <div className="text-sm text-neutral-500 dark:text-neutral-400">Нажми на пользователя в таблице сверху, чтобы увидеть его личную статистику: входы, запросы, решения и support-активность.</div>
      </Card>
    );
  }

  const profile = data.profile || {};
  const activity = data.activity || {};
  return (
    <div className="space-y-4">
      <Card className="p-5">
        <div className="flex flex-wrap items-start justify-between gap-4">
          <div>
            <div className="text-xs uppercase tracking-wide text-neutral-500 dark:text-neutral-400">Выбранный пользователь</div>
            <div className="mt-2 text-2xl font-semibold">{profile.fullName || 'Без имени'}</div>
            <div className="mt-1 text-sm text-neutral-500 dark:text-neutral-400">{profile.email || '—'} · роль {profile.role || 'User'}</div>
          </div>
          <div className="rounded-2xl bg-neutral-50 px-4 py-3 text-sm dark:bg-neutral-900/60">
            <div>Создан: <b>{formatDateTime(profile.createdAt)}</b></div>
            <div className="mt-1">Последний вход: <b>{formatDateTime(profile.lastLoginAt)}</b></div>
          </div>
        </div>
      </Card>

      <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-4">
        <MetricCard icon={Users} label="Входы" value={formatNumber(activity.totalLogins)} hint="За выбранный период" accent="from-brand-500/15 to-transparent" />
        <MetricCard icon={Database} label="Запросы к API" value={formatNumber(activity.totalRequests)} hint={`Ошибок: ${formatNumber(activity.errorRequests)}`} accent="from-violet-500/15 to-transparent" />
        <MetricCard icon={BarChart3} label="Code/image/test" value={`${formatNumber(activity.codeSubmits)} / ${formatNumber(activity.imageSubmits)} / ${formatNumber(activity.testAttempts)}`} hint="Отправки и попытки" accent="from-emerald-500/15 to-transparent" />
        <MetricCard icon={Clock3} label="Средняя задержка" value={formatMs(activity.avgLatencyMs)} hint={`Тикетов: ${formatNumber(activity.ticketsCreated)}`} accent="from-amber-500/15 to-transparent" />
      </div>

      <div className="grid gap-4 xl:grid-cols-2">
        <ChartCard title="Логины по дням" subtitle="Как часто пользователь действительно возвращается на сайт.">
          <LineAreaChart data={data.charts?.loginsByDay || []} />
        </ChartCard>
        <ChartCard title="Запросы по дням" subtitle="Нагрузка, которую пользователь создаёт на backend.">
          <LineAreaChart data={data.charts?.requestsByDay || []} color="rgb(var(--brand-500))" />
        </ChartCard>
      </div>

      <div className="grid gap-4 xl:grid-cols-2">
        <ChartCard title="Запросы по часам" subtitle="В какие часы пользователь чаще всего активен.">
          <BarChart data={data.charts?.requestsByHour || []} valueFormatter={formatNumber} />
        </ChartCard>
        <ChartCard title="Топ маршрутов пользователя" subtitle="Какие API он вызывает чаще всего.">
          <RankedTable
            rows={data.topPaths || []}
            columns={[
              { key: 'label', label: 'Маршрут' },
              { key: 'value', label: 'Запросов', render: (row) => formatNumber(row.value) },
              { key: 'avgLatencyMs', label: 'Средняя задержка', render: (row) => formatMs(row.avgLatencyMs) },
              { key: 'errors', label: 'Ошибки', render: (row) => formatNumber(row.errors) },
            ]}
          />
        </ChartCard>
      </div>
    </div>
  );
}

export default function AdminAnalyticsPage() {
  const notify = useNotify();
  const [days, setDays] = useState(30);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [pageError, setPageError] = useState(null);
  const [data, setData] = useState(null);
  const [selectedUser, setSelectedUser] = useState(null);
  const [userData, setUserData] = useState(null);
  const [userLoading, setUserLoading] = useState(false);
  const [userError, setUserError] = useState(null);

  const load = async (silent = false, nextDays = days) => {
    try {
      if (silent) setRefreshing(true); else setLoading(true);
      const res = await getAdminAnalyticsOverview(nextDays);
      setData(res);
      setPageError(null);
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось загрузить общую аналитику');
      setPageError(parsed);
    } finally {
      if (silent) setRefreshing(false); else setLoading(false);
    }
  };

  const loadUser = async (userId) => {
    if (!userId) return;
    try {
      setUserLoading(true);
      const res = await getAdminAnalyticsUser(userId, days);
      setUserData(res);
      setUserError(null);
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось загрузить аналитику пользователя');
      setUserError(parsed);
    } finally {
      setUserLoading(false);
    }
  };

  useEffect(() => {
    load(false, days);
  }, [days]);

  useEffect(() => {
    if (selectedUser?.userId) loadUser(selectedUser.userId);
    else {
      setUserData(null);
      setUserError(null);
    }
  }, [selectedUser?.userId, days]);

  const apiTotals = data?.api?.totals || {};
  const usersTotals = data?.users?.totals || {};
  const assignmentTotals = data?.assignments?.totals || {};
  const supportTotals = data?.support?.totals || {};

  const heroCards = useMemo(() => [
    {
      icon: Users,
      label: 'Активные пользователи',
      value: formatNumber(usersTotals.activeUsers),
      hint: `Всего пользователей: ${formatNumber(usersTotals.totalUsers)} · новых за период: ${formatNumber(usersTotals.newUsers)}`,
      accent: 'from-brand-500/20 to-transparent',
    },
    {
      icon: Database,
      label: 'Запросы к backend',
      value: formatNumber(apiTotals.totalRequests),
      hint: `4xx: ${formatNumber(apiTotals.errors4xx)} · 5xx: ${formatNumber(apiTotals.errors5xx)}`,
      accent: 'from-violet-500/20 to-transparent',
    },
    {
      icon: BarChart3,
      label: 'Попытки по заданиям',
      value: formatNumber(assignmentTotals.totalAttempts),
      hint: `Успешность: ${formatPercent(assignmentTotals.successRate)} · code/test/image: ${formatNumber(assignmentTotals.codeAttempts)} / ${formatNumber(assignmentTotals.testAttempts)} / ${formatNumber(assignmentTotals.imageAttempts)}`,
      accent: 'from-emerald-500/20 to-transparent',
    },
    {
      icon: LifeBuoy,
      label: 'Support-тикеты',
      value: formatNumber(supportTotals.totalTickets),
      hint: `Открыто: ${formatNumber(supportTotals.openTickets)} · средний первый ответ: ${formatMinutes(supportTotals.avgFirstResponseMinutes)}`,
      accent: 'from-amber-500/20 to-transparent',
    },
  ], [usersTotals, apiTotals, assignmentTotals, supportTotals]);

  return (
    <Layout>
      <div className="space-y-8">
        <SectionTitle
          icon={Sparkles}
          title="Аналитика платформы"
          subtitle="Большой админский дашборд по пользователям, backend API, заданиям и support. Здесь уже настоящие графики по реальным данным, а не декоративные заглушки."
          action={
            <div className="flex flex-wrap items-center gap-2">
              <div className="flex flex-wrap gap-2 rounded-2xl border border-neutral-200/70 bg-white/70 p-1 dark:border-neutral-800/70 dark:bg-neutral-950/50">
                {PERIODS.map((value) => (
                  <button
                    key={value}
                    type="button"
                    onClick={() => setDays(value)}
                    className={cn(
                      'rounded-xl px-3 py-2 text-sm transition',
                      days === value ? 'bg-[rgb(var(--card))] shadow-soft font-medium' : 'text-neutral-500 hover:text-neutral-900 dark:text-neutral-400 dark:hover:text-neutral-100'
                    )}
                  >
                    {value} дн
                  </button>
                ))}
              </div>
              <Button variant="outline" onClick={() => load(true)} disabled={refreshing || loading}>
                <RefreshCw size={16} className={cn(refreshing && 'animate-spin')} />
                <span className="ml-2">Обновить</span>
              </Button>
            </div>
          }
        />

        {pageError ? <AppErrorPanel error={pageError} title="Не удалось загрузить аналитику" /> : null}

        <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-4">
          {heroCards.map((card) => <MetricCard key={card.label} {...card} />)}
        </div>

        {loading ? (
          <div className="grid gap-4 lg:grid-cols-2">
            {[0, 1, 2, 3].map((i) => <Card key={i} className="h-64 animate-pulse bg-neutral-100/80 dark:bg-neutral-900/70" />)}
          </div>
        ) : data ? (
          <>
            <div className="grid gap-4 xl:grid-cols-2">
              <ChartCard title="Входы по дням" subtitle="Позволяет видеть общий ритм посещаемости и всплески активности.">
                <LineAreaChart data={data.users?.loginsByDay || []} />
              </ChartCard>
              <ChartCard title="Уникальные пользователи по дням" subtitle="Кто реально возвращается, а не просто суммарное число входов.">
                <LineAreaChart data={data.users?.uniqueUsersByDay || []} color="rgb(var(--brand-500))" />
              </ChartCard>
            </div>

            <div className="grid gap-4 xl:grid-cols-[1.2fr,0.8fr]">
              <ChartCard title="Пользовательская активность по часам" subtitle="Показывает, когда аудитория чаще всего авторизуется на сайте.">
                <BarChart data={data.users?.loginsByHour || []} />
              </ChartCard>
              <ChartCard title="Распределение ролей" subtitle="Кто вообще живёт в системе: обычные пользователи, редакторы, админы.">
                <DonutChart data={data.users?.roleDistribution || []} />
              </ChartCard>
            </div>

            <div className="grid gap-4 xl:grid-cols-[1.15fr,0.85fr]">
              <ChartCard title="Топ пользователей по входам" subtitle="Нажми на строку, чтобы открыть личную статистику пользователя." tall>
                <RankedTable
                  rows={data.users?.topUsers || []}
                  activeId={selectedUser?.userId}
                  onRowClick={(row) => setSelectedUser(row)}
                  columns={[
                    { key: 'fullName', label: 'Пользователь', render: (row) => <div><div className="font-medium">{row.fullName}</div><div className="text-xs text-neutral-500 dark:text-neutral-400">{row.email || '—'} · {row.role || 'User'}</div></div> },
                    { key: 'value', label: 'Входов', render: (row) => formatNumber(row.value) },
                    { key: 'activeDays', label: 'Активных дней', render: (row) => formatNumber(row.activeDays) },
                    { key: 'lastLoginAt', label: 'Последний вход', render: (row) => formatDateTime(row.lastLoginAt) },
                  ]}
                />
              </ChartCard>
              <UserSpotlight data={userData} loading={userLoading} error={userError} />
            </div>

            <div className="grid gap-4 xl:grid-cols-2">
              <ChartCard title="Запросы к backend по дням" subtitle="Общая нагрузка на API. Полезно видеть пики и просадки.">
                <LineAreaChart data={data.api?.requestsByDay || []} color="rgb(var(--brand-700))" />
              </ChartCard>
              <ChartCard title="Ошибки API по дням" subtitle="Сколько запросов завершались 4xx/5xx за выбранный период.">
                <LineAreaChart data={data.api?.errorsByDay || []} color="rgb(239,68,68)" />
              </ChartCard>
            </div>

            <div className="grid gap-4 xl:grid-cols-[1.1fr,0.9fr]">
              <ChartCard title="Latency backend по дням" subtitle={`Средняя задержка: ${formatMs(apiTotals.avgLatencyMs)} · p95: ${formatMs(apiTotals.p95LatencyMs)} · p99: ${formatMs(apiTotals.p99LatencyMs)}`}>
                <LineAreaChart data={data.api?.latencyByDay || []} color="rgb(168,85,247)" valueFormatter={formatMs} />
              </ChartCard>
              <ChartCard title="Типы клиентов" subtitle="Кто именно нагружает backend: сайт, админка, внутренние клиенты, ручные запросы.">
                <DonutChart data={data.api?.clientTypes || []} />
              </ChartCard>
            </div>

            <div className="grid gap-4 xl:grid-cols-2">
              <ChartCard title="Самые вызываемые endpoint’ы" subtitle="Маршруты, которые чаще всего бьют в backend.">
                <RankedTable
                  rows={data.api?.topEndpoints || []}
                  columns={[
                    { key: 'label', label: 'Маршрут' },
                    { key: 'value', label: 'Запросов', render: (row) => formatNumber(row.value) },
                    { key: 'avgLatencyMs', label: 'Средняя задержка', render: (row) => formatMs(row.avgLatencyMs) },
                    { key: 'errorRate', label: 'Ошибка %', render: (row) => formatPercent(row.errorRate) },
                  ]}
                />
              </ChartCard>
              <ChartCard title="Кто чаще всего стучится в backend" subtitle="Здесь быстро видно самых активных пользователей по API-вызовам.">
                <RankedTable
                  rows={data.api?.topUsers || []}
                  activeId={selectedUser?.userId}
                  onRowClick={(row) => setSelectedUser(row)}
                  columns={[
                    { key: 'fullName', label: 'Пользователь', render: (row) => <div><div className="font-medium">{row.fullName}</div><div className="text-xs text-neutral-500 dark:text-neutral-400">{row.email || '—'}</div></div> },
                    { key: 'value', label: 'Запросов', render: (row) => formatNumber(row.value) },
                    { key: 'errors', label: 'Ошибок', render: (row) => formatNumber(row.errors) },
                    { key: 'avgLatencyMs', label: 'Средняя задержка', render: (row) => formatMs(row.avgLatencyMs) },
                  ]}
                />
              </ChartCard>
            </div>

            <div className="grid gap-4 xl:grid-cols-2">
              <ChartCard title="Попытки по заданиям" subtitle="Сколько действий по code/test/image вообще было за период.">
                <LineAreaChart data={data.assignments?.attemptsByDay || []} color="rgb(var(--brand-600))" />
              </ChartCard>
              <ChartCard title="Успешные попытки по дням" subtitle={`Общая успешность: ${formatPercent(assignmentTotals.successRate)} · средний score тестов: ${formatPercent(assignmentTotals.avgTestScore)}`}>
                <LineAreaChart data={data.assignments?.successByDay || []} color="rgb(34,197,94)" />
              </ChartCard>
            </div>

            <div className="grid gap-4 xl:grid-cols-[0.95fr,1.05fr]">
              <ChartCard title="Типы активностей в заданиях" subtitle="Каких попыток больше: code, image или test.">
                <DonutChart data={data.assignments?.types || []} />
              </ChartCard>
              <ChartCard title="Топ языков решений" subtitle="Какие языки реально используют чаще всего.">
                <BarChart data={data.assignments?.languages || []} />
              </ChartCard>
            </div>

            <div className="grid gap-4 xl:grid-cols-2">
              <ChartCard title="Самые активные задания" subtitle="У каких заданий больше всего попыток. Это хороший индикатор интереса или боли.">
                <RankedTable
                  rows={data.assignments?.topAssignments || []}
                  columns={[
                    { key: 'title', label: 'Задание', render: (row) => <div><div className="font-medium">{row.title}</div><div className="text-xs text-neutral-500 dark:text-neutral-400">{row.type} · diff {row.difficulty} · rating {row.rating}</div></div> },
                    { key: 'attempts', label: 'Попыток', render: (row) => formatNumber(row.attempts) },
                    { key: 'passed', label: 'Успешных', render: (row) => formatNumber(row.passed) },
                    { key: 'successRate', label: 'Успешность', render: (row) => formatPercent(row.successRate) },
                  ]}
                />
              </ChartCard>
              <ChartCard title="Самые проблемные задания" subtitle="Задания с низкой успешностью и достаточным числом попыток.">
                <RankedTable
                  rows={data.assignments?.hardAssignments || []}
                  columns={[
                    { key: 'title', label: 'Задание', render: (row) => <div><div className="font-medium">{row.title}</div><div className="text-xs text-neutral-500 dark:text-neutral-400">{row.type}</div></div> },
                    { key: 'attempts', label: 'Попыток', render: (row) => formatNumber(row.attempts) },
                    { key: 'successRate', label: 'Успешность', render: (row) => <span className="text-rose-600 dark:text-rose-400">{formatPercent(row.successRate)}</span> },
                    { key: 'rating', label: 'Рейтинг', render: (row) => formatNumber(row.rating) },
                  ]}
                />
              </ChartCard>
            </div>

            <div className="grid gap-4 xl:grid-cols-2">
              <ChartCard title="Новые support-тикеты по дням" subtitle="Видно нагрузку на поддержку и всплески обращений.">
                <LineAreaChart data={data.support?.ticketsByDay || []} color="rgb(245,158,11)" />
              </ChartCard>
              <ChartCard title="Закрытые тикеты по дням" subtitle={`Средний первый ответ: ${formatMinutes(supportTotals.avgFirstResponseMinutes)} · среднее закрытие: ${formatMinutes(supportTotals.avgCloseMinutes)}`}>
                <LineAreaChart data={data.support?.closedByDay || []} color="rgb(34,197,94)" />
              </ChartCard>
            </div>

            <div className="grid gap-4 xl:grid-cols-[0.9fr,1.1fr]">
              <ChartCard title="Типы обращений" subtitle="Что люди приносят чаще: баги, вопросы, предложения и т.д.">
                <DonutChart data={data.support?.ticketTypes || []} />
              </ChartCard>
              <ChartCard title="Самые активные админы поддержки" subtitle="Кто чаще всего отвечает в support-системе.">
                <RankedTable
                  rows={data.support?.topAdmins || []}
                  columns={[
                    { key: 'label', label: 'Админ', render: (row) => <div><div className="font-medium">{row.label}</div><div className="text-xs text-neutral-500 dark:text-neutral-400">{row.email || '—'}</div></div> },
                    { key: 'value', label: 'Сообщений', render: (row) => formatNumber(row.value) },
                  ]}
                />
              </ChartCard>
            </div>
          </>
        ) : null}

        <Card className="border border-brand-200/60 bg-[linear-gradient(135deg,rgba(var(--brand-500),0.08),transparent_75%)] p-5 dark:border-brand-900/20">
          <div className="flex flex-wrap items-start gap-3">
            <ShieldAlert size={20} className="mt-0.5" />
            <div className="min-w-0 flex-1 text-sm text-neutral-700 dark:text-neutral-200">
              <div className="font-semibold">Важно про миграции</div>
              <div className="mt-1 text-neutral-600 dark:text-neutral-400">
                Я не добавлял в архив готовую EF-миграцию. В коде уже есть новая сущность RequestLog и DbContext-настройка, но саму миграцию ты снимешь у себя командами, как и просил.
              </div>
            </div>
          </div>
        </Card>
      </div>
    </Layout>
  );
}
