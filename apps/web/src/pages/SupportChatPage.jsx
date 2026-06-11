



import React, { useEffect, useMemo, useRef, useState } from 'react';
import { useParams } from 'react-router-dom';
import Layout from '../components/Layout';
import { Field, Textarea, Button, Card } from '../components/ui';
import { getSupportTicket, sendSupportMessage } from '../api/support';
import { useNotify } from '../components/notify/NotifyProvider';
import AppErrorPanel from '../components/AppErrorPanel';
import { handleApiError } from '../utils/handleApiError';
import { notifyOnce } from '../utils/notifyOnce';
import { useAuth } from '../auth/AuthContext';
import { ensureSupportHubStarted } from '../realtime/supportHub';

function pickLastMessage(messages) {
  if (!Array.isArray(messages) || messages.length === 0) return null;
  
  return [...messages].sort((a, b) => new Date(a.createdAt) - new Date(b.createdAt)).at(-1);
}

export default function SupportChatPage() {
  const { ticketId } = useParams();
  const notify = useNotify();
  const { access } = useAuth();

  const [ticket, setTicket] = useState(null);
  const [messages, setMessages] = useState([]);
  const [newMessage, setNewMessage] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);

  const lastMessageIdRef = useRef(null);
  const isMountedRef = useRef(true);

  const title = useMemo(() => ticket?.subject || ticket?.title || 'Обращение', [ticket?.subject, ticket?.title]);

  const fetchTicket = async ({ silent = false } = {}) => {
    try {
      const data = await getSupportTicket(ticketId);
      if (!isMountedRef.current) return;

      setTicket(data.ticket);
      setMessages(data.messages || []);

      const last = pickLastMessage(data.messages);
      const lastId = last?.id || `${last?.createdAt || ''}-${last?.text || ''}`;
      const prev = lastMessageIdRef.current;
      lastMessageIdRef.current = lastId;

      
      if (prev && last && lastId !== prev && last.isFromAdmin) {
        notifyOnce(
          `support_msg_${ticketId}_${lastId}`,
          () => notify.info(`Техподдержка ответила в ${title}`),
          6000
        );
      }
    } catch (err) {
      if (!silent) {
        const parsed = handleApiError(err, notify, 'Не удалось загрузить переписку');
        setError(parsed);
      }
    } finally {
      if (!silent) setLoading(false);
    }
  };

  useEffect(() => {
    isMountedRef.current = true;
    setLoading(true);
    setError(null);
    lastMessageIdRef.current = null;

    fetchTicket();

    let conn = null;
    let disposed = false;
    let onReceive = null;

    const setupRealtime = async () => {
      try {
        if (!access) return;
        conn = await ensureSupportHubStarted(access);
        if (!conn || disposed) return;

        const join = async () => {
          try {
            await conn.invoke('JoinTicket', ticketId);
          } catch {
            
          }
        };

        await join();
        conn.onreconnected(async () => {
          if (disposed) return;
          await join();
        });

        onReceive = (incomingTicketId, msg) => {
          if (!isMountedRef.current) return;
          if (String(incomingTicketId) !== String(ticketId)) return;

          
          const m = {
            id: msg?.id ?? msg?.Id,
            text: msg?.text ?? msg?.Text,
            createdAt: msg?.createdAt ?? msg?.CreatedAt,
            isFromAdmin: msg?.isFromAdmin ?? msg?.IsFromAdmin,
            authorName: msg?.authorName ?? msg?.AuthorName,
          };

          setMessages((prev) => {
            
            if (m.id && prev.some((x) => x.id === m.id)) return prev;
            return [...prev, m].sort((a, b) => new Date(a.createdAt) - new Date(b.createdAt));
          });

          const lastId = m?.id || `${m?.createdAt || ''}-${m?.text || ''}`;
          const prev = lastMessageIdRef.current;
          lastMessageIdRef.current = lastId;

          
          if (prev && m && m.isFromAdmin) {
            notifyOnce(
              `support_msg_${ticketId}_${lastId}`,
              () => notify.info(`Техподдержка ответила в ${title}`),
              6000
            );
          }
        };

        conn.on('ReceiveMessage', onReceive);
      } catch {
        
      }
    };

    setupRealtime();

    return () => {
      isMountedRef.current = false;
      disposed = true;
      try {
        if (conn) {
          if (onReceive) conn.off('ReceiveMessage', onReceive);
          conn.invoke('LeaveTicket', ticketId).catch(() => {});
        }
      } catch {}
    };
    
  }, [ticketId, access]);

  const send = async (e) => {
    e.preventDefault();
    const txt = newMessage.trim();
    if (!txt) return;

    try {
      setError(null);
      await sendSupportMessage(ticketId, { message: txt });
      setNewMessage('');
      notify.success('Сообщение отправлено');
      
      await fetchTicket({ silent: true });
    } catch (err) {
      const parsed = handleApiError(err, notify, 'Не удалось отправить сообщение');
      setError(parsed);
    }
  };

  return (
    <Layout>
      <div className="max-w-3xl mx-auto">
        <h1 className="text-2xl font-semibold mb-4">{title}</h1>
        {error ? <div className="mb-4"><AppErrorPanel error={error} title="Проблема в переписке с поддержкой" /></div> : null}

        {loading ? (
          <div>Загрузка…</div>
        ) : (
          <Card>
            <div className="space-y-4 mb-4">
              {messages.length === 0 ? (
                <div className="text-neutral-500 dark:text-neutral-400">Пока нет сообщений.</div>
              ) : (
                messages.map((m) => (
                  <div key={m.id || `${m.createdAt}-${m.text}`} className={m.isFromAdmin ? 'text-right' : 'text-left'}>
                    <div
                      className={
                        m.isFromAdmin
                          ? 'bg-neutral-100 dark:bg-neutral-800 inline-block p-3 rounded-xl'
                          : 'bg-brand-100 dark:bg-brand-900 inline-block p-3 rounded-xl'
                      }
                    >
                      {m.text}
                    </div>
                    <div className="text-xs text-neutral-500 mt-1">{m.createdAt ? new Date(m.createdAt).toLocaleString() : ''}</div>
                  </div>
                ))
              )}
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
