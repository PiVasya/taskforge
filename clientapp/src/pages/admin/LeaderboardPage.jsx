// LeaderboardPage.jsx — обновлённая финальная версия
import React, { useEffect, useState } from 'react';
import Layout from '../../components/Layout';
import { getLeaderboard } from '../../api/leaderboard';
import { getCourses } from '../../api/courses';
import LeaderboardCard from '../../components/LeaderboardCard';

// UI
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

  // load courses once
  useEffect(() => {
    (async () => {
      try {
        const list = await getCourses();
        setCourses(Array.isArray(list) ? list : []);
      } catch (e) {
        console.error('Failed to load courses', e);
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

            {/* Courses */}
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

            {/* Days */}
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

            {/* Group (disabled placeholder) */}
            <div className="flex flex-col w-32">
              <label htmlFor="group-filter" className="text-xs font-medium mb-1">
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

            {/* Apply button */}
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

        {/* Loading */}
        {loading && <div>Загрузка…</div>}

        {/* Error */}
        {error && (
          <div className="text-sm text-red-500 bg-red-50 dark:bg-red-900/20 px-3 py-2 rounded-xl">
            {error}
          </div>
        )}

        {/* List */}
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
