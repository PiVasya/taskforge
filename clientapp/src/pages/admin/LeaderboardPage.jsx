import React, { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import Layout from '../../components/Layout';
import { Card, Select } from '../../components/ui';
import { getLeaderboard } from '../../api/admin';
import { api } from '../../api/http';

/**
 * Leaderboard page for administrators. Shows a ranking of students by the number
 * of tasks solved. Users can filter by course and by period (days) and view
 * the top N students. This page depends on admin API endpoints defined in
 * `clientapp/src/api/admin.js`.
 */
export default function LeaderboardPage() {
  const [courses, setCourses] = useState([]);
  const [courseId, setCourseId] = useState('');
  const [days, setDays] = useState('30');
  const [rows, setRows] = useState([]);
  const nav = useNavigate();

  // Load available courses on mount.
  useEffect(() => {
    (async () => {
      try {
        const { data } = await api.get('/api/courses');
        setCourses(data || []);
      } catch (err) {
        console.error('Failed to load courses', err);
      }
    })();
  }, []);

  // Load leaderboard whenever course or days filter changes
  useEffect(() => {
    (async () => {
      try {
        const data = await getLeaderboard({
          courseId: courseId || undefined,
          days: days ? Number(days) : undefined,
          top: 50,
        });
        setRows(data || []);
      } catch (err) {
        console.error('Failed to load leaderboard', err);
      }
    })();
  }, [courseId, days]);

  return (
    <Layout>
      <div className="container-app py-6 space-y-4">
        <h1 className="text-2xl font-semibold">Топ</h1>

        <Card className="p-4 grid grid-cols-1 md:grid-cols-3 gap-3">
          <div>
            <label className="label">Курс</label>
            <Select value={courseId} onChange={(e) => setCourseId(e.target.value)}>
              <option value="">Все курсы</option>
              {courses.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.title}
                </option>
              ))}
            </Select>
          </div>
          <div>
            <label className="label">Период</label>
            <Select value={days} onChange={(e) => setDays(e.target.value)}>
              <option value="7">7 дней</option>
              <option value="30">30 дней</option>
              <option value="90">90 дней</option>
              <option value="">За всё время</option>
            </Select>
          </div>
        </Card>

        <Card className="p-0 overflow-x-auto">
          <table className="min-w-full text-sm">
            <thead className="bg-slate-50 dark:bg-slate-800/50">
              <tr>
                <th className="th w-16">#</th>
                <th className="th">Студент</th>
                <th className="th">Email</th>
                <th className="th text-right">Решено</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((r, i) => (
                <tr key={r.userId} className="border-t">
                  <td className="td">{i + 1}</td>
                  <td className="td">{r.lastName} {r.firstName}</td>
                  <td className="td">{r.email}</td>
                  <td className="td text-right font-medium">{r.solved}</td>
                </tr>
              ))}
              {!rows.length && (
                <tr>
                  <td className="td" colSpan={4}>Нет данных</td>
                </tr>
              )}
            </tbody>
          </table>
        </Card>
      </div>
    </Layout>
  );
}
