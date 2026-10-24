import React, { useMemo, useState } from 'react';
import {
  Area, AreaChart, Bar, BarChart, CartesianGrid, Cell,
  Pie, PieChart, ResponsiveContainer, Tooltip, XAxis, YAxis,
} from 'recharts';
import { Activity, BookCheck, BarChart3, Clock3, Code2, TrendingUp } from 'lucide-react';
import { Card } from '../../components/ui';
import { acceptedSources, dailySeries, languageLabel, successPercent } from './publicStatisticsModel';

const SUCCESS = '#34d399';
const FAILURE = '#fb7185';
const PENDING = '#fbbf24';
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
      ['Успешно', stats.codeAccepted, SUCCESS],
      ['Неуспешно', stats.codeFailed, FAILURE],
      ['Проверяется', stats.codePending, PENDING],
    ].map(([label, value, color]) => (
      <span key={label} className="inline-flex items-center gap-1.5">
        <span className="h-2.5 w-2.5 rounded-full" style={{ background: color }} />
        {label}: <b className="text-foreground">{number(value)}</b>
      </span>
    ))}
  </div>;
}

export default function PublicStatisticsDashboard({ statistics, reliable = true, totalAttempts, attemptsReliable = true }) {
  const [days, setDays] = useState(30);
  const codeDays = useMemo(() => dailySeries(statistics?.dailyCode, days), [statistics?.dailyCode, days]);
  const ratingDays = useMemo(() => dailySeries(statistics?.dailyRating, days), [statistics?.dailyRating, days]);
  if (!reliable || !statistics) {
    return <Card className="p-5 text-sm text-neutral-500" role="status">
      Подробная статистика временно недоступна. Попробуйте открыть профиль позже.
    </Card>;
  }

  const total = Number(statistics.codeAttempts || 0);
  const complete = Number(statistics.codeAccepted || 0) + Number(statistics.codeFailed || 0);
  const percent = successPercent(statistics.codeAccepted, statistics.codeFailed);
  const hasCode = codeDays.some((row) => row.accepted + row.failed + row.pending > 0);
  const hasRating = ratingDays.some((row) => row.tasks > 0);
  const totalNewTasks = ratingDays.reduce((sum, day) => sum + day.tasks, 0);
  const totalEarned = ratingDays.reduce((sum, day) => sum + day.rating, 0);
  const languages = Array.isArray(statistics.languages) ? statistics.languages.slice(0, 7) : [];
  const sources = acceptedSources(statistics.acceptedSources);
  const donut = [
    { name: 'Успешные', value: Number(statistics.codeAccepted || 0), color: SUCCESS },
    { name: 'Неуспешные', value: Number(statistics.codeFailed || 0), color: FAILURE },
    { name: 'Проверяются', value: Number(statistics.codePending || 0), color: PENDING },
  ].filter((item) => item.value > 0);

  return (
    <section className="space-y-4" aria-labelledby="public-user-statistics-title">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h2 id="public-user-statistics-title" className="text-xl font-semibold">Статистика решений</h2>
          <p className="text-sm text-neutral-500 dark:text-neutral-400">Проверенные результаты участника — без исходного кода и приватных ответов</p>
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
        <Stat icon={Activity} title="Всего попыток" value={attemptsReliable && typeof totalAttempts === 'number' ? number(totalAttempts) : '—'} detail="Все типы заданий" />
        <Stat icon={Code2} title="Успешность кода и SQL" value={complete ? `${percent}%` : '—'} detail={`${number(complete)} завершённых проверок`} />
      </div>
      <div className="grid gap-4 lg:grid-cols-[minmax(0,1.6fr)_minmax(300px,1fr)]">
        <Card className="min-w-0 p-5">
          <h3 className="font-semibold">Успешные и неуспешные решения</h3>
          <p className="mt-1 text-xs text-neutral-500">Отправки кода и SQL за {days} дней. Незавершённые проверки отдельно.</p>
          {hasCode ? (
            <div role="img" aria-label="График результатов кода и SQL по дням" className="mt-4">
              <ResponsiveContainer width="100%" height={260}>
                <BarChart data={codeDays} margin={{ top: 8, bottom: 0, left: -22, right: 4 }} barCategoryGap="22%">
                  <CartesianGrid stroke="rgba(148,163,184,.16)" strokeDasharray="3 6" vertical={false} />
                  <XAxis dataKey="label" stroke={TICK} tick={{ fontSize: 10 }} tickLine={false} axisLine={false} interval={days === 90 ? 14 : days === 30 ? 5 : 0} />
                  <YAxis stroke={TICK} tick={{ fontSize: 11 }} allowDecimals={false} tickLine={false} axisLine={false} />
                  <Tooltip contentStyle={TOOLTIP} labelFormatter={(_, payload) => payload?.[0]?.payload?.day || ''} />
                  <Bar name="Принято" dataKey="accepted" stackId="results" fill={SUCCESS} maxBarSize={26} />
                  <Bar name="Не принято" dataKey="failed" stackId="results" fill={FAILURE} maxBarSize={26} />
                  <Bar name="Проверяется" dataKey="pending" stackId="results" fill={PENDING} maxBarSize={26} />
                </BarChart>
              </ResponsiveContainer>
            </div>
          ) : <EmptyGraph>За этот период отправок кода и SQL нет.</EmptyGraph>}
        </Card>
        <Card className="p-5">
          <h3 className="font-semibold">Результат проверок</h3>
          <p className="mt-1 text-xs text-neutral-500">Код и SQL за всё время · {number(total)} попыток</p>
          {donut.length ? (
            <div className="relative mt-2" role="img" aria-label={`Успешных ${statistics.codeAccepted}, неуспешных ${statistics.codeFailed}, проверяются ${statistics.codePending}`}>
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
                <span className="text-xs text-neutral-500">успешных</span>
              </div>
            </div>
          ) : <EmptyGraph />}
          <ResultLegend stats={statistics} />
        </Card>
      </div>
      <div className="grid gap-4 lg:grid-cols-2">
        <Card className="min-w-0 p-5">
          <div className="flex items-center gap-2"><BarChart3 size={17} /><h3 className="font-semibold">Начисленный рейтинг</h3></div>
          <p className="mt-1 text-xs text-neutral-500">Только первое зачтённое решение каждого задания, за {days} дней</p>
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
          <div className="flex items-center gap-2"><Code2 size={17} /><h3 className="font-semibold">Языки программирования</h3></div>
          <p className="mt-1 text-xs text-neutral-500">Количество отправок кода и SQL за всё время</p>
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
          </div> : <EmptyGraph>Отправок кода ещё нет.</EmptyGraph>}
        </Card>
      </div>
      <Card className="p-5">
        <div className="flex items-center gap-2"><Clock3 size={17} /><h3 className="font-semibold">Зачтённые попытки по видам заданий</h3></div>
        <p className="mt-1 text-xs text-neutral-500">Результаты из проверенного рейтингового каталога. Повторные зачёты также входят в число попыток.</p>
        {sources.length ? <div className="mt-4 grid gap-3 sm:grid-cols-3">
          {sources.map((source, index) => (
            <div key={`${source.label}-${index}`} className="rounded-xl border border-[rgba(var(--border)/0.6)] p-4">
              <strong className="text-xl tabular-nums">{number(source.value)}</strong>
              <div className="mt-1 text-xs text-neutral-500">{source.label}</div>
            </div>
          ))}
        </div> : <p className="mt-3 text-sm text-neutral-500">Пока нет зачтённых решений.</p>}
      </Card>
    </section>
  );
}
