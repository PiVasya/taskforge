import React, { useState } from 'react';
import {
  BarChart3,
  Database,
  Search,
  LifeBuoy,
  RefreshCw,
  Sparkles,
  Users,
} from 'lucide-react';
import AppErrorPanel from '../../components/AppErrorPanel';
import { Button, Card } from '../../components/ui';
import { getAdminAnalyticsOverview, getAdminAnalyticsUser, searchAdminAnalyticsUsers } from '../../api/adminAnalytics';
import useQuery from '../../hooks/useQuery';
import useDebouncedValue from '../../hooks/useDebouncedValue';
import {
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
} from './components/AnalyticsViews';

export default function AdminAnalyticsPage() {
  const [days, setDays] = useState(30);
  const [selectedUser, setSelectedUser] = useState(null);
  const [userSearch, setUserSearch] = useState('');
  const [globalUserSearch, setGlobalUserSearch] = useState('');
  const debouncedGlobalUserSearch = useDebouncedValue(globalUserSearch.trim(), 250);

  const overviewQuery = useQuery({
    queryKey: ['admin-analytics', 'overview', days],
    queryFn: () => getAdminAnalyticsOverview(days),
    staleTime: 30_000,
    keepPreviousData: true,
    refetchOnWindowFocus: true,
  });
  const userQuery = useQuery({
    queryKey: ['admin-analytics', 'user', selectedUser?.userId || '', days],
    queryFn: () => getAdminAnalyticsUser(selectedUser.userId, days),
    enabled: Boolean(selectedUser?.userId),
    staleTime: 30_000,
    keepPreviousData: true,
  });
  const globalUserQuery = useQuery({
    queryKey: ['admin-analytics', 'user-search', debouncedGlobalUserSearch],
    queryFn: () => searchAdminAnalyticsUsers(debouncedGlobalUserSearch, 8),
    enabled: debouncedGlobalUserSearch.length >= 2,
    staleTime: 30_000,
    keepPreviousData: true,
  });

  const data = overviewQuery.data || null;
  const loading = overviewQuery.isLoading;
  const refreshing = overviewQuery.isFetching;
  const pageError = overviewQuery.error || null;
  const userData = userQuery.data || null;
  const userLoading = userQuery.isLoading || userQuery.isFetching;
  const userError = userQuery.error || null;
  const globalUserResults = Array.isArray(globalUserQuery.data) ? globalUserQuery.data : [];
  const globalUserLoading = globalUserQuery.isLoading || globalUserQuery.isFetching;

  const apiTotals = data?.api?.totals || {};
  const usersTotals = data?.users?.totals || {};
  const assignmentTotals = data?.assignments?.totals || {};
  const supportTotals = data?.support?.totals || {};
  const executive = data?.executive || {};

  const heroCards = [
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
      hint: `Успешность: ${formatPercent(assignmentTotals.successRate)} · code/test/image/math: ${formatNumber(assignmentTotals.codeAttempts)} / ${formatNumber(assignmentTotals.testAttempts)} / ${formatNumber(assignmentTotals.imageAttempts)} / ${formatNumber(assignmentTotals.mathAttempts)}`,
    },
    {
      icon: LifeBuoy,
      label: 'Support',
      value: formatNumber(supportTotals.userMessages || supportTotals.totalMessages || supportTotals.totalTickets),
      hint: `Среднее время ответа: ${formatMinutes(supportTotals.avgResponseMinutes || supportTotals.avgFirstResponseMinutes)} · ответов админов: ${formatNumber(supportTotals.adminMessages)}`,
    },
  ];

  return (
    <>
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
                      days === value ? 'bg-[rgba(var(--card)/0.96)] shadow-soft font-medium border border-[rgba(var(--border)/0.45)]' : 'text-neutral-500 hover:text-neutral-900 dark:text-neutral-300 dark:hover:text-neutral-100'
                    )}
                  >
                    {value} дн
                  </button>
                ))}
              </div>
              <Button variant="outline" onClick={overviewQuery.refetch} disabled={refreshing || loading}>
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
            {data.privacy ? (
              <Card className="p-5">
                <div className="text-base font-semibold">Сбор IP и приватность</div>
                <div className="mt-2 text-sm text-neutral-500 dark:text-neutral-300">
                  {data.privacy.note || 'IP фиксируются по правилам пользовательского соглашения.'} Подсеть: {data.privacy.ipPrefix || '—'}.
                </div>
              </Card>
            ) : null}
            <div className="grid gap-4 xl:grid-cols-3">
              <ChartCard title="Потенциально шумные пользователи" subtitle="Кто создаёт больше всего API-нагрузки за период. По клику можно открыть личную аналитику.">
                <RankedTable
                  rows={executive.noisyUsers || []}
                  activeId={selectedUser?.userId}
                  onRowClick={(row) => setSelectedUser(row)}
                  columns={[
                    { key: 'fullName', label: 'Пользователь', render: (row) => <div><div className="font-medium">{row.fullName}</div><div className="text-xs text-neutral-500 dark:text-neutral-300">{row.email || '—'}</div></div> },
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
                    { key: 'title', label: 'Задание', render: (row) => <div><div className="font-medium">{row.title}</div><div className="text-xs text-neutral-500 dark:text-neutral-300">{row.type}</div></div> },
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
              <ChartCard title="Топ пользователей по активности" subtitle="Нажми на строку, чтобы открыть личную статистику пользователя. Считаются реальные API-вызовы, а не только входы." tall>
                <div className="mb-5 space-y-3">
                  <div className="max-w-xl">
                    <label className="mb-2 block text-sm font-medium text-neutral-600 dark:text-neutral-300">Найти любого пользователя по всей базе</label>
                    <div className="relative">
                      <Search size={16} className="pointer-events-none absolute left-4 top-1/2 -translate-y-1/2 text-neutral-400" />
                      <input
                        type="search"
                        value={globalUserSearch}
                        onChange={(e) => setGlobalUserSearch(e.target.value)}
                        placeholder="Имя, почта или роль"
                        className="w-full rounded-2xl border border-[rgba(var(--border)/0.65)] bg-[rgba(var(--muted)/0.28)] py-3 pl-11 pr-4 text-sm outline-none transition placeholder:text-neutral-400 focus:border-[rgb(var(--accent))] focus:bg-[rgb(var(--card))]"
                      />
                    </div>
                  </div>
                  {globalUserSearch.trim().length >= 2 ? (
                    <div className="rounded-2xl border border-[rgba(var(--border)/0.45)] bg-[rgba(var(--muted)/0.22)] p-2">
                      {globalUserLoading ? (
                        <div className="px-3 py-4 text-sm text-neutral-500 dark:text-neutral-400">Ищу пользователей…</div>
                      ) : globalUserResults.length === 0 ? (
                        <div className="px-3 py-4 text-sm text-neutral-500 dark:text-neutral-400">Ничего не найдено по всей базе пользователей.</div>
                      ) : (
                        <div className="space-y-1">
                          {globalUserResults.map((row) => (
                            <button
                              key={row.userId}
                              type="button"
                              onClick={() => {
                                setSelectedUser(row);
                                setGlobalUserSearch('');
                              }}
                              className="flex w-full items-center justify-between gap-3 rounded-2xl px-3 py-3 text-left transition hover:bg-[rgba(var(--muted)/0.32)]"
                            >
                              <div className="min-w-0">
                                <div className="truncate font-medium">{row.fullName}</div>
                                <div className="truncate text-xs text-neutral-500 dark:text-neutral-300">{row.email || '—'} · {row.role || 'User'}</div>
                              </div>
                              <div className="shrink-0 text-xs text-neutral-500 dark:text-neutral-400">{formatDateTime(row.lastLoginAt)}</div>
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
                    { key: 'fullName', label: 'Пользователь', render: (row) => <div><div className="font-medium">{row.fullName}</div><div className="text-xs text-neutral-500 dark:text-neutral-300">{row.email || '—'} · {row.role || 'User'}</div></div> },
                    { key: 'value', label: 'API-запросов', render: (row) => formatNumber(row.value) },
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
                    { key: 'fullName', label: 'Пользователь', render: (row) => <div><div className="font-medium">{row.fullName}</div><div className="text-xs text-neutral-500 dark:text-neutral-300">{row.email || '—'}</div></div> },
                    { key: 'value', label: 'Запросов', render: (row) => formatNumber(row.value) },
                    { key: 'errors', label: 'Ошибок', render: (row) => formatNumber(row.errors) },
                    { key: 'avgLatencyMs', label: 'Средняя задержка', render: (row) => formatMs(row.avgLatencyMs) },
                  ]}
                />
              </ChartCard>
            </div>

            <div className="grid gap-4 xl:grid-cols-3">
              <ChartCard title="Ошибочные endpoint’ы" subtitle="Где реально были 4xx/5xx.">
                <RankedTable
                  rows={data.api?.errorEndpoints || []}
                  columns={[
                    { key: 'label', label: 'Маршрут', render: (row) => <div><span className="block break-all text-sm font-medium">{row.label}</span><span className="text-xs text-neutral-500 dark:text-neutral-300">{row.sample || '—'}</span></div> },
                    { key: 'errors', label: 'Ошибок', render: (row) => formatNumber(row.errors || row.value) },
                    { key: 'lastErrorAt', label: 'Последняя', render: (row) => formatDateTime(row.lastErrorAt) },
                  ]}
                />
              </ChartCard>
              <ChartCard title="HTTP-статусы" subtitle="Распределение ответов backend за выбранный период.">
                <DonutChart data={data.api?.statusCodes || []} />
              </ChartCard>
              <ChartCard title="Сетевые источники" subtitle="IP фиксируются по правилам соглашения; здесь сгруппированы сетевые источники.">
                <RankedTable
                  rows={data.api?.ipPrefixes || []}
                  columns={[
                    { key: 'label', label: 'Подсеть', render: (row) => <span className="block break-all text-sm">{row.label}</span> },
                    { key: 'requests', label: 'Запросов', render: (row) => formatNumber(row.requests || row.value) },
                    { key: 'errors', label: 'Ошибок', render: (row) => formatNumber(row.errors) },
                  ]}
                />
              </ChartCard>
            </div>

            <div className="grid gap-4 xl:grid-cols-2">
              <ChartCard title="Попытки по заданиям" subtitle="Сколько реальных отправленных попыток code/test/image/math было за период.">
                <LineAreaChart data={data.assignments?.attemptsByDay || []} color="rgb(var(--accent))" />
              </ChartCard>
              <ChartCard title="Успешные попытки по дням" subtitle={`Общая успешность: ${formatPercent(assignmentTotals.successRate)} · средний score test/math: ${formatPercent(assignmentTotals.avgTestScore)}`}>
                <LineAreaChart data={data.assignments?.successByDay || []} color="rgb(var(--accent2))" />
              </ChartCard>
            </div>

            <div className="grid gap-4 xl:grid-cols-[0.95fr,1.05fr]">
              <ChartCard title="Типы активностей в заданиях" subtitle="Распределение реальных отправленных попыток по типам.">
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
                    { key: 'title', label: 'Задание', render: (row) => <div><div className="font-medium">{row.title}</div><div className="text-xs text-neutral-500 dark:text-neutral-300">{row.type} · diff {row.difficulty} · rating {row.rating}</div></div> },
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
                    { key: 'title', label: 'Задание', render: (row) => <div><div className="font-medium">{row.title}</div><div className="text-xs text-neutral-500 dark:text-neutral-300">{row.type}</div></div> },
                    { key: 'attempts', label: 'Попыток', render: (row) => formatNumber(row.attempts) },
                    { key: 'successRate', label: 'Успешность', render: (row) => <span className="text-[rgb(var(--accent))]">{formatPercent(row.successRate)}</span> },
                    { key: 'rating', label: 'Рейтинг', render: (row) => formatNumber(row.rating) },
                  ]}
                />
              </ChartCard>
            </div>

            <div className="grid gap-4 xl:grid-cols-2">
              <ChartCard title="Сообщения пользователей по дням" subtitle="Видно нагрузку на поддержку и всплески сообщений.">
                <LineAreaChart data={data.support?.userMessagesByDay || data.support?.ticketsByDay || []} color="rgb(var(--accent3))" />
              </ChartCard>
              <ChartCard title="Ответы админов по дням" subtitle={`Средний первый ответ: ${formatMinutes(supportTotals.avgFirstResponseMinutes)} · среднее время ответа: ${formatMinutes(supportTotals.avgResponseMinutes)}`}>
                <LineAreaChart data={data.support?.adminMessagesByDay || data.support?.closedByDay || []} color="rgb(var(--accent2))" />
              </ChartCard>
            </div>

            <div className="grid gap-4 xl:grid-cols-[0.9fr,1.1fr]">
              <ChartCard title="Типы обращений" subtitle="Распределение обращений по доступным категориям.">
                <DonutChart data={data.support?.ticketTypes || []} />
              </ChartCard>
              <ChartCard title="Самые активные админы поддержки" subtitle="Кто чаще всего отвечает в support-системе.">
                <RankedTable
                  rows={data.support?.topAdmins || []}
                  columns={[
                    { key: 'label', label: 'Админ', render: (row) => <div><div className="font-medium">{row.label}</div><div className="text-xs text-neutral-500 dark:text-neutral-300">{row.email || '—'}</div></div> },
                    { key: 'value', label: 'Сообщений', render: (row) => formatNumber(row.value) },
                  ]}
                />
              </ChartCard>
            </div>
          </>
        ) : null}
      </div>
    </>
  );
}
