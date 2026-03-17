import React, { useEffect, useMemo, useState } from 'react';
import {
  BarChart3,
  Clock3,
  Database,
  LifeBuoy,
  RefreshCw,
  Sparkles,
  Users,
  Search,
  TrendingUp,
  TrendingDown,
  ShieldAlert,
} from 'lucide-react';
import Layout from '../../components/Layout';
import AppErrorPanel from '../../components/AppErrorPanel';
import { Button, Card } from '../../components/ui';
import { getAdminAnalyticsOverview, getAdminAnalyticsUser, searchAdminAnalyticsUsers } from '../../api/adminAnalytics';
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

function formatSignedPercent(value) {
  const n = Number(value || 0);
  return `${n > 0 ? '+' : ''}${n.toFixed(1)}%`;
}

function formatComparisonValue(item) {
  if (!item) return '0';
  if (item.percentMetric) return `${Number(item.current || 0).toFixed(1)}%`;
  return `${formatNumber(item.current)}${item.unit ? ` ${item.unit}` : ''}`.trim();
}

function ComparisonCard({ item }) {
  if (!item) return null;
  const delta = Number(item.deltaPercent || 0);
  const up = delta >= 0;
  const toneClass = up
    ? 'border-[rgba(var(--accent)/0.35)] bg-[rgba(var(--accent)/0.08)] text-[rgb(var(--accent))]'
    : 'border-[rgba(var(--accent3)/0.35)] bg-[rgba(var(--accent3)/0.08)] text-[rgb(var(--accent3))]';
  const Icon = up ? TrendingUp : TrendingDown;
  return (
    <Card className="p-5">
      <div className="flex items-start justify-between gap-4">
        <div>
          <div className="text-sm text-muted-foreground">{item.label}</div>
          <div className="mt-2 text-2xl font-semibold tracking-tight">{formatComparisonValue(item)}</div>
          <div className="mt-2 text-xs text-muted-foreground">Предыдущий период: {item.percentMetric ? `${Number(item.previous || 0).toFixed(1)}%` : formatNumber(item.previous)}{!item.percentMetric && item.unit ? ` ${item.unit}` : ''}</div>
        </div>
        <div className={cn('inline-flex items-center gap-2 rounded-2xl border px-3 py-2 text-sm font-medium', toneClass)}>
          <Icon size={16} />
          <span>{formatSignedPercent(delta)}</span>
        </div>
      </div>
    </Card>
  );
}

function SignalsBoard({ alerts = [] }) {
  if (!Array.isArray(alerts) || alerts.length === 0) return null;
  return (
    <Card className="p-5">
      <div className="mb-4 flex items-center gap-3 text-base font-semibold">
        <ShieldAlert size={18} />
        <span>Сигналы платформы</span>
      </div>
      <div className="grid gap-3 lg:grid-cols-2">
        {alerts.map((alert, idx) => {
          const severity = alert?.severity || 'medium';
          const toneClass = severity === 'high'
            ? 'border-[rgba(var(--accent3)/0.35)] bg-[rgba(var(--accent3)/0.07)]'
            : severity === 'good'
              ? 'border-[rgba(var(--accent)/0.35)] bg-[rgba(var(--accent)/0.07)]'
              : 'border-[rgba(var(--accent2)/0.35)] bg-[rgba(var(--accent2)/0.07)]';
          return (
            <div key={`${alert?.title || 'signal'}-${idx}`} className={cn('rounded-2xl border px-4 py-3', toneClass)}>
              <div className="text-sm font-semibold">{alert?.title || 'Сигнал'}</div>
              <div className="mt-1 text-sm text-muted-foreground">{alert?.message}</div>
            </div>
          );
        })}
      </div>
    </Card>
  );
}

function MetricCard({ icon: Icon, label, value, hint }) {
  return (
    <Card className="p-5">
      <div className="flex items-start justify-between gap-4">
        <div>
          <div className="text-sm text-muted-foreground">{label}</div>
          <div className="mt-2 text-3xl font-semibold tracking-tight">{value}</div>
          {hint ? <div className="mt-2 text-xs text-muted-foreground">{hint}</div> : null}
        </div>
        {Icon ? <div className="rounded-2xl border border-[rgba(var(--border)/0.45)] bg-[rgba(var(--muted)/0.55)] p-3"><Icon size={20} /></div> : null}
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
        {subtitle ? <p className="mt-2 text-sm text-muted-foreground max-w-3xl">{subtitle}</p> : null}
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
        {subtitle ? <div className="mt-1 text-sm text-muted-foreground">{subtitle}</div> : null}
      </div>
      {children}
    </Card>
  );
}

function EmptyState() {
  return <div className="rounded-2xl border border-dashed border-[rgba(var(--border)/0.55)] bg-[rgba(var(--muted)/0.26)] px-4 py-10 text-center text-sm text-muted-foreground">Недостаточно данных за выбранный период.</div>;
}

function LineAreaChart({ data = [], color = 'rgb(var(--accent))', height = 250, valueFormatter = formatNumber }) {
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
  const dense = data.length > 90;
  const veryDense = data.length > 180;
  const showDots = data.length <= 18;
  const strokeWidth = veryDense ? 0.14 : dense ? 0.22 : data.length > 30 ? 0.34 : 0.48;
  const areaOpacity = veryDense ? 0.012 : dense ? 0.02 : 0.03;

  return (
    <div className="space-y-4">
      <div className="grid gap-3 sm:grid-cols-3">
        <div className="rounded-2xl border border-[rgba(var(--border)/0.45)] bg-[rgba(var(--muted)/0.38)] px-4 py-3">
          <div className="text-xs text-muted-foreground">Последнее значение</div>
          <div className="mt-1 text-xl font-semibold">{valueFormatter(last?.value)}</div>
        </div>
        <div className="rounded-2xl border border-[rgba(var(--border)/0.45)] bg-[rgba(var(--muted)/0.38)] px-4 py-3">
          <div className="text-xs text-muted-foreground">Пик</div>
          <div className="mt-1 text-xl font-semibold">{valueFormatter(peak)}</div>
        </div>
        <div className="rounded-2xl border border-[rgba(var(--border)/0.45)] bg-[rgba(var(--muted)/0.38)] px-4 py-3">
          <div className="text-xs text-muted-foreground">Точек</div>
          <div className="mt-1 text-xl font-semibold">{formatNumber(data.length)}</div>
        </div>
      </div>
      <div className="relative overflow-hidden rounded-3xl border border-[rgba(var(--border)/0.55)] bg-[rgba(var(--muted)/0.2)]" style={{ height }}>
        <svg viewBox="0 0 100 100" preserveAspectRatio="none" className="h-full w-full">
          {[0.25, 0.5, 0.75].map((n) => (
            <line key={n} x1="0" x2="100" y1={n * 100} y2={n * 100} stroke="rgba(var(--border),0.38)" strokeWidth="0.35" />
          ))}
          <path d={area} fill={color} opacity={areaOpacity} />
          <path d={path} fill="none" stroke={color} strokeWidth={strokeWidth} strokeLinejoin="miter" strokeLinecap="butt" vectorEffect="non-scaling-stroke" />
          {showDots ? pts.map((p, i) => (
            <circle key={i} cx={p[0]} cy={p[1]} r={i === pts.length - 1 ? 0.7 : 0.45} fill={color} opacity={i === pts.length - 1 ? 1 : 0.65} />
          )) : null}
        </svg>
      </div>
      <div className="flex items-center justify-between gap-3 text-xs text-muted-foreground">
        <span>{data[0]?.label || '—'}</span>
        <span>{data[Math.floor((data.length - 1) / 2)]?.label || '—'}</span>
        <span>{data[data.length - 1]?.label || '—'}</span>
      </div>
    </div>
  );
}

function BarChart({ data = [], color = 'rgb(var(--accent))', height = 260, valueFormatter = formatNumber }) {
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
                <div className="truncate text-foreground">{item.label}</div>
                <div className="shrink-0 font-medium">{valueFormatter(item.value)}</div>
              </div>
              <div className="h-3 overflow-hidden rounded-full bg-[rgba(var(--border)/0.18)]">
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
  const stroke = 13;
  const circumference = 2 * Math.PI * radius;
  const palette = [
    'rgb(var(--accent))',
    'rgb(var(--accent-700))',
    'rgb(var(--accent2))',
    'rgb(var(--accent3))',
    'rgba(var(--accent),0.45)',
  ];
  let offset = 0;
  return (
    <div className="grid gap-6 md:grid-cols-[auto,1fr] md:items-center">
      <div className="mx-auto" style={{ width: size, height: size }}>
        <svg viewBox="0 0 120 120" className="h-full w-full -rotate-90">
          <circle cx="60" cy="60" r={radius} fill="none" stroke="rgba(var(--border),0.32)" strokeWidth={stroke} />
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
                strokeLinecap="butt"
              />
            );
            offset += dash;
            return el;
          })}
          <circle cx="60" cy="60" r="28" fill="rgb(var(--card))" stroke="rgba(var(--border),0.4)" strokeWidth="1" />
          <text x="60" y="57" textAnchor="middle" className="fill-[rgb(var(--foreground))] text-[11px] font-semibold rotate-90 origin-center">{formatNumber(total)}</text>
          <text x="60" y="70" textAnchor="middle" className="fill-[rgb(var(--muted-foreground))] text-[5px] rotate-90 origin-center">всего</text>
        </svg>
      </div>
      <div className="space-y-3">
        {data.map((item, idx) => {
          const value = Number(item.value || 0);
          return (
            <div key={item.label} className="flex items-center justify-between gap-4 rounded-2xl border border-[rgba(var(--border)/0.45)] bg-[rgba(var(--muted)/0.34)] px-4 py-3">
              <div className="flex items-center gap-3 min-w-0">
                <span className="h-3 w-3 shrink-0 rounded-full" style={{ background: palette[idx % palette.length] }} />
                <span className="truncate">{item.label}</span>
              </div>
              <div className="text-right shrink-0">
                <div className="font-semibold">{formatNumber(value)}</div>
                <div className="text-xs text-muted-foreground">{formatPercent(total ? (value / total) * 100 : 0)}</div>
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}

function RankedTable({ rows = [], columns = [], onRowClick, activeId, searchValue = '', onSearchChange, searchPlaceholder = 'Поиск...' }) {
  const normalized = (searchValue || '').trim().toLowerCase();
  const filteredRows = !normalized
    ? rows
    : rows.filter((row) =>
        JSON.stringify(row || {}).toLowerCase().includes(normalized)
      );
  const clickable = typeof onRowClick === 'function';

  return (
    <div className="space-y-4">
      {typeof onSearchChange === 'function' ? (
        <div className="max-w-md">
          <input
            type="search"
            value={searchValue}
            onChange={(e) => onSearchChange(e.target.value)}
            placeholder={searchPlaceholder}
            className="w-full rounded-2xl border border-[rgba(var(--border)/0.65)] bg-[rgba(var(--muted)/0.28)] px-4 py-3 text-sm outline-none transition placeholder:text-muted-foreground/70 focus:border-[rgb(var(--accent))] focus:bg-[rgb(var(--card))]"
          />
        </div>
      ) : null}
      <div className="overflow-hidden rounded-3xl border border-[rgba(var(--border)/0.55)]">
        <table className="w-full table-fixed">
          <thead className="bg-[rgba(var(--muted)/0.38)]">
            <tr>
              {columns.map((col, idx) => (
                <th
                  key={col.key}
                  className={cn(
                    'px-4 py-3 text-left text-sm font-semibold text-muted-foreground',
                    idx === 0 ? 'w-[40%]' : 'w-[20%]'
                  )}
                >
                  {col.label}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {filteredRows.length === 0 ? (
              <tr>
                <td colSpan={columns.length} className="px-4 py-8 text-center text-sm text-muted-foreground">Ничего не найдено.</td>
              </tr>
            ) : filteredRows.map((row, idx) => {
              const isActive = activeId && (row.userId === activeId || row.id === activeId);
              return (
                <tr
                  key={row.userId || row.assignmentId || row.label || idx}
                  className={cn('border-t border-[rgba(var(--border)/0.45)]', clickable && 'cursor-pointer hover:bg-[rgba(var(--muted)/0.26)]', isActive && 'bg-[rgba(var(--muted)/0.34)]')}
                  onClick={clickable ? () => onRowClick(row) : undefined}
                >
                  {columns.map((col) => (
                    <td key={col.key} className="px-4 py-3 align-top text-sm break-words text-foreground">
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
    return <Card className="p-6"><div className="animate-pulse text-sm text-muted-foreground">Загружаю профиль активности пользователя…</div></Card>;
  }
  if (error) {
    return <AppErrorPanel error={error} title="Не удалось загрузить аналитику пользователя" />;
  }
  if (!data) {
    return (
      <Card className="p-6">
        <div className="text-sm text-muted-foreground">Нажми на пользователя в таблице сверху, чтобы увидеть его личную статистику: входы, запросы, решения и support-активность.</div>
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
            <div className="text-xs uppercase tracking-wide text-muted-foreground">Выбранный пользователь</div>
            <div className="mt-2 text-2xl font-semibold">{profile.fullName || 'Без имени'}</div>
            <div className="mt-1 text-sm text-muted-foreground">{profile.email || '—'} · роль {profile.role || 'User'}</div>
          </div>
          <div className="rounded-2xl border border-[rgba(var(--border)/0.45)] bg-[rgba(var(--muted)/0.34)] px-4 py-3 text-sm">
            <div>Создан: <b>{formatDateTime(profile.createdAt)}</b></div>
            <div className="mt-1">Последний вход: <b>{formatDateTime(profile.lastLoginAt)}</b></div>
          </div>
        </div>
      </Card>

      <div className="grid gap-4 md:grid-cols-2 2xl:grid-cols-4">
        <MetricCard icon={Users} label="Входы" value={formatNumber(activity.totalLogins)} hint="За выбранный период" />
        <MetricCard icon={Database} label="Запросы к API" value={formatNumber(activity.totalRequests)} hint={`Ошибок: ${formatNumber(activity.errorRequests)}`} />
        <MetricCard icon={BarChart3} label="Code/image/test" value={`${formatNumber(activity.codeSubmits)} / ${formatNumber(activity.imageSubmits)} / ${formatNumber(activity.testAttempts)}`} hint="Отправки и попытки" />
        <MetricCard icon={Clock3} label="Средняя задержка" value={formatMs(activity.avgLatencyMs)} hint={`Тикетов: ${formatNumber(activity.ticketsCreated)}`} />
      </div>

      <div className="grid gap-4 xl:grid-cols-2">
        <ChartCard title="Логины по дням" subtitle="Как часто пользователь действительно возвращается на сайт.">
          <LineAreaChart data={data.charts?.loginsByDay || []} />
        </ChartCard>
        <ChartCard title="Запросы по дням" subtitle="Нагрузка, которую пользователь создаёт на backend.">
          <LineAreaChart data={data.charts?.requestsByDay || []} color="rgb(var(--accent-700))" />
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
              { key: 'label', label: 'Маршрут', render: (row) => <span className="block break-all text-sm">{row.label}</span> },
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
  const [userSearch, setUserSearch] = useState('');
  const [globalUserSearch, setGlobalUserSearch] = useState('');
  const [globalUserResults, setGlobalUserResults] = useState([]);
  const [globalUserLoading, setGlobalUserLoading] = useState(false);

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

  useEffect(() => {
    const q = globalUserSearch.trim();
    if (q.length < 2) {
      setGlobalUserResults([]);
      setGlobalUserLoading(false);
      return undefined;
    }
    const handle = setTimeout(async () => {
      try {
        setGlobalUserLoading(true);
        const res = await searchAdminAnalyticsUsers(q, 8);
        setGlobalUserResults(Array.isArray(res) ? res : []);
      } catch {
        setGlobalUserResults([]);
      } finally {
        setGlobalUserLoading(false);
      }
    }, 250);
    return () => clearTimeout(handle);
  }, [globalUserSearch]);

  const apiTotals = data?.api?.totals || {};
  const usersTotals = data?.users?.totals || {};
  const assignmentTotals = data?.assignments?.totals || {};
  const supportTotals = data?.support?.totals || {};
  const executive = data?.executive || {};

  const heroCards = useMemo(() => [
    {
      icon: Users,
      label: 'Активные пользователи',
      value: formatNumber(usersTotals.activeUsers),
      hint: `Всего пользователей: ${formatNumber(usersTotals.totalUsers)} · новых за период: ${formatNumber(usersTotals.newUsers)}`,
    },
    {
      icon: Database,
      label: 'Запросы к backend',
      value: formatNumber(apiTotals.totalRequests),
      hint: `4xx: ${formatNumber(apiTotals.errors4xx)} · 5xx: ${formatNumber(apiTotals.errors5xx)}`,
    },
    {
      icon: BarChart3,
      label: 'Попытки по заданиям',
      value: formatNumber(assignmentTotals.totalAttempts),
      hint: `Успешность: ${formatPercent(assignmentTotals.successRate)} · code/test/image: ${formatNumber(assignmentTotals.codeAttempts)} / ${formatNumber(assignmentTotals.testAttempts)} / ${formatNumber(assignmentTotals.imageAttempts)}`,
    },
    {
      icon: LifeBuoy,
      label: 'Support-тикеты',
      value: formatNumber(supportTotals.totalTickets),
      hint: `Открыто: ${formatNumber(supportTotals.openTickets)} · средний первый ответ: ${formatMinutes(supportTotals.avgFirstResponseMinutes)}`,
    },
  ], [usersTotals, apiTotals, assignmentTotals, supportTotals]);

  return (
    <Layout>
      <div className="space-y-8">
        <SectionTitle
          icon={Sparkles}
          title="Аналитика платформы"
          subtitle="Большой админский дашборд по пользователям, backend API, заданиям и support. Здесь собрана живая административная аналитика по реальным данным платформы."
          action={
            <div className="flex flex-wrap items-center gap-2">
              <div className="flex flex-wrap gap-2 rounded-2xl border border-[rgba(var(--border)/0.55)] bg-[rgba(var(--muted)/0.3)] p-1">
                {PERIODS.map((value) => (
                  <button
                    key={value}
                    type="button"
                    onClick={() => setDays(value)}
                    className={cn(
                      'rounded-xl px-3 py-2 text-sm transition',
                      days === value ? 'bg-[rgba(var(--card)/0.96)] shadow-soft font-medium border border-[rgba(var(--border)/0.45)]' : 'text-muted-foreground hover:text-foreground dark:hover:text-foreground'
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

        <div className="grid gap-4 md:grid-cols-2 2xl:grid-cols-4">
          {heroCards.map((card) => <MetricCard key={card.label} {...card} />)}
        </div>

        {!loading && data ? (
          <>
            <div className="grid gap-4 md:grid-cols-2 2xl:grid-cols-4">
              {(executive.comparisons || []).map((item) => <ComparisonCard key={item.label} item={item} />)}
            </div>
            <SignalsBoard alerts={executive.alerts || []} />
            <div className="grid gap-4 xl:grid-cols-3">
              <ChartCard title="Потенциально шумные пользователи" subtitle="Кто создаёт больше всего API-нагрузки за период. По клику можно открыть личную аналитику.">
                <RankedTable
                  rows={executive.noisyUsers || []}
                  activeId={selectedUser?.userId}
                  onRowClick={(row) => setSelectedUser(row)}
                  columns={[
                    { key: 'fullName', label: 'Пользователь', render: (row) => <div><div className="font-medium">{row.fullName}</div><div className="text-xs text-muted-foreground">{row.email || '—'}</div></div> },
                    { key: 'value', label: 'Запросов', render: (row) => formatNumber(row.value) },
                    { key: 'errorRate', label: 'Ошибка %', render: (row) => formatPercent(row.errorRate) },
                  ]}
                />
              </ChartCard>
              <ChartCard title="Самые медленные маршруты" subtitle="Топ тяжёлых endpoint’ов за выбранный период.">
                <RankedTable
                  rows={executive.slowEndpoints || []}
                  columns={[
                    { key: 'label', label: 'Маршрут', render: (row) => <span className="block break-all text-sm">{row.label}</span> },
                    { key: 'value', label: 'Ср. задержка', render: (row) => formatMs(row.value) },
                    { key: 'requests', label: 'Запросов', render: (row) => formatNumber(row.requests) },
                  ]}
                />
              </ChartCard>
              <ChartCard title="Задания в зоне риска" subtitle="Что чаще всего проваливают при достаточном числе попыток.">
                <RankedTable
                  rows={executive.failingAssignments || []}
                  columns={[
                    { key: 'title', label: 'Задание', render: (row) => <div><div className="font-medium">{row.title}</div><div className="text-xs text-muted-foreground">{row.type}</div></div> },
                    { key: 'attempts', label: 'Попыток', render: (row) => formatNumber(row.attempts) },
                    { key: 'successRate', label: 'Успешность', render: (row) => formatPercent(row.successRate) },
                  ]}
                />
              </ChartCard>
            </div>
          </>
        ) : null}

        {loading ? (
          <div className="grid gap-4 lg:grid-cols-2">
            {[0, 1, 2, 3].map((i) => <Card key={i} className="h-64 animate-pulse bg-[rgba(var(--muted)/0.35)]" />)}
          </div>
        ) : data ? (
          <>
            <div className="grid gap-4 xl:grid-cols-2">
              <ChartCard title="Входы по дням" subtitle="Позволяет видеть общий ритм посещаемости и всплески активности.">
                <LineAreaChart data={data.users?.loginsByDay || []} />
              </ChartCard>
              <ChartCard title="Уникальные пользователи по дням" subtitle="Кто реально возвращается, а не просто суммарное число входов.">
                <LineAreaChart data={data.users?.uniqueUsersByDay || []} color="rgb(var(--accent-700))" />
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

            <div className="space-y-4">
              <ChartCard title="Топ пользователей по входам" subtitle="Нажми на строку, чтобы открыть личную статистику пользователя." tall>
                <div className="mb-5 space-y-3">
                  <div className="max-w-xl">
                    <label className="mb-2 block text-sm font-medium text-muted-foreground">Найти любого пользователя по всей базе</label>
                    <div className="relative">
                      <Search size={16} className="pointer-events-none absolute left-4 top-1/2 -translate-y-1/2 text-muted-foreground/70" />
                      <input
                        type="search"
                        value={globalUserSearch}
                        onChange={(e) => setGlobalUserSearch(e.target.value)}
                        placeholder="Имя, почта или роль"
                        className="w-full rounded-2xl border border-[rgba(var(--border)/0.65)] bg-[rgba(var(--muted)/0.28)] py-3 pl-11 pr-4 text-sm outline-none transition placeholder:text-muted-foreground/70 focus:border-[rgb(var(--accent))] focus:bg-[rgb(var(--card))]"
                      />
                    </div>
                  </div>
                  {globalUserSearch.trim().length >= 2 ? (
                    <div className="rounded-2xl border border-[rgba(var(--border)/0.45)] bg-[rgba(var(--muted)/0.22)] p-2">
                      {globalUserLoading ? (
                        <div className="px-3 py-4 text-sm text-muted-foreground">Ищу пользователей…</div>
                      ) : globalUserResults.length === 0 ? (
                        <div className="px-3 py-4 text-sm text-muted-foreground">Ничего не найдено по всей базе пользователей.</div>
                      ) : (
                        <div className="space-y-1">
                          {globalUserResults.map((row) => (
                            <button
                              key={row.userId}
                              type="button"
                              onClick={() => {
                                setSelectedUser(row);
                                setGlobalUserSearch('');
                                setGlobalUserResults([]);
                              }}
                              className="flex w-full items-center justify-between gap-3 rounded-2xl px-3 py-3 text-left transition hover:bg-[rgba(var(--muted)/0.32)]"
                            >
                              <div className="min-w-0">
                                <div className="truncate font-medium">{row.fullName}</div>
                                <div className="truncate text-xs text-muted-foreground">{row.email || '—'} · {row.role || 'User'}</div>
                              </div>
                              <div className="shrink-0 text-xs text-muted-foreground">{formatDateTime(row.lastLoginAt)}</div>
                            </button>
                          ))}
                        </div>
                      )}
                    </div>
                  ) : null}
                </div>
                <RankedTable
                  rows={data.users?.topUsers || []}
                  activeId={selectedUser?.userId}
                  onRowClick={(row) => setSelectedUser(row)}
                  searchValue={userSearch}
                  onSearchChange={setUserSearch}
                  searchPlaceholder="Поиск по имени, почте или роли"
                  columns={[
                    { key: 'fullName', label: 'Пользователь', render: (row) => <div><div className="font-medium">{row.fullName}</div><div className="text-xs text-muted-foreground">{row.email || '—'} · {row.role || 'User'}</div></div> },
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
                <LineAreaChart data={data.api?.requestsByDay || []} color="rgb(var(--accent-700))" />
              </ChartCard>
              <ChartCard title="Ошибки API по дням" subtitle="Сколько запросов завершались 4xx/5xx за выбранный период.">
                <LineAreaChart data={data.api?.errorsByDay || []} color="rgb(var(--accent2))" />
              </ChartCard>
            </div>

            <div className="grid gap-4 xl:grid-cols-[1.1fr,0.9fr]">
              <ChartCard title="Latency backend по дням" subtitle={`Средняя задержка: ${formatMs(apiTotals.avgLatencyMs)} · p95: ${formatMs(apiTotals.p95LatencyMs)} · p99: ${formatMs(apiTotals.p99LatencyMs)}`}>
                <LineAreaChart data={data.api?.latencyByDay || []} color="rgb(var(--accent3))" valueFormatter={formatMs} />
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
                    { key: 'label', label: 'Маршрут', render: (row) => <span className="block break-all text-sm">{row.label}</span> },
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
                    { key: 'fullName', label: 'Пользователь', render: (row) => <div><div className="font-medium">{row.fullName}</div><div className="text-xs text-muted-foreground">{row.email || '—'}</div></div> },
                    { key: 'value', label: 'Запросов', render: (row) => formatNumber(row.value) },
                    { key: 'errors', label: 'Ошибок', render: (row) => formatNumber(row.errors) },
                    { key: 'avgLatencyMs', label: 'Средняя задержка', render: (row) => formatMs(row.avgLatencyMs) },
                  ]}
                />
              </ChartCard>
            </div>

            <div className="grid gap-4 xl:grid-cols-2">
              <ChartCard title="Попытки по заданиям" subtitle="Сколько действий по code/test/image вообще было за период.">
                <LineAreaChart data={data.assignments?.attemptsByDay || []} color="rgb(var(--accent))" />
              </ChartCard>
              <ChartCard title="Успешные попытки по дням" subtitle={`Общая успешность: ${formatPercent(assignmentTotals.successRate)} · средний score тестов: ${formatPercent(assignmentTotals.avgTestScore)}`}>
                <LineAreaChart data={data.assignments?.successByDay || []} color="rgb(var(--accent2))" />
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
                    { key: 'title', label: 'Задание', render: (row) => <div><div className="font-medium">{row.title}</div><div className="text-xs text-muted-foreground">{row.type} · diff {row.difficulty} · rating {row.rating}</div></div> },
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
                    { key: 'title', label: 'Задание', render: (row) => <div><div className="font-medium">{row.title}</div><div className="text-xs text-muted-foreground">{row.type}</div></div> },
                    { key: 'attempts', label: 'Попыток', render: (row) => formatNumber(row.attempts) },
                    { key: 'successRate', label: 'Успешность', render: (row) => <span className="text-[rgb(var(--accent))]">{formatPercent(row.successRate)}</span> },
                    { key: 'rating', label: 'Рейтинг', render: (row) => formatNumber(row.rating) },
                  ]}
                />
              </ChartCard>
            </div>

            <div className="grid gap-4 xl:grid-cols-2">
              <ChartCard title="Новые support-тикеты по дням" subtitle="Видно нагрузку на поддержку и всплески обращений.">
                <LineAreaChart data={data.support?.ticketsByDay || []} color="rgb(var(--accent3))" />
              </ChartCard>
              <ChartCard title="Закрытые тикеты по дням" subtitle={`Средний первый ответ: ${formatMinutes(supportTotals.avgFirstResponseMinutes)} · среднее закрытие: ${formatMinutes(supportTotals.avgCloseMinutes)}`}>
                <LineAreaChart data={data.support?.closedByDay || []} color="rgb(var(--accent2))" />
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
                    { key: 'label', label: 'Админ', render: (row) => <div><div className="font-medium">{row.label}</div><div className="text-xs text-muted-foreground">{row.email || '—'}</div></div> },
                    { key: 'value', label: 'Сообщений', render: (row) => formatNumber(row.value) },
                  ]}
                />
              </ChartCard>
            </div>
          </>
        ) : null}
      </div>
    </Layout>
  );
}
