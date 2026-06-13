

import React, { useEffect, useState } from 'react';
import Layout from '../../components/Layout';
import { getLeaderboard } from '../../api/leaderboard';
import { getCourses } from '../../api/courses';
import { getGroups } from '../../api/groups';
import LeaderboardCard from '../../components/LeaderboardCard';
import QuotaPill from '../../components/QuotaPill';
import { Card, Input, Select, Button } from '../../components/ui';
import { useNotify } from '../../components/notify/NotifyProvider';
import { handleApiError } from '../../utils/handleApiError';
import { getApiErrorMessage } from '../../api/http';
import { AlertTriangle } from 'lucide-react';

const LEADERBOARD_PAGE_SIZE = 20;

function normalizePagedLeaderboard(payload) {
  if (Array.isArray(payload)) return { items: payload, page: 1, hasMore: false, total: payload.length };
  const items = Array.isArray(payload?.items) ? payload.items : [];
  return {
    items,
    page: Number(payload?.page || 1),
    hasMore: Boolean(payload?.hasMore),
    total: Number(payload?.total || items.length),
  };
}

export default function LeaderboardPage() {
  const notify = useNotify();
  const [entries, setEntries] = useState([]);
  const [loading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState(null);
  const [page, setPage] = useState(1);
  const [hasMore, setHasMore] = useState(false);
  const [total, setTotal] = useState(0);

  
  const [courses, setCourses] = useState([]);
  const [groups, setGroups] = useState([]);
  const [courseId, setCourseId] = useState('');
  const [days, setDays] = useState('');
  const [groupId, setGroupId] = useState('');
  const [query, setQuery] = useState('');

  
  useEffect(() => {
    (async () => {
      try {
        const list = await getCourses();
        setCourses(Array.isArray(list) ? list : []);
      } catch (e) {
        handleApiError(e, notify, 'Не удалось загрузить курсы');
      }
      try {
        const gs = await getGroups();
        setGroups(Array.isArray(gs) ? gs : []);
      } catch (e) {
        handleApiError(e, notify, 'Не удалось загрузить группы');
      }
    })();
  }, []);

  const loadEntries = async ({ reset = true } = {}) => {
    try {
      if (reset) setLoading(true);
      else setLoadingMore(true);
      setError(null);

      const params = {};

      if (courseId) params.courseId = courseId;

      const daysInt = parseInt(days, 10);
      if (!Number.isNaN(daysInt) && daysInt > 0) {
        params.days = daysInt;
      }

      if (groupId) params.groupId = groupId;
      if (query.trim()) params.q = query.trim();

      const nextPage = reset ? 1 : page + 1;
      params.page = nextPage;
      params.pageSize = LEADERBOARD_PAGE_SIZE;

      const data = await getLeaderboard(params);
      const parsed = normalizePagedLeaderboard(data);
      setEntries((prev) => reset ? parsed.items : [...prev, ...parsed.items]);
      setPage(parsed.page);
      setHasMore(parsed.hasMore);
      setTotal(parsed.total);
    } catch (e) {
      if (e?.response?.status === 429) {
        const ra = e.response?.data?.retryAfterSeconds;
        const msg = getApiErrorMessage(e, 'Топ можно обновлять раз в 5 минут');
        setError(ra ? `${msg}. Повтори через ~${Math.ceil(ra / 60)} мин.` : msg);
      } else {
        const parsed = handleApiError(e, notify, 'Не удалось загрузить топ');
        setError(parsed?.userMessage || 'Не удалось загрузить топ');
      }
    } finally {
      setLoading(false);
      setLoadingMore(false);
    }
  };

  
  useEffect(() => {
    loadEntries();
    
  }, []);

  return (
    <Layout>
      <div className="space-y-4">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h1 className="text-2xl font-semibold">Топ студентов</h1>
          <QuotaPill bucket="top" />
        </div>
        <p className="text-sm text-neutral-500 dark:text-neutral-400">
          Нажми на участника, чтобы открыть его профиль.
        </p>

        
        <Card className="p-4 space-y-2">
          <div className="flex flex-wrap gap-4 items-end">
            
            <div className="flex flex-col min-w-[220px]">
              <label htmlFor="leaderboard-search" className="text-xs font-medium mb-1">
                Поиск участника
              </label>
              <Input
                id="leaderboard-search"
                type="search"
                placeholder="Имя, фамилия, почта, id"
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                onKeyDown={(e) => { if (e.key === 'Enter') loadEntries({ reset: true }); }}
              />
            </div>

            <div className="flex flex-col min-w-[140px]">
              <label htmlFor="course-filter" className="text-xs font-medium mb-1">
                Курс
              </label>
              <Select
                id="course-filter"
                value={courseId}
                onChange={(e) => setCourseId(e.target.value)}
              >
                <option value="">Все курсы</option>
                {courses.map((c) => (
                  <option key={c.id} value={c.id}>
                    {c.title}
                  </option>
                ))}
              </Select>
            </div>

            
            <div className="flex flex-col w-24">
              <label htmlFor="days-filter" className="text-xs font-medium mb-1">
                За последние, дней
              </label>
              <Input
                id="days-filter"
                type="number"
                min={0}
                placeholder="Напр. 7"
                value={days}
                onChange={(e) => setDays(e.target.value)}
              />
            </div>

            
            <div className="flex flex-col min-w-[180px]">
              <label htmlFor="group-filter" className="text-xs font-medium mb-1">
                Группа
              </label>
              <Select
                id="group-filter"
                value={groupId}
                onChange={(e) => setGroupId(e.target.value)}
                disabled={!groups.length}
              >
                <option value="">Все группы</option>
                {groups.map((g) => (
                  <option key={g.id} value={g.id}>
                    {g.name}
                  </option>
                ))}
              </Select>
            </div>

            
            <Button
              type="button"
              variant="primary"
              className="h-8"
              onClick={() => loadEntries({ reset: true })}
            >
              Применить
            </Button>
          </div>
        </Card>

        {loading && <div>Загрузка…</div>}

        {error && (
          <Card className="border-rose-300 bg-rose-50 text-rose-700">
            <div className="flex items-start gap-2">
              <AlertTriangle size={18} className="mt-0.5" />
              <div className="text-sm whitespace-pre-wrap">{error}</div>
            </div>
          </Card>
        )}

        {!loading && !error && (
          
          <>
            <div className="grid gap-4 md:grid-cols-2">
              {entries.map((e) => (
                <LeaderboardCard key={e.userId} entry={e} />
              ))}
            </div>
            {entries.length > 0 && (
              <div className="mt-4 flex flex-col items-center gap-2">
                <div className="text-xs text-neutral-500">Показано {entries.length}{total ? ` из ${total}` : ''}</div>
                {hasMore && (
                  <Button variant="outline" onClick={() => loadEntries({ reset: false })} disabled={loadingMore}>
                    {loadingMore ? 'Загружаем ещё…' : 'Показать ещё'}
                  </Button>
                )}
              </div>
            )}
          </>
        )}
      </div>
    </Layout>
  );
}
