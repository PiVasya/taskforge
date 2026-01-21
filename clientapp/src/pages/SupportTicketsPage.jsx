// clientapp/src/pages/SupportTicketsPage.jsx
// Страница «Мои обращения». Показывает список тикетов и позволяет
// перейти к переписке или создать новое обращение.

import React, { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import Layout from '../components/Layout';
import { Card } from '../components/ui';
import { listSupportTickets } from '../api/support';
import { useNotify } from '../components/notify/NotifyProvider';

export default function SupportTicketsPage() {
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
        const msg = err?.message || 'Ошибка загрузки обращений';
        setError(msg);
        notify.error(msg);
      } finally {
        setLoading(false);
      }
    })();
  }, [notify]);

  return (
    <Layout>
      <div className="max-w-3xl mx-auto">
        <div className="flex justify-between items-center mb-4">
          <h1 className="text-2xl font-semibold">Мои обращения</h1>
          <Link to="/support/new" className="btn-primary">Новое обращение</Link>
        </div>

        {error && <div className="text-red-500 mb-4">{error}</div>}

        <Card>
          {loading ? (
            <div>Загрузка…</div>
          ) : tickets.length === 0 ? (
            <div>У вас ещё нет обращений.</div>
          ) : (
            <ul className="divide-y divide-slate-200 dark:divide-slate-800">
              {tickets.map((t) => (
                <li key={t.id} className="p-4 flex justify-between items-center">
                  <div>
                    <div className="font-semibold">#{String(t.id).slice(0, 8)}</div>
                    <div className="text-sm text-slate-500 dark:text-slate-400">
                      Тип: {t.type} · {t.isClosed ? 'закрыто' : 'открыто'} · сообщений: {t.messagesCount ?? '—'}
                    </div>
                    {t.lastMessagePreview ? (
                      <div className="text-sm text-slate-600 dark:text-slate-300 mt-1 line-clamp-2">
                        {t.lastMessagePreview}
                      </div>
                    ) : null}
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
