// clientapp/src/pages/AdminSupportPage.jsx
// Админская страница для просмотра всех тикетов поддержки. Позволяет
// открывать тикеты и отвечать в них. Доступна только для администраторов.

import React, { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import Layout from '../components/Layout';
import { Card } from '../components/ui';
import { listSupportTickets } from '../api/support';

// Пока админская и пользовательская страницы используют один и тот же API.
// Но здесь админ видит все тикеты, а не только свои. Для этого сервер
// возвращает полный список, проверяя роль пользователя.

export default function AdminSupportPage() {
  const [tickets, setTickets] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  useEffect(() => {
    (async () => {
      try {
        const data = await listSupportTickets();
        setTickets(data);
      } catch (err) {
        setError(err?.message || 'Ошибка загрузки');
      } finally {
        setLoading(false);
      }
    })();
  }, []);

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
                    <div className="font-semibold">#{t.id?.slice(0, 8)}</div>
                    <div className="text-sm text-slate-500 dark:text-slate-400">Тип: {t.type}</div>
                    <div className="text-xs text-slate-400 dark:text-slate-500">
                      Пользователь: {t.user?.firstName} {t.user?.lastName}
                    </div>
                    <div className="text-xs text-slate-400 dark:text-slate-500">
                      Обновлено {new Date(t.updatedAt).toLocaleString()}
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