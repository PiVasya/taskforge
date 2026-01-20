// clientapp/src/components/SupportNotifier.jsx
// Глобальный poller для уведомлений: если в обращении появился новый ответ
// от техподдержки, показываем всплывающее уведомление.

import React, { useEffect } from 'react';
import { useLocation } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { useNotify } from './notify/NotifyProvider';
import { getSupportTicket, listSupportTickets } from '../api/support';
import { notifyOnce } from '../utils/notifyOnce';

const SEEN_PREFIX = 'support_seen_';

function safeDate(v) {
  try {
    const d = new Date(v);
    if (Number.isNaN(d.getTime())) return null;
    return d;
  } catch {
    return null;
  }
}

function pickLastMessage(messages) {
  if (!Array.isArray(messages) || messages.length === 0) return null;
  return [...messages]
    .sort((a, b) => new Date(a.createdAt) - new Date(b.createdAt))
    .at(-1);
}

export default function SupportNotifier() {
  const { access } = useAuth();
  const notify = useNotify();
  const location = useLocation();

  useEffect(() => {
    if (!access) return;
    // Чтобы не "тыкать" саппорт постоянно: включаем poller только на страницах техподдержки.
    const p = location?.pathname || '';
    const shouldPoll = p.startsWith('/support') || p.startsWith('/admin/support');
    if (!shouldPoll) return;
    let stopped = false;

    const tick = async () => {
      try {
        const tickets = await listSupportTickets();
        if (stopped || !Array.isArray(tickets)) return;

        for (const t of tickets) {
          const id = t?.id;
          if (!id) continue;
          const updatedAt = safeDate(t.updatedAt);
          if (!updatedAt) continue;

          const key = `${SEEN_PREFIX}${id}`;
          const seenRaw = localStorage.getItem(key);
          const seenAt = safeDate(seenRaw);

          // первый раз просто запоминаем — без уведомлений
          if (!seenAt) {
            localStorage.setItem(key, updatedAt.toISOString());
            continue;
          }

          // если обращение обновилось — проверяем, от кого последнее сообщение
          if (updatedAt > seenAt) {
            // обновляем сразу, чтобы не спамить, даже если запрос упадёт
            localStorage.setItem(key, updatedAt.toISOString());

            const data = await getSupportTicket(id);
            if (stopped) return;
            const last = pickLastMessage(data?.messages);

            if (last?.isFromAdmin) {
              const shortId = String(id).slice(0, 8);
              const uniq = `${id}_${last?.id || updatedAt.toISOString()}`;
              notifyOnce(
                `support_notify_${uniq}`,
                () => notify.info(`Ответ от техподдержки в обращении #${shortId}`),
                8000
              );
            }
          }
        }
      } catch {
        // молча: уведомления не должны ломать приложение
      }
    };

    // стартуем сразу и дальше периодически
    tick();
    const t = setInterval(tick, 60000);

    return () => {
      stopped = true;
      clearInterval(t);
    };
  }, [access, notify, location]);

  return null;
}
