import React, { useMemo, useState } from 'react';
import { Activity, Search, Wifi, WifiOff } from 'lucide-react';
import { Badge, Card } from '../../components/ui';
import AdminSolutionLiveCard from '../admin-solutions/components/AdminSolutionLiveCard';
import { isPendingLiveStatus } from '../admin-solutions/adminSolutionLiveModel';
import { mergeAttemptFeed, normalizeHistoricalAttempts } from './assignmentInsightsModel';

const isSuccess = (item) => ['accepted', 'passed', 'success'].includes(String(item.status || '').toLowerCase());

export default function AssignmentLiveFeed({ assignmentId, data, liveItems, liveState, compact = false }) {
  const [filter, setFilter] = useState('all');
  const [query, setQuery] = useState('');
  const historical = useMemo(() => normalizeHistoricalAttempts(data?.recentActivity, assignmentId, data?.title), [data?.recentActivity, assignmentId, data?.title]);
  const merged = useMemo(() => mergeAttemptFeed(historical, liveItems, assignmentId, compact ? 6 : 120), [historical, liveItems, assignmentId, compact]);
  const filtered = useMemo(() => merged.filter((item) => {
    if (filter === 'passed' && !isSuccess(item)) return false;
    if (filter === 'failed' && (isSuccess(item) || isPendingLiveStatus(item.status))) return false;
    if (filter === 'pending' && !isPendingLiveStatus(item.status)) return false;
    const q = query.trim().toLowerCase();
    return !q || [item.userLabel, item.kind, item.status, item.language].some((value) => String(value || '').toLowerCase().includes(q));
  }), [merged, filter, query]);
  const online = liveState === 'live';
  return (
    <Card>
      <div className="flex flex-wrap items-start justify-between gap-3 mb-4">
        <div>
          <h2 className="flex items-center gap-2 text-lg font-semibold"><Activity size={19} /> Лента решений</h2>
          <p className="mt-1 text-sm text-muted-foreground">Новые попытки появляются автоматически, старые можно раскрыть для просмотра кода или ответов</p>
        </div>
        <Badge intent={online ? 'success' : 'warning'}>
          {online ? <Wifi size={13} className="mr-1" /> : <WifiOff size={13} className="mr-1" />}
          {online ? 'SSE · в эфире' : liveState === 'reconnecting' ? 'SSE · переподключение' : 'SSE · подключение'}
        </Badge>
      </div>
      {!compact && <div className="flex flex-col md:flex-row gap-3 mb-4">
        <div className="flex flex-wrap gap-1.5" role="group" aria-label="Фильтр решений">
          {[
            { key: 'all', label: 'Все' },
            { key: 'passed', label: 'Принято' },
            { key: 'failed', label: 'Ошибки' },
            { key: 'pending', label: 'Проверяются' },
          ].map((item) => <button key={item.key} type="button" aria-pressed={filter === item.key} onClick={() => setFilter(item.key)}
            className={`rounded-xl border px-3 py-2 text-sm transition-colors ${filter === item.key ? 'bg-[rgb(var(--accent))] text-[rgb(var(--accent-foreground))] border-transparent' : 'border-border hover:bg-muted/40'}`}>
            {item.label}
          </button>)}
        </div>
        <label className="relative md:ml-auto w-full md:max-w-xs">
          <Search size={14} className="absolute left-3 top-1/2 -translate-y-1/2 opacity-60" />
          <input className="h-10 w-full rounded-xl border border-border bg-muted/30 pl-9 pr-3 text-sm outline-none" value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Имя, язык, результат" aria-label="Поиск в ленте решений" />
        </label>
      </div>}
      <div className={`space-y-2 overflow-y-auto pr-1 ${compact ? 'max-h-[480px]' : 'max-h-[850px]'}`} aria-live="polite" aria-relevant="additions">
        {filtered.length ? filtered.map((item) => (
          <div key={item.key} className="relative">
            {liveItems.some((live) => live.key === item.key) ? <span className="absolute -left-1 top-3 z-10 h-2.5 w-2.5 rounded-full bg-emerald-400 ring-2 ring-card" aria-label="Получено в эфире" title="Получено в эфире" /> : null}
            <AdminSolutionLiveCard item={item} compact />
          </div>
        )) : (
          <div className="rounded-xl border border-dashed border-border px-4 py-8 text-center text-sm text-muted-foreground">
            {query || filter !== 'all' ? 'Для этого фильтра попыток не найдено.' : 'Решений пока нет. Новые появятся здесь без перезагрузки страницы.'}
          </div>
        )}
      </div>
      <p className="mt-3 text-xs text-muted-foreground">Показаны последние записи и события SSE. Лента не является полным архивом; для статистики используйте графики выше.</p>
    </Card>
  );
}
