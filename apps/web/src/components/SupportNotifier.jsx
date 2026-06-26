



import React, { useEffect } from 'react';
import { useAuth } from '../auth/AuthContext';
import { useRoleFlags } from '../contexts/EditorModeContext';
import { useNotify } from './notify/NotifyProvider';
import { notifyOnce } from '../utils/notifyOnce';
import { ensureSupportHubStarted } from '../realtime/supportHub';

const SEEN_PREFIX = 'support_seen_';

function readBool(v) {
  return v === true || v === 'true' || v === 1 || v === '1';
}

function getMsgField(obj, a, b) {
  if (!obj) return undefined;
  return obj[a] ?? obj[b];
}

export default function SupportNotifier() {
  const { access } = useAuth();
  const notify = useNotify();
  const { isAdmin } = useRoleFlags();

  useEffect(() => {
    if (!access) return;
    let disposed = false;
    let conn = null;
    let onReceive = null;

    const setup = async () => {
      try {
        conn = await ensureSupportHubStarted(access);
        if (!conn || disposed) return;

        const joinAll = async () => {
          try {
            await conn.invoke('JoinUser');
            if (isAdmin) await conn.invoke('JoinAdmins');
          } catch {
            
          }
        };

        await joinAll();

        conn.onreconnected(async () => {
          if (disposed) return;
          await joinAll();
        });

        onReceive = (ticketId, msg) => {
          try {
            if (disposed) return;
            const isFromAdmin = readBool(getMsgField(msg, 'isFromAdmin', 'IsFromAdmin'));
            const messageId = getMsgField(msg, 'id', 'Id') || getMsgField(msg, 'createdAt', 'CreatedAt');

            
            const key = `${SEEN_PREFIX}${ticketId}`;
            const lastSeen = localStorage.getItem(key);
            const cur = String(messageId || '');
            if (cur && lastSeen === cur) return;
            if (cur) localStorage.setItem(key, cur);

            if (!isAdmin && isFromAdmin) {
              notifyOnce(
                `support_notify_${ticketId}_${cur}`,
                () => notify.info('Ответ от техподдержки'),
                8000
              );
            }
            if (isAdmin && !isFromAdmin) {
              const author = getMsgField(msg, 'authorName', 'AuthorName') || 'пользователя';
              notifyOnce(
                `support_notify_admin_${ticketId}_${cur}`,
                () => notify.info(`Новое сообщение в поддержке от ${author}`),
                8000
              );
            }
          } catch {
            
          }
        };

        conn.on('ReceiveMessage', onReceive);
      } catch {
        
      }
    };

    setup();

    return () => {
      disposed = true;
      try {
        if (conn && onReceive) conn.off('ReceiveMessage', onReceive);
      } catch {}
    };
  }, [access, notify, isAdmin]);

  return null;
}
