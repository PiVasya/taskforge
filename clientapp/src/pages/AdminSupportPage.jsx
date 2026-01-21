// clientapp/src/pages/AdminSupportPage.jsx
// Админская страница: список всех обращений.

import React, { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import Layout from '../components/Layout';
import { Card } from '../components/ui';
import { listSupportTickets } from '../api/support';
import { useNotify } from '../components/notify/NotifyProvider';

export default function AdminSupportPage() {
  const notify = useNotify();
  const [tickets, setTickets] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  useEffect(() => {
    (async () => {
      try {
        const data = await listSupportTickets();
        setTickets(data);
      } catch (err) {
        const msg = err?.message || 'Ошибка загрузки';
        setError(msg);
        notify.error(msg);
      } finally {
        setLoading(false);
      }
    })();
  }, [notify]);

  return (
    <Layout>
      <div className="max-w-4xl mx-auto">
        <h1 className="text-2xl font-semibold mb-4">Обращения пользователей</h1>
        {error && <div className="text-red-500 mb-4">{error}</div>}
        <Card>
          {loading ? (
            <div>Загрузка…</div>
          ) : tickets.length === 0 ? (
            <div>Нет обращений.</div>
          ) : (
            <ul className="divide-y divide-slate-200 dark:divide-slate-800">
              {tickets.map((t) => (
                <li key={t.id} className="p-4 flex justify-between items-center">
                  <div>
                    <div className="font-semibold">#{String(t.id).slice(0, 8)}</div>
                    <div className="text-sm text-slate-500 dark:text-slate-400">
                      Тип: {t.type} · {t.isClosed ? 'закрыто' : 'открыто'}
                    </div>
                    {t.user && (
                      <div className="text-xs text-slate-400 dark:text-slate-500">
                        Пользователь: {t.user?.firstName} {t.user?.lastName}
                        {t.user?.email ? ` (${t.user.email})` : ''}
                      </div>
                    )}
                    {t.lastMessagePreview && (
                      <div className="text-xs text-slate-400 dark:text-slate-500 mt-1 max-w-[44rem]">
                        {t.lastMessagePreview}
                      </div>
                    )}
                    <div className="text-xs text-slate-400 dark:text-slate-500">
                      Обновлено {t.updatedAt ? new Date(t.updatedAt).toLocaleString() : '—'}
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
