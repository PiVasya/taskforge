import React, { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { Card } from '../components/ui';
import { listSupportTickets } from '../api/support';
import { useNotify } from '../components/notify/NotifyProvider';
import AppErrorPanel from '../components/AppErrorPanel';
import { handleApiError } from '../utils/handleApiError';
import { ContextMenu, ContextMenuItem, ContextMenuLabel, ContextMenuSeparator } from '../components/ui/ContextMenu';
import { Copy, ExternalLink, UserCog } from 'lucide-react';

function userLabel(user) {
  if (!user) return 'Пользователь';
  return user.displayName || user.fullName || [user.firstName, user.lastName].filter(Boolean).join(' ') || user.login || user.email || user.maskedEmail || 'Пользователь';
}

export default function AdminSupportPage() {
  const notify = useNotify();
  const navigate = useNavigate();
  const [chats, setChats] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(null);
  const [contextMenu, setContextMenu] = useState({ open: false, x: 0, y: 0, chat: null });

  useEffect(() => {
    (async () => {
      try {
        const data = await listSupportTickets();
        setChats(Array.isArray(data) ? data : []);
      } catch (err) {
        const parsed = handleApiError(err, notify, 'Не удалось загрузить чаты поддержки');
        setError(parsed);
      } finally {
        setLoading(false);
      }
    })();
  }, [notify]);

  const totals = useMemo(() => ({
    chats: chats.length,
    messages: chats.reduce((sum, chat) => sum + Number(chat.messagesCount || 0), 0),
  }), [chats]);

  const closeContextMenu = () => setContextMenu((current) => current.open ? { ...current, open: false } : current);
  const openContextMenu = (event, chat) => {
    event.preventDefault();
    event.stopPropagation();
    setContextMenu({ open: true, x: event.clientX, y: event.clientY, chat });
  };
  const copyValue = async (value, label) => {
    if (!value) return;
    try { await navigator.clipboard.writeText(String(value)); notify.success(`${label} скопирован`); }
    catch { notify.warn('Не удалось скопировать'); }
  };

  return (
    <>
      <div className="max-w-5xl mx-auto">
        <div className="mb-5">
          <h1 className="text-2xl font-semibold">Чаты поддержки</h1>
          <p className="mt-1 text-sm text-neutral-500 dark:text-neutral-400">
            Один пользователь — один постоянный чат. Темы и технические ID пользователям не показываются.
          </p>
        </div>

        {error ? <div className="mb-4"><AppErrorPanel error={error} title="Не удалось загрузить чаты поддержки" /></div> : null}

        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3 mb-4">
          <Card>
            <div className="text-sm text-neutral-500 dark:text-neutral-400">Диалогов</div>
            <div className="text-3xl font-semibold">{totals.chats}</div>
          </Card>
          <Card>
            <div className="text-sm text-neutral-500 dark:text-neutral-400">Сообщений</div>
            <div className="text-3xl font-semibold">{totals.messages}</div>
          </Card>
        </div>

        <Card>
          {loading ? (
            <div>Загрузка…</div>
          ) : chats.length === 0 ? (
            <div className="text-neutral-500 dark:text-neutral-400">Пока нет чатов поддержки.</div>
          ) : (
            <ul className="divide-y divide-neutral-200 dark:divide-neutral-800">
              {chats.map((chat) => {
                const user = chat.user;
                const name = userLabel(user);
                const chatId = chat.chatId || chat.ticketId || chat.id;
                return (
                  <li key={chatId} className="py-4 flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3" onContextMenu={(event) => openContextMenu(event, chat)}>
                    <div className="min-w-0">
                      <div className="font-semibold text-neutral-900 dark:text-neutral-100">{name}</div>
                      <div className="text-sm text-neutral-500 dark:text-neutral-400">
                        {user?.login ? `@${user.login}` : 'логин не указан'}
                        {user?.email || user?.maskedEmail ? ` · ${user.email || user.maskedEmail}` : ''}
                        {chat.messagesCount != null ? ` · сообщений: ${chat.messagesCount}` : ''}
                      </div>
                      {chat.lastMessagePreview ? (
                        <div className="text-sm text-neutral-700 dark:text-neutral-300 mt-1 line-clamp-2 max-w-3xl">
                          {chat.lastMessagePreview}
                        </div>
                      ) : (
                        <div className="text-sm text-neutral-500 dark:text-neutral-400 mt-1">Сообщений ещё нет.</div>
                      )}
                      <div className="text-xs text-neutral-400 dark:text-neutral-500 mt-1">
                        Обновлено {chat.updatedAt ? new Date(chat.updatedAt).toLocaleString() : '—'}
                      </div>
                    </div>
                    <Link to={`/admin/support/${chatId}`} className="btn-outline shrink-0">Открыть чат</Link>
                  </li>
                );
              })}
            </ul>
          )}
        </Card>
      </div>

      <ContextMenu open={contextMenu.open} x={contextMenu.x} y={contextMenu.y} onClose={closeContextMenu} ariaLabel="Действия чата поддержки">
        {contextMenu.chat ? (() => {
          const chat = contextMenu.chat;
          const user = chat.user || {};
          const chatId = chat.chatId || chat.ticketId || chat.id;
          return (
            <>
              <ContextMenuLabel>Чат поддержки</ContextMenuLabel>
              <ContextMenuItem icon={ExternalLink} onClick={() => { closeContextMenu(); navigate(`/admin/support/${chatId}`); }}>Открыть чат</ContextMenuItem>
              {user.id ? <ContextMenuItem icon={UserCog} onClick={() => { closeContextMenu(); navigate(`/admin/users/${user.id}`); }}>Открыть пользователя</ContextMenuItem> : null}
              <ContextMenuSeparator />
              <ContextMenuItem icon={Copy} disabled={!user.login} onClick={() => { closeContextMenu(); void copyValue(user.login, 'Логин'); }}>Скопировать логин</ContextMenuItem>
              <ContextMenuItem icon={Copy} disabled={!(user.email || user.maskedEmail)} onClick={() => { closeContextMenu(); void copyValue(user.email || user.maskedEmail, 'Email'); }}>Скопировать email</ContextMenuItem>
            </>
          );
        })() : null}
      </ContextMenu>
    </>
  );
}
