import React, { useState, useEffect } from 'react';
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

  const fetchTicket = () => {
    getSupportTicket(ticketId)
      .then((data) => {
        setTicket(data.ticket);
        setMessages(data.messages);
      })
      .catch((e) => setError(e.message))
      .finally(() => setLoading(false));
  };

  useEffect(() => {
    fetchTicket();
  }, [ticketId]);

  const send = async (e) => {
    e.preventDefault();
    if (!newMessage.trim()) return;
    try {
      await sendSupportMessage(ticketId, { message: newMessage });
      // Добавляем локально, чтобы отобразилось без перезагрузки
      setMessages([
        ...messages,
        {
          id: 'tmp-' + Date.now(),
          text: newMessage,
          createdAt: new Date().toISOString(),
          isFromAdmin: false,
        },
      ]);
      setNewMessage('');
    } catch (e) {
      setError(e.message);
    }
  };

  return (
    <Layout>
      <div className="max-w-3xl mx-auto">
        <h1 className="text-2xl font-semibold mb-4">Обращение #{ticketId}</h1>
        {error && <div className="mb-4 text-red-500">{error}</div>}
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
                  placeholder="Введите ответ…"
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
