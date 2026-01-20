// LeaderboardPage.jsx — версия с фильтрами и сеткой максимум из 2 колонок

import React, { useEffect, useState } from 'react';
import Layout from '../../components/Layout';
import { getLeaderboard } from '../../api/leaderboard';
import { getCourses } from '../../api/courses';
import { getGroups } from '../../api/groups';
import LeaderboardCard from '../../components/LeaderboardCard';
import { Card, Input, Select, Button } from '../../components/ui';

export default function LeaderboardPage() {
  const [entries, setEntries] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);

  // filters
  const [courses, setCourses] = useState([]);
  const [groups, setGroups] = useState([]);
  const [courseId, setCourseId] = useState('');
  const [days, setDays] = useState('');
  const [groupId, setGroupId] = useState('');

  // load courses once
  useEffect(() => {
    (async () => {
      try {
        const list = await getCourses();
        setCourses(Array.isArray(list) ? list : []);
      } catch (e) {
        console.error('Failed to load courses', e);
      }
      try {
        const gs = await getGroups();
        setGroups(Array.isArray(gs) ? gs : []);
      } catch (e) {
        console.error('Failed to load groups', e);
      }
    })();
  }, []);

  const loadEntries = async () => {
    try {
      setLoading(true);
      setError(null);

      const params = {};

      if (courseId) params.courseId = courseId;

      const daysInt = parseInt(days, 10);
      if (!Number.isNaN(daysInt) && daysInt > 0) {
        params.days = daysInt;
      }

      if (groupId) params.groupId = groupId;

      params.top = 100;

      const data = await getLeaderboard(params);
      setEntries(Array.isArray(data) ? data : []);
    } catch (e) {
      console.error(e);
      if (e?.response?.status === 429) {
        const ra = e.response?.data?.retryAfterSeconds;
        const msg = e.response?.data?.message || 'Топ можно обновлять раз в 5 минут';
        setError(ra ? `${msg}. Повтори через ~${Math.ceil(ra / 60)} мин.` : msg);
      } else {
        setError('Не удалось загрузить топ');
      }
    } finally {
      setLoading(false);
    }
  };

  // initial load
  useEffect(() => {
    loadEntries();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <Layout>
      <div className="space-y-4">
        <h1 className="text-2xl font-semibold">Топ студентов</h1>
        <p className="text-sm text-slate-500 dark:text-slate-400">
          Нажми на участника, чтобы открыть его профиль.
        </p>

        {/* Фильтры */}
        <Card className="p-4 space-y-2">
          <div className="flex flex-wrap gap-4 items-end">
            {/* Курс */}
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

            {/* Дни */}
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

            {/* Группа */}
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

            {/* Кнопка */}
            <Button
              type="button"
              variant="primary"
              className="h-8"
              onClick={loadEntries}
            >
              Применить
            </Button>
          </div>
        </Card>

        {loading && <div>Загрузка…</div>}

        {error && (
          <div className="text-sm text-red-500 bg-red-50 dark:bg-red-900/20 px-3 py-2 rounded-xl">
            {error}
          </div>
        )}

        {!loading && !error && (
          // максимум 2 человека в строку: 1 колонка на мобиле, 2 — на шире md
          <div className="grid gap-4 md:grid-cols-2">
            {entries.map((e) => (
              <LeaderboardCard key={e.userId} entry={e} />
            ))}
          </div>
        )}
      </div>
    </Layout>
  );
}
