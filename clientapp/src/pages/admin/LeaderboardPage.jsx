// modified LeaderboardPage.jsx adds filtering by course, days and group and improves UI
import React, { useEffect, useState } from 'react';
import Layout from '../../components/Layout';
import { getLeaderboard } from '../../api/leaderboard';
import { getCourses } from '../../api/courses';
import LeaderboardCard from '../../components/LeaderboardCard';
// Подключаем UI-компоненты для единообразного оформления фильтров
import { Card, Input, Select, Button } from '../../components/ui';

export default function LeaderboardPage() {
  const [entries, setEntries] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);

  // filters
  const [courses, setCourses] = useState([]);
  const [courseId, setCourseId] = useState('');
  const [days, setDays] = useState('');
  const [groupId, setGroupId] = useState('');

  // load list of courses once
  useEffect(() => {
    (async () => {
      try {
        const list = await getCourses();
        setCourses(Array.isArray(list) ? list : []);
      } catch (e) {
        console.error('Failed to load courses', e);
        // ignore
      }
    })();
  }, []);

  const loadEntries = async () => {
    try {
      setLoading(true);
      setError(null);
      const params = {};
      if (courseId) params.courseId = courseId;
      // convert days to integer if provided
      const daysInt = parseInt(days, 10);
      if (!Number.isNaN(daysInt) && daysInt > 0) params.days = daysInt;
      if (groupId) params.groupId = groupId;
      // ask backend to return up to 100 entries
      params.top = 100;
      const data = await getLeaderboard(params);
      setEntries(Array.isArray(data) ? data : []);
    } catch (e) {
      console.error(e);
      setError('Не удалось загрузить топ');
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
            {/* выбор курса */}
            <div className="flex flex-col min-w-[140px]">
              <label className="text-xs font-medium mb-1" htmlFor="course-filter">
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
            {/* фильтр по количеству дней */}
            <div className="flex flex-col w-24">
              <label className="text-xs font-medium mb-1" htmlFor="days-filter">
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
            {/* поле для групп (пока недоступно) */}
            <div className="flex flex-col w-32">
              <label className="text-xs font-medium mb-1" htmlFor="group-filter">
                Группа
              </label>
              <Input
                id="group-filter"
                type="text"
                placeholder="Скоро"
                value={groupId}
                onChange={(e) => setGroupId(e.target.value)}
                disabled
              />
            </div>
            {/* кнопка */}
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
          <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
            {entries.map((e) => (
              <LeaderboardCard key={e.userId} entry={e} />
            ))}
          </div>
        )}
      </div>
    </Layout>
  );
}