import React, { useMemo, useState } from 'react';
import {
  Bar, BarChart, CartesianGrid, Cell, Legend, Pie, PieChart,
  ResponsiveContainer, Tooltip, XAxis, YAxis,
} from 'recharts';
import { Badge, Card } from '../../components/ui';
import { attemptSummary, dailyChartRows, statusLabel } from './assignmentInsightsModel';

const SUCCESS = '#34d399';
const FAILURE = '#fb7185';
const PENDING = '#94a3b8';
const INK = '#94a3b8';
const tooltipStyle = {
  background: '#22252e', border: '1px solid #4b5563', borderRadius: '14px',
  color: '#f8fafc', boxShadow: '0 10px 25px rgba(0,0,0,.2)',
};

function ChartEmpty({ children = 'Пока нет попыток для построения графика.' }) {
  return <div className="flex items-center justify-center min-h-[220px] rounded-2xl border border-dashed border-border text-center text-sm text-muted-foreground px-5">{children}</div>;
}

function SummaryLegend({ passed, failed, pending }) {
  return (
    <div className="flex flex-wrap justify-center gap-4 text-sm">
      <span className="inline-flex items-center gap-2"><i className="h-2.5 w-2.5 rounded-full" style={{ background: SUCCESS }} />Успешных: <b>{passed}</b></span>
      <span className="inline-flex items-center gap-2"><i className="h-2.5 w-2.5 rounded-full" style={{ background: FAILURE }} />Неуспешных: <b>{failed}</b></span>
      {pending ? <span className="inline-flex items-center gap-2"><i className="h-2.5 w-2.5 rounded-full" style={{ background: PENDING }} />Проверяются: <b>{pending}</b></span> : null}
    </div>
  );
}

export default function AssignmentAnalyticsCharts({ data }) {
  const summary = useMemo(() => attemptSummary(data), [data]);
  const [range, setRange] = useState(14);
  const daily = useMemo(() => dailyChartRows(data?.dailyAttempts, range), [data?.dailyAttempts, range]);
  const hasDaily = daily.some((item) => item.total > 0);
  const donut = [{ name: 'Успешные', value: summary.passed }, { name: 'Неуспешные', value: summary.failed }, { name: 'Проверяются', value: summary.pending }].filter((x) => x.value > 0);
  const kinds = summary.kinds.filter((item) => item.total > 0);
  const statuses = (data?.statusStats || []).slice(0, 6);
  const maxStatus = Math.max(1, ...statuses.map((x) => Number(x.value || 0)));

  return (
    <div className="space-y-5">
      <div className="grid gap-5 xl:grid-cols-[minmax(0,1.6fr)_minmax(300px,1fr)]">
        <Card>
          <div className="flex flex-wrap items-start justify-between gap-3 mb-4">
            <div>
              <h2 className="text-lg font-semibold">Успешные и неуспешные решения</h2>
              <p className="mt-1 text-sm text-muted-foreground">Реальные попытки за последние 30 дней, включая SQL, тесты и математику</p>
            </div>
            <div className="flex gap-1 rounded-xl border border-border p-1" role="group" aria-label="Период графика">
              {[7, 14, 30].map((days) => (
                <button key={days} type="button" onClick={() => setRange(days)}
                  aria-pressed={range === days}
                  className={`rounded-lg px-3 py-1.5 text-xs font-medium transition-colors ${range === days ? 'bg-[rgb(var(--accent))] text-[rgb(var(--accent-foreground))]' : 'hover:bg-muted/60 text-muted-foreground'}`}>
                  {days} дней
                </button>
              ))}
            </div>
          </div>
          {hasDaily ? (
            <div role="img" aria-label="Столбчатый график успешных и неуспешных решений по дням">
              <ResponsiveContainer width="100%" height={270}>
                <BarChart data={daily} margin={{ top: 8, right: 4, bottom: 0, left: -24 }} barCategoryGap="28%">
                  <CartesianGrid stroke="rgba(148,163,184,.14)" strokeDasharray="3 6" vertical={false} />
                  <XAxis dataKey="label" stroke={INK} tick={{ fontSize: 11 }} tickLine={false} axisLine={false}
                    interval={range >= 30 ? 4 : range >= 14 ? 1 : 0} />
                  <YAxis stroke={INK} allowDecimals={false} tick={{ fontSize: 11 }} tickLine={false} axisLine={false} />
                  <Tooltip contentStyle={tooltipStyle} labelFormatter={(_, payload) => payload?.[0]?.payload?.dayUtc || ''} />
                  <Legend wrapperStyle={{ fontSize: 12, paddingTop: 8 }} />
                  <Bar name="Успешные" dataKey="passed" stackId="attempts" fill={SUCCESS} maxBarSize={30} />
                  <Bar name="Неуспешные" dataKey="failed" stackId="attempts" fill={FAILURE} maxBarSize={30} />
                  <Bar name="Проверяются" dataKey="pending" stackId="attempts" fill={PENDING} radius={[4, 4, 0, 0]} maxBarSize={30} />
                </BarChart>
              </ResponsiveContainer>
            </div>
          ) : <ChartEmpty>За выбранный период решений нет. Выбери больший период или вернись позже.</ChartEmpty>}
        </Card>

        <Card>
          <h2 className="text-lg font-semibold">Общий результат</h2>
          <p className="mt-1 text-sm text-muted-foreground">По всем попыткам задания за всё время</p>
          {summary.total ? (
            <>
              <div className="relative" role="img" aria-label={`Успешных попыток ${summary.passed}, неуспешных ${summary.failed}, проверяются ${summary.pending}`}>
                <ResponsiveContainer width="100%" height={230}>
                  <PieChart>
                    <Pie data={donut} dataKey="value" nameKey="name" cx="50%" cy="50%" innerRadius={77} outerRadius={96} stroke="none" paddingAngle={2}>
                      {donut.map((item) => <Cell key={item.name} fill={item.name === 'Успешные' ? SUCCESS : item.name === 'Неуспешные' ? FAILURE : PENDING} />)}
                    </Pie>
                    <Tooltip contentStyle={tooltipStyle} />
                  </PieChart>
                </ResponsiveContainer>
                <div className="pointer-events-none absolute inset-0 flex flex-col items-center justify-center">
                  <span className="text-3xl font-bold">{summary.successRate}%</span>
                  <span className="text-xs text-muted-foreground">успешных</span>
                </div>
              </div>
              <SummaryLegend passed={summary.passed} failed={summary.failed} pending={summary.pending} />
            </>
          ) : <ChartEmpty />}
        </Card>
      </div>

      <div className="grid gap-5 lg:grid-cols-2">
        <Card>
          <h2 className="text-lg font-semibold mb-1">По типам попыток</h2>
          <p className="mb-4 text-sm text-muted-foreground">Сравнение принятых и непринятых решений</p>
          {kinds.length ? <div className="space-y-4">
            {kinds.map((item) => (
              <div key={item.kind}>
                <div className="mb-2 flex items-center justify-between gap-2 text-sm">
                  <b>{item.label}</b>
                  <span className="text-muted-foreground">{item.passed} принято · {item.failed} не принято{item.pending ? ` · ${item.pending} проверяются` : ''}</span>
                </div>
                <div className="h-3 w-full flex rounded-full bg-muted overflow-hidden" role="img" aria-label={`${item.label}: ${item.passed} успешных из ${item.total}`}>
                  <div style={{ width: `${item.passed / item.total * 100}%`, background: SUCCESS }} />
                  <div style={{ width: `${item.failed / item.total * 100}%`, background: FAILURE }} />
                  {item.pending ? <div style={{ width: `${item.pending / item.total * 100}%`, background: PENDING }} /> : null}
                </div>
              </div>
            ))}
          </div> : <ChartEmpty />}
        </Card>
        <Card>
          <div className="flex flex-wrap justify-between items-center gap-2 mb-1">
            <h2 className="text-lg font-semibold">Причины результатов</h2>
            <Badge intent="outline">Последние записи</Badge>
          </div>
          <p className="mb-4 text-sm text-muted-foreground">Статусы из доступной истории попыток, не из общего количества</p>
          {statuses.length ? <div className="space-y-3">
            {statuses.map((item, idx) => (
              <div key={`${item.label}-${idx}`}>
                <div className="flex items-center justify-between gap-3 mb-1.5 text-sm">
                  <span className="truncate" title={String(item.label)}>{statusLabel(item.label)}</span>
                  <b className="tabular-nums">{item.value}</b>
                </div>
                <div className="h-2 w-full rounded-full bg-muted overflow-hidden">
                  <div className="h-full rounded-full bg-sky-400" style={{ width: `${Math.max(1, Number(item.value || 0) / maxStatus * 100)}%` }} />
                </div>
              </div>
            ))}
          </div> : <ChartEmpty />}
        </Card>
      </div>
    </div>
  );
}
