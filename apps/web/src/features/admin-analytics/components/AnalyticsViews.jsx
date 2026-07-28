import React from 'react';
import {
  Clock3,
  Users,
  Database,
  BarChart3,
  TrendingUp,
  TrendingDown,
  ShieldAlert,
} from 'lucide-react';
import { Card } from '../../../components/ui';
import AppErrorPanel from '../../../components/AppErrorPanel';
import {
  ResponsiveContainer,
  AreaChart as ReAreaChart,
  Area,
  BarChart as ReBarChart,
  Bar,
  PieChart as RePieChart,
  Pie,
  Cell,
  CartesianGrid,
  XAxis,
  YAxis,
  Tooltip,
} from 'recharts';

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
          <div className="text-sm text-neutral-500 dark:text-neutral-300">{item.label}</div>
          <div className="mt-2 text-2xl font-semibold tracking-tight">{formatComparisonValue(item)}</div>
          <div className="mt-2 text-xs text-neutral-500 dark:text-neutral-400">Предыдущий период: {item.percentMetric ? `${Number(item.previous || 0).toFixed(1)}%` : formatNumber(item.previous)}{!item.percentMetric && item.unit ? ` ${item.unit}` : ''}</div>
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
              <div className="mt-1 text-sm text-neutral-600 dark:text-neutral-300">{alert?.message}</div>
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
          <div className="text-sm text-neutral-500 dark:text-neutral-300">{label}</div>
          <div className="mt-2 text-3xl font-semibold tracking-tight">{value}</div>
          {hint ? <div className="mt-2 text-xs text-neutral-500 dark:text-neutral-300">{hint}</div> : null}
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
  return <div className="rounded-2xl border border-dashed border-[rgba(var(--border)/0.55)] bg-[rgba(var(--muted)/0.26)] px-4 py-10 text-center text-sm text-neutral-500 dark:text-neutral-300">Недостаточно данных за выбранный период.</div>;
}

const chartPalette = [
  'rgba(var(--accent) / 0.96)',
  'rgba(var(--accent2) / 0.88)',
  'rgba(var(--accent3) / 0.92)',
  'rgba(var(--accent) / 0.66)',
  'rgba(var(--accent2) / 0.60)',
  'rgba(var(--accent3) / 0.76)',
  'rgba(var(--text-muted) / 0.72)',
];

function chartValue(item) {
  return Number(item?.value ?? item?.count ?? item?.requests ?? item?.attempts ?? 0);
}

function ChartTooltip({ active, payload, label, formatter = formatNumber }) {
  if (!active || !payload?.length) return null;
  return (
    <div className="rounded-2xl border border-[rgba(var(--border)/0.65)] bg-[rgba(var(--card)/0.96)] px-3 py-2 text-sm shadow-soft backdrop-blur">
      <div className="mb-1 text-xs text-neutral-500 dark:text-neutral-300">{label}</div>
      {payload.map((entry) => (
        <div key={entry.dataKey || entry.name} className="flex items-center justify-between gap-6">
          <span className="text-neutral-500 dark:text-neutral-300">{entry.name}</span>
          <span className="font-semibold">{formatter(entry.value)}</span>
        </div>
      ))}
    </div>
  );
}

function ChartShell({ children, height = 250 }) {
  return (
    <div className="tf-analytics-chart-safe rounded-3xl border border-[rgba(var(--border)/0.55)] bg-[rgba(var(--muted)/0.18)] p-3" style={{ height }}>
      {children}
    </div>
  );
}

function LineAreaChart({ data = [], color = 'rgb(var(--accent))', height = 250, valueFormatter = formatNumber }) {
  if (!Array.isArray(data) || data.length === 0) return <EmptyState />;
  const normalized = data.map((item) => ({ ...item, value: chartValue(item), label: item.label || item.date || '—' }));
  const values = normalized.map((item) => item.value);
  const last = normalized[normalized.length - 1];
  const peak = Math.max(...values, 0);

  return (
    <div className="space-y-4">
      <div className="grid gap-3 sm:grid-cols-3">
        <div className="rounded-2xl border border-[rgba(var(--border)/0.45)] bg-[rgba(var(--muted)/0.38)] px-4 py-3">
          <div className="text-xs text-neutral-500 dark:text-neutral-400">Последнее значение</div>
          <div className="mt-1 text-xl font-semibold">{valueFormatter(last?.value)}</div>
        </div>
        <div className="rounded-2xl border border-[rgba(var(--border)/0.45)] bg-[rgba(var(--muted)/0.38)] px-4 py-3">
          <div className="text-xs text-neutral-500 dark:text-neutral-400">Пик</div>
          <div className="mt-1 text-xl font-semibold">{valueFormatter(peak)}</div>
        </div>
        <div className="rounded-2xl border border-[rgba(var(--border)/0.45)] bg-[rgba(var(--muted)/0.38)] px-4 py-3">
          <div className="text-xs text-neutral-500 dark:text-neutral-400">Точек</div>
          <div className="mt-1 text-xl font-semibold">{formatNumber(normalized.length)}</div>
        </div>
      </div>
      <ChartShell height={height}>
        <ResponsiveContainer width="100%" height="100%">
          <ReAreaChart data={normalized} margin={{ top: 14, right: 18, left: 0, bottom: 4 }}>
            <CartesianGrid stroke="rgba(var(--border) / 0.26)" vertical={false} />
            <XAxis dataKey="label" tickLine={false} axisLine={false} interval="preserveStartEnd" tick={{ fill: 'rgb(var(--text-muted))', fontSize: 12 }} />
            <YAxis tickLine={false} axisLine={false} width={48} tickFormatter={valueFormatter} tick={{ fill: 'rgb(var(--text-muted))', fontSize: 12 }} />
            <Tooltip content={<ChartTooltip formatter={valueFormatter} />} cursor={{ stroke: color, strokeOpacity: 0.22 }} />
            <Area type="monotone" dataKey="value" name="Значение" stroke={color} strokeWidth={2.2} fill={color} fillOpacity={0.14} dot={{ r: 2.5 }} activeDot={{ r: 5 }} isAnimationActive />
          </ReAreaChart>
        </ResponsiveContainer>
      </ChartShell>
    </div>
  );
}

function BarChart({ data = [], color = 'rgb(var(--accent))', height = 300, valueFormatter = formatNumber }) {
  if (!Array.isArray(data) || data.length === 0) return <EmptyState />;
  const normalized = data.map((item) => ({ ...item, label: item.label || item.name || '—', value: chartValue(item) }));
  const looksTemporal = normalized.every((item) => /^\d{2}:\d{2}$/.test(item.label) || /^\d{2}\.\d{2}$/.test(item.label));
  const layout = normalized.length >= 7 && normalized.length <= 15 && !looksTemporal ? 'vertical' : 'horizontal';
  const computedHeight = layout === 'vertical' ? Math.max(height, normalized.length * 38 + 60) : height;

  return (
    <ChartShell height={computedHeight}>
      <ResponsiveContainer width="100%" height="100%">
        <ReBarChart data={normalized} layout={layout} margin={{ top: 12, right: 20, left: layout === 'vertical' ? 18 : 0, bottom: 8 }}>
          <CartesianGrid stroke="rgba(var(--border) / 0.24)" horizontal={layout !== 'vertical'} vertical={layout === 'vertical'} />
          {layout === 'vertical' ? (
            <>
              <XAxis type="number" tickLine={false} axisLine={false} tickFormatter={valueFormatter} tick={{ fill: 'rgb(var(--text-muted))', fontSize: 12 }} />
              <YAxis dataKey="label" type="category" width={130} tickLine={false} axisLine={false} tick={{ fill: 'rgb(var(--text-muted))', fontSize: 12 }} />
            </>
          ) : (
            <>
              <XAxis dataKey="label" tickLine={false} axisLine={false} tick={{ fill: 'rgb(var(--text-muted))', fontSize: 12 }} />
              <YAxis tickLine={false} axisLine={false} width={48} tickFormatter={valueFormatter} tick={{ fill: 'rgb(var(--text-muted))', fontSize: 12 }} />
            </>
          )}
          <Tooltip content={<ChartTooltip formatter={valueFormatter} />} cursor={{ fill: 'rgba(var(--accent) / 0.045)' }} />
          <Bar dataKey="value" name="Значение" fill={color} activeBar={false} radius={layout === 'vertical' ? [0, 10, 10, 0] : [10, 10, 0, 0]} isAnimationActive />
        </ReBarChart>
      </ResponsiveContainer>
    </ChartShell>
  );
}

function DonutChart({ data = [], size = 260 }) {
  if (!Array.isArray(data) || data.length === 0 || data.every((x) => !Number(x.value))) return <EmptyState />;
  const normalized = data.map((item) => ({ ...item, label: item.label || item.name || '—', value: chartValue(item) }));
  const total = normalized.reduce((sum, item) => sum + item.value, 0);

  return (
    <div className="grid gap-5 md:grid-cols-[minmax(220px,0.8fr),1fr] md:items-center">
      <ChartShell height={size}>
        <ResponsiveContainer width="100%" height="100%">
          <RePieChart>
            <Pie data={normalized} dataKey="value" nameKey="label" innerRadius="62%" outerRadius="82%" paddingAngle={2} activeShape={false} isAnimationActive>
              {normalized.map((item, idx) => <Cell key={item.label} fill={chartPalette[idx % chartPalette.length]} />)}
            </Pie>
            <Tooltip content={<ChartTooltip formatter={formatNumber} />} />
            <text x="50%" y="47%" textAnchor="middle" dominantBaseline="middle" fill="rgb(var(--text))" style={{ fontSize: 22, fontWeight: 700 }}>
              {formatNumber(total)}
            </text>
            <text x="50%" y="58%" textAnchor="middle" dominantBaseline="middle" fill="rgb(var(--text-muted))" style={{ fontSize: 9, fontWeight: 700, letterSpacing: '0.16em', textTransform: 'uppercase' }}>
              всего
            </text>
          </RePieChart>
        </ResponsiveContainer>
      </ChartShell>
      <div className="space-y-3">
        {normalized.map((item, idx) => (
          <div key={item.label} className="flex items-center justify-between gap-4 rounded-2xl border border-[rgba(var(--border)/0.45)] bg-[rgba(var(--muted)/0.34)] px-4 py-3">
            <div className="flex min-w-0 items-center gap-3">
              <span className="h-3 w-3 shrink-0 rounded-full" style={{ background: chartPalette[idx % chartPalette.length] }} />
              <span className="truncate">{item.label}</span>
            </div>
            <div className="shrink-0 text-right">
              <div className="font-semibold">{formatNumber(item.value)}</div>
              <div className="text-xs text-neutral-500 dark:text-neutral-400">{formatPercent(total ? (item.value / total) * 100 : 0)}</div>
            </div>
          </div>
        ))}
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
            className="w-full rounded-2xl border border-[rgba(var(--border)/0.65)] bg-[rgba(var(--muted)/0.28)] px-4 py-3 text-sm outline-none transition placeholder:text-neutral-400 focus:border-[rgb(var(--accent))] focus:bg-[rgb(var(--card))]"
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
                    'px-4 py-3 text-left text-sm font-semibold text-neutral-500 dark:text-neutral-300',
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
                <td colSpan={columns.length} className="px-4 py-8 text-center text-sm text-neutral-500 dark:text-neutral-400">Ничего не найдено.</td>
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
                    <td key={col.key} className="px-4 py-3 align-top text-sm break-words text-neutral-800 dark:text-neutral-200">
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
          <div className="rounded-2xl border border-[rgba(var(--border)/0.45)] bg-[rgba(var(--muted)/0.34)] px-4 py-3 text-sm">
            <div>Создан: <b>{formatDateTime(profile.createdAt)}</b></div>
            <div className="mt-1">Последний вход: <b>{formatDateTime(profile.lastLoginAt)}</b></div>
          </div>
        </div>
      </Card>

      <div className="grid gap-4 md:grid-cols-2 2xl:grid-cols-4">
        <MetricCard icon={Users} label="Входы" value={formatNumber(activity.totalLogins)} hint="За выбранный период" />
        <MetricCard icon={Database} label="Запросы к API" value={formatNumber(activity.totalRequests)} hint={`Ошибок: ${formatNumber(activity.errorRequests)}`} />
        <MetricCard icon={BarChart3} label="Code/image/test/math" value={`${formatNumber(activity.codeSubmits)} / ${formatNumber(activity.imageSubmits)} / ${formatNumber(activity.testAttempts)} / ${formatNumber(activity.mathAttempts)}`} hint="Отправки и попытки" />
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


export {
  PERIODS,
  cn,
  formatNumber,
  formatPercent,
  formatMs,
  formatMinutes,
  formatDateTime,
  ComparisonCard,
  SignalsBoard,
  MetricCard,
  SectionTitle,
  ChartCard,
  LineAreaChart,
  BarChart,
  DonutChart,
  RankedTable,
  UserSpotlight,
};
