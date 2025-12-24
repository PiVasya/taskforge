// clientapp/src/pages/SupportChatPage.jsx
// Страница переписки по конкретному обращению. Показывает все сообщения
// и позволяет добавить ответ. Подписывается на уведомления через SignalR (пока
// не реализовано) для получения новых сообщений в реальном времени.

import React, { useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import Layout from '../components/Layout';
import { Field, Textarea, Button, Card } from '../components/ui';
import { getSupportTicket, sendSupportMessage } from '../api/support';

export default function SupportChatPage() {
  const { ticketId } = useParams();
  const [ticket, setTicket] = useState(null);
  const [messages, setMessages] = useState([]);
  const [newMessage, setNewMessage] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  // загрузка тикета
  const fetchTicket = async () => {
    try {
      const data = await getSupportTicket(ticketId);
      setTicket(data.ticket);
      setMessages(data.messages);
    } catch (err) {
      setError(err?.message || 'Ошибка загрузки сообщения');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    fetchTicket();
    // TODO: Подписка на SignalR для новых сообщений
  }, [ticketId]);

  const send = async (e) => {
    e.preventDefault();
    if (!newMessage.trim()) return;
    try {
      await sendSupportMessage(ticketId, { message: newMessage });
      // локально добавляем пока без real-time
      setMessages([
        ...messages,
        {
          id: `tmp-${Date.now()}`,
          text: newMessage,
          createdAt: new Date().toISOString(),
          isFromAdmin: false,
        },
      ]);
      setNewMessage('');
    } catch (err) {
      setError(err?.message || 'Не удалось отправить сообщение');
    }
  };

  return (
    <Layout>
      <div className="max-w-3xl mx-auto">
        <h1 className="text-2xl font-semibold mb-4">Обращение #{ticketId}</h1>
        {error && <div className="text-red-500 mb-4">{error}</div>}
        {loading ? (
          <div>Загрузка…</div>
        ) : (
          <Card>
            <div className="space-y-4 mb-4">
              {messages.map((m, idx) => (
                <div key={idx} className={m.isFromAdmin ? 'text-right' : 'text-left'}>
                  <div
                    className={
                      m.isFromAdmin
                        ? 'bg-slate-100 dark:bg-slate-800 inline-block p-3 rounded-xl'
                        : 'bg-brand-100 dark:bg-brand-900 inline-block p-3 rounded-xl'
                    }
                  >
                    {m.text}
                  </div>
                  <div className="text-xs text-slate-500 mt-1">
                    {new Date(m.createdAt).toLocaleString()}
                  </div>
                </div>
              ))}
            </div>
            <form onSubmit={send} className="space-y-2">
              <Field label="Ваш ответ">
                <Textarea
                  value={newMessage}
                  onChange={(e) => setNewMessage(e.target.value)}
                  rows={4}
                  placeholder="Введите ваш ответ…"
                />
              </Field>
              <div className="flex justify-end">
                <Button type="submit">Отправить</Button>
              </div>
            </form>
          </Card>
        )}
      </div>
    </Layout>
  );
}