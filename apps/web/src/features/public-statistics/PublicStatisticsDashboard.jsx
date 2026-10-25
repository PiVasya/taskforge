import React, { useMemo, useState } from 'react';
import {
  Area, AreaChart, Bar, BarChart, CartesianGrid, Cell,
  Pie, PieChart, ResponsiveContainer, Tooltip, XAxis, YAxis,
} from 'recharts';
import { Activity, BookCheck, BarChart3, CheckCircle2, Code2, TrendingUp } from 'lucide-react';
import { Card } from '../../components/ui';
import { dailySeries, languageLabel, resultTypes, successPercent } from './publicStatisticsModel';

// A restrained blue / violet palette instead of traffic-light red and green.
// Status is always communicated by its written label, not color alone.
const SUCCESS = '#38bdf8';
const FAILURE = '#a78bfa';
const PENDING = '#94a3b8';
const ACCENT = '#60a5fa';
const TICK = '#94a3b8';
const TOOLTIP = { background: '#252633', border: '1px solid #525463', borderRadius: 12, color: '#f8fafc' };
const number = (value) => new Intl.NumberFormat('ru-RU').format(Number(value || 0));

function EmptyGraph({ children = 'Пока нет данных за выбранный период.' }) {
  return <div className="flex h-[240px] items-center justify-center px-4 text-center text-sm text-neutral-500">{children}</div>;
}

function Stat({ icon: Icon, title, value, detail }) {
  return (
    <Card className="flex flex-col gap-2 p-4">
      <div className="flex items-center justify-between text-sm text-neutral-500 dark:text-neutral-400">
        <span>{title}</span><Icon size={18} aria-hidden="true" />
      </div>
      <strong className="text-2xl font-semibold tabular-nums">{value}</strong>
      <span className="text-xs text-neutral-500 dark:text-neutral-400">{detail}</span>
    </Card>
  );
}

function ResultLegend({ stats }) {
  return <div className="flex flex-wrap justify-center gap-x-4 gap-y-2 text-xs text-neutral-500 dark:text-neutral-400">
    {[
      ['Принято', stats.resultAccepted, SUCCESS],
      ['Не принято', stats.resultFailed, FAILURE],
      ['Не завершено', stats.resultPending, PENDING],
    ].map(([label, value, color]) => (
      <span key={label} className="inline-flex items-center gap-1.5">
        <span className="h-2.5 w-2.5 rounded-sm" style={{ background: color }} aria-hidden="true" />
        {label}: <b className="text-foreground">{number(value)}</b>
      </span>
    ))}
  </div>;
}

function AttemptTypes({ rows }) {
  if (!rows.length) return <p className="mt-3 text-sm text-neutral-500">Пока нет завершённых или ожидающих попыток.</p>;
  return <div className="mt-5 grid gap-x-6 gap-y-5 md:grid-cols-2">
    {rows.map((source) => {
      const all = Math.max(1, source.attempts);
      const acceptedWidth = source.accepted / all * 100;
      const failedWidth = source.failed / all * 100;
      const pendingWidth = source.pending / all * 100;
      return <div key={source.kind}>
        <div className="mb-2 flex items-center justify-between gap-3 text-sm">
          <strong>{source.label}</strong>
          <span className="tabular-nums text-neutral-500">{number(source.attempts)} попыток</span>
        </div>
        <div className="flex h-3 w-full overflow-hidden rounded-md bg-muted" role="img"
          aria-label={`${source.label}: принято ${source.accepted}, не принято ${source.failed}, не завершено ${source.pending}`}>
          <span style={{ background: SUCCESS, width: `${acceptedWidth}%` }} />
          <span style={{ background: FAILURE, width: `${failedWidth}%` }} />
          <span style={{ background: PENDING, width: `${pendingWidth}%` }} />
        </div>
        <div className="mt-2 flex flex-wrap gap-x-3 gap-y-1 text-xs text-neutral-500">
          <span>Принято {number(source.accepted)}</span>
          <span>Не принято {number(source.failed)}</span>
          {source.pending > 0 && <span>Не завершено {number(source.pending)}</span>}
        </div>
      </div>;
    })}
  </div>;
}

export default function PublicStatisticsDashboard({ statistics, reliable = true }) {
  const [days, setDays] = useState(30);
  const attemptsByDay = useMemo(() => dailySeries(statistics?.dailyResults, days), [statistics?.dailyResults, days]);
  const ratingDays = useMemo(() => dailySeries(statistics?.dailyRating, days), [statistics?.dailyRating, days]);
  if (!reliable || !statistics?.dailyResults || !statistics?.resultTypes) {
    return <Card className="p-5 text-sm text-neutral-500" role="status">
      Подробная статистика временно недоступна: не все источники результатов ответили. Попробуйте открыть профиль позже.
    </Card>;
  }

  const total = Number(statistics.resultAttempts || 0);
  const complete = Number(statistics.resultAccepted || 0) + Number(statistics.resultFailed || 0);
  const percent = successPercent(statistics.resultAccepted, statistics.resultFailed);
  const hasAttempts = attemptsByDay.some((row) => row.accepted + row.failed + row.pending > 0);
  const hasRating = ratingDays.some((row) => row.tasks > 0);
  const totalNewTasks = ratingDays.reduce((sum, day) => sum + day.tasks, 0);
  const totalEarned = ratingDays.reduce((sum, day) => sum + day.rating, 0);
  const languages = Array.isArray(statistics.languages) ? statistics.languages.slice(0, 7) : [];
  const sources = resultTypes(statistics.resultTypes);
  const donut = [
    { name: 'Принято', value: Number(statistics.resultAccepted || 0), color: SUCCESS },
    { name: 'Не принято', value: Number(statistics.resultFailed || 0), color: FAILURE },
    { name: 'Не завершено', value: Number(statistics.resultPending || 0), color: PENDING },
  ].filter((item) => item.value > 0);

  return (
    <section className="space-y-4" aria-labelledby="public-user-statistics-title">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h2 id="public-user-statistics-title" className="text-xl font-semibold">Статистика решений</h2>
          <p className="text-sm text-neutral-500 dark:text-neutral-400">Все виды заданий · без исходного кода и приватных ответов</p>
        </div>
        <div className="inline-flex gap-1 rounded-xl border border-border p-1" role="group" aria-label="Период статистики">
          {[7, 30, 90].map((range) => (
            <button key={range} type="button" onClick={() => setDays(range)} aria-pressed={days === range}
              className={`rounded-lg px-3 py-1.5 text-xs font-semibold transition-colors ${days === range ? 'bg-[rgb(var(--accent))] text-[rgb(var(--accent-foreground))]' : 'text-neutral-500 hover:bg-muted/70'}`}>
              {range} дней
            </button>
          ))}
        </div>
      </div>
      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        <Stat icon={TrendingUp} title="Рейтинг" value={number(statistics.rating)} detail="Из сохранённого рейтинга" />
        <Stat icon={BookCheck} title="Зачтено заданий" value={number(statistics.solvedAssignments)} detail="Уникальные задания" />
        <Stat icon={Activity} title="Всего попыток" value={number(total)} detail="Программирование, SQL, графика, тесты и математика" />
        <Stat icon={CheckCircle2} title="Успешность решений" value={complete ? `${percent}%` : '—'} detail={`${number(complete)} завершённых проверок · все виды`} />
      </div>
      <div className="grid gap-4 lg:grid-cols-[minmax(0,1.6fr)_minmax(300px,1fr)]">
        <Card className="min-w-0 p-5">
          <h3 className="font-semibold">Попытки по дням</h3>
          <p className="mt-1 text-xs text-neutral-500">Все виды заданий за {days} дней. Незавершённые попытки показаны отдельно.</p>
          {hasAttempts ? (
            <div role="img" aria-label="Результаты попыток по дням, все виды заданий" className="mt-4">
              <ResponsiveContainer width="100%" height={260}>
                <BarChart data={attemptsByDay} margin={{ top: 8, bottom: 0, left: -22, right: 4 }} barCategoryGap="22%">
                  <CartesianGrid stroke="rgba(148,163,184,.16)" strokeDasharray="3 6" vertical={false} />
                  <XAxis dataKey="label" stroke={TICK} tick={{ fontSize: 10 }} tickLine={false} axisLine={false} interval={days === 90 ? 14 : days === 30 ? 5 : 0} />
                  <YAxis stroke={TICK} tick={{ fontSize: 11 }} allowDecimals={false} tickLine={false} axisLine={false} />
                  <Tooltip contentStyle={TOOLTIP} labelFormatter={(_, payload) => payload?.[0]?.payload?.day || ''} />
                  <Bar name="Принято" dataKey="accepted" stackId="results" fill={SUCCESS} maxBarSize={26} />
                  <Bar name="Не принято" dataKey="failed" stackId="results" fill={FAILURE} maxBarSize={26} />
                  <Bar name="Не завершено" dataKey="pending" stackId="results" fill={PENDING} maxBarSize={26} />
                </BarChart>
              </ResponsiveContainer>
            </div>
          ) : <EmptyGraph>За этот период попыток пока нет.</EmptyGraph>}
        </Card>
        <Card className="p-5">
          <h3 className="font-semibold">Результаты проверок</h3>
          <p className="mt-1 text-xs text-neutral-500">Все виды заданий за всё время · {number(total)} попыток</p>
          {donut.length ? (
            <div className="relative mt-2" role="img" aria-label={`Принято ${statistics.resultAccepted}, не принято ${statistics.resultFailed}, не завершено ${statistics.resultPending}`}>
              <ResponsiveContainer width="100%" height={225}>
                <PieChart>
                  <Pie data={donut} dataKey="value" nameKey="name" cx="50%" cy="50%" innerRadius={68} outerRadius={89} paddingAngle={2} stroke="none">
                    {donut.map((item) => <Cell key={item.name} fill={item.color} />)}
                  </Pie>
                  <Tooltip contentStyle={TOOLTIP} />
                </PieChart>
              </ResponsiveContainer>
              <div className="pointer-events-none absolute inset-0 flex flex-col items-center justify-center">
                <strong className="text-3xl tabular-nums">{complete ? `${percent}%` : '—'}</strong>
                <span className="text-xs text-neutral-500">принято</span>
              </div>
            </div>
          ) : <EmptyGraph />}
          <ResultLegend stats={statistics} />
        </Card>
      </div>
      <Card className="p-5">
        <div className="flex items-center gap-2"><BarChart3 size={17} /><h3 className="font-semibold">По видам заданий</h3></div>
        <p className="mt-1 text-xs text-neutral-500">У каждого типа одинаковая статистика: принято, не принято и не завершено.</p>
        <AttemptTypes rows={sources} />
      </Card>
      <div className="grid gap-4 lg:grid-cols-2">
        <Card className="min-w-0 p-5">
          <div className="flex items-center gap-2"><TrendingUp size={17} /><h3 className="font-semibold">Начисленный рейтинг</h3></div>
          <p className="mt-1 text-xs text-neutral-500">Первое зачтённое решение каждого задания, за {days} дней</p>
          {hasRating ? (
            <div role="img" aria-label="График начисленного рейтинга по дням" className="mt-4">
              <ResponsiveContainer width="100%" height={220}>
                <AreaChart data={ratingDays} margin={{ top: 8, bottom: 0, left: -22, right: 4 }}>
                  <CartesianGrid stroke="rgba(148,163,184,.16)" strokeDasharray="3 6" vertical={false} />
                  <XAxis dataKey="label" stroke={TICK} tick={{ fontSize: 10 }} tickLine={false} axisLine={false} interval={days === 90 ? 14 : days === 30 ? 5 : 0} />
                  <YAxis stroke={TICK} tick={{ fontSize: 11 }} tickLine={false} axisLine={false} allowDecimals={false} />
                  <Tooltip contentStyle={TOOLTIP} labelFormatter={(_, payload) => payload?.[0]?.payload?.day || ''} />
                  <Area dataKey="rating" name="Начислено" type="monotone" stroke={ACCENT} fill="rgba(96,165,250,.18)" strokeWidth={2} />
                </AreaChart>
              </ResponsiveContainer>
            </div>
          ) : <EmptyGraph>За этот период новых зачтённых заданий нет.</EmptyGraph>}
          <div className="mt-2 flex flex-wrap items-center gap-4 text-xs text-neutral-500">
            <span>Зачтено заданий: <b className="text-foreground">{number(totalNewTasks)}</b></span>
            <span>Начислено: <b className="text-foreground">+{number(totalEarned)}</b></span>
          </div>
        </Card>
        <Card className="p-5">
          <div className="flex items-center gap-2"><Code2 size={17} /><h3 className="font-semibold">Использованные языки</h3></div>
          <p className="mt-1 text-xs text-neutral-500">Для заданий, в которых отправляется программный код</p>
          {languages.length ? <div className="mt-5 space-y-4">
            {languages.map((item) => {
              const maximum = Math.max(1, ...languages.map((language) => Number(language.attempts || 0)));
              return <div key={item.language}>
                <div className="mb-1.5 flex justify-between gap-3 text-sm"><strong>{languageLabel(item.language)}</strong><span className="text-neutral-500">{number(item.attempts)} · принято {number(item.accepted)}</span></div>
                <div className="h-2.5 w-full overflow-hidden rounded-full bg-muted" role="img" aria-label={`${languageLabel(item.language)}: ${item.attempts} отправок`}>
                  <div className="h-full rounded-full bg-sky-400" style={{ width: `${Number(item.attempts || 0) / maximum * 100}%` }} />
                </div>
              </div>;
            })}
          </div> : <EmptyGraph>Отправок программного кода ещё нет.</EmptyGraph>}
        </Card>
      </div>
    </section>
  );
}
