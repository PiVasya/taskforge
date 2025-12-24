import React, { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import Layout from '../components/Layout';
import { Card } from '../components/ui';
import { listSupportTickets } from '../api/support';

export default function SupportTicketsPage() {
  const [tickets, setTickets] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  useEffect(() => {
    listSupportTickets()
      .then(setTickets)
      .catch((e) => setError(e.message))
      .finally(() => setLoading(false));
  }, []);

  return (
    <Layout>
      <div className="max-w-3xl mx-auto">
        <div className="flex justify-between items-center mb-4">
          <h1 className="text-2xl font-semibold">Мои обращения</h1>
          <Link to="/support/new" className="btn-primary">Новое обращение</Link>
        </div>
        {error && <div className="mb-4 text-red-500">{error}</div>}
        <Card>
          {loading ? (
            <div>Загрузка…</div>
          ) : tickets.length === 0 ? (
            <div>Обращений пока нет.</div>
          ) : (
            <ul className="space-y-3">
              {tickets.map((t) => (
                <li key={t.id} className="border border-slate-200 dark:border-slate-700 rounded p-3 flex justify-between items-center">
                  <div>
                    <div className="font-semibold">#{t.id.slice(0, 8)}</div>
                    <div className="text-sm text-slate-600 dark:text-slate-400">Тип: {t.type}</div>
                    <div className="text-xs text-slate-500 dark:text-slate-500">
                      Последнее обновление: {new Date(t.updatedAt).toLocaleString()}
                    </div>
                  </div>
                    <Link to={`/support/${t.id}`} className="btn-outline">Открыть</Link>
                </li>
              ))}
            </ul>
          )}
        </Card>
      </div>
    </Layout>
  );
}
