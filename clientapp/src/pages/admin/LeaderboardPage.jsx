import React, { useEffect, useState } from 'react';
import Layout from '../components/Layout';
import { getLeaderboard } from '../api/leaderboard';
import LeaderboardCard from '../components/LeaderboardCard';

export default function LeaderboardPage() {
  const [entries, setEntries] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);

  useEffect(() => {
    (async () => {
      try {
        setLoading(true);
        const data = await getLeaderboard(); // твой API-метод
        setEntries(data || []);
      } catch (e) {
        console.error(e);
        setError('Не удалось загрузить топ');
      } finally {
        setLoading(false);
      }
    })();
  }, []);

  return (
    <Layout>
      <div className="space-y-4">
        <h1 className="text-2xl font-semibold">Топ студентов</h1>
        <p className="text-sm text-slate-500 dark:text-slate-400">
          Нажми на участника, чтобы открыть его профиль.
        </p>

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
