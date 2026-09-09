import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link, useLocation, useParams } from 'react-router-dom';
import { Field, Textarea, Button, Card } from '../components/ui';
import { ContextMenu, ContextMenuItem, ContextMenuLabel, ContextMenuSeparator, claimContextMenuEvent } from '../components/ui/ContextMenu';
import { Copy, Reply } from 'lucide-react';
import { getSupportChat, getSupportTicket, sendSupportChatMessage, sendSupportMessage } from '../api/support';
import { useNotify } from '../components/notify/NotifyProvider';
import AppErrorPanel from '../components/AppErrorPanel';
import { handleApiError } from '../utils/handleApiError';
import { notifyOnce } from '../utils/notifyOnce';
import { useAuth } from '../auth/AuthContext';
import { ensureSupportHubStarted } from '../realtime/supportHub';

function normalizeMessage(raw) {
  if (!raw) return null;
  const reply = raw.replyTo || raw.ReplyTo || null;
  return {
    id: raw.id ?? raw.Id ?? raw.messageId ?? raw.MessageId,
    text: raw.text ?? raw.Text ?? raw.body ?? raw.Body ?? '',
    createdAt: raw.createdAt ?? raw.CreatedAt ?? raw.createdAtUtc ?? raw.CreatedAtUtc,
    isFromAdmin: raw.isFromAdmin ?? raw.IsFromAdmin ?? String(raw.authorRole ?? raw.AuthorRole ?? '').toLowerCase() === 'admin',
    authorName: raw.authorName ?? raw.AuthorName ?? raw.authorDisplayName ?? raw.AuthorDisplayName,
    authorLogin: raw.authorLogin ?? raw.AuthorLogin,
    authorEmail: raw.authorEmail ?? raw.AuthorEmail,
    source: raw.source ?? raw.Source,
    replyToMessageId: raw.replyToMessageId ?? raw.ReplyToMessageId,
    replyTo: reply
      ? {
          id: reply.id ?? reply.Id ?? reply.messageId ?? reply.MessageId,
          textPreview: reply.textPreview ?? reply.TextPreview ?? reply.text ?? reply.Text ?? '',
          authorName: reply.authorName ?? reply.AuthorName,
          authorRole: reply.authorRole ?? reply.AuthorRole,
        }
      : null,
  };
}

function pickLastMessage(messages) {
  if (!Array.isArray(messages) || messages.length === 0) return null;
  return [...messages].sort((a, b) => new Date(a.createdAt) - new Date(b.createdAt)).at(-1);
}

function userLabel(user) {
  if (!user) return 'Пользователь';
  return user.displayName || user.fullName || [user.firstName, user.lastName].filter(Boolean).join(' ') || user.login || user.email || user.maskedEmail || 'Пользователь';
}

function sourceLabel(source) {
  if (source === 'TelegramUser') return 'Telegram';
  if (source === 'TelegramGroup') return 'Telegram-группа';
  if (source === 'Web') return 'Сайт';
  return null;
}

function MessageBubble({ message, isAdminView, onReply, onContextMenu }) {
  const mine = message.isFromAdmin;
  const author = mine ? 'Поддержка' : (message.authorName || 'Пользователь');
  const meta = [message.createdAt ? new Date(message.createdAt).toLocaleString() : null, sourceLabel(message.source)].filter(Boolean).join(' · ');
  return (
    <div className={mine ? 'flex justify-end' : 'flex justify-start'} onContextMenuCapture={(event) => onContextMenu?.(event, message)}>
      <div className={mine ? 'max-w-[85%] text-right' : 'max-w-[85%] text-left'}>
        <div className="mb-1 text-xs text-neutral-500 dark:text-neutral-400">
          <span className="font-medium text-neutral-700 dark:text-neutral-200">{isAdminView ? author : (mine ? 'Поддержка' : 'Вы')}</span>
          {isAdminView && !mine && message.authorLogin ? <span> · @{message.authorLogin}</span> : null}
          {isAdminView && !mine && message.authorEmail ? <span> · {message.authorEmail}</span> : null}
        </div>
        <div className={mine ? 'bg-neutral-100 dark:bg-neutral-800 inline-block p-3 rounded-2xl rounded-br-md' : 'bg-brand-100 dark:bg-brand-900 inline-block p-3 rounded-2xl rounded-bl-md'}>
          {message.replyTo ? (
            <div className="mb-2 rounded-xl border border-black/10 dark:border-white/10 bg-white/50 dark:bg-black/20 p-2 text-xs text-left">
              <div className="font-medium text-neutral-700 dark:text-neutral-200">
                Ответ на: {message.replyTo.authorName || (String(message.replyTo.authorRole).toLowerCase() === 'admin' ? 'Поддержка' : 'Пользователь')}
              </div>
              <div className="text-neutral-600 dark:text-neutral-300 line-clamp-2">{message.replyTo.textPreview || 'Сообщение'}</div>
            </div>
          ) : null}
          <div className="whitespace-pre-wrap break-words text-left">{message.text}</div>
        </div>
        <div className="mt-1 flex items-center gap-2 text-xs text-neutral-500 dark:text-neutral-400 justify-end">
          <span>{meta}</span>
          <button type="button" className="hover:underline" onClick={() => onReply(message)}>Ответить</button>
        </div>
      </div>
    </div>
  );
}

export default function SupportChatPage() {
  const { ticketId } = useParams();
  const location = useLocation();
  const isAdminView = location.pathname.startsWith('/admin/support');
  const notify = useNotify();
  const { access } = useAuth();

  const [chat, setChat] = useState(null);
  const [messages, setMessages] = useState([]);
  const [newMessage, setNewMessage] = useState('');
  const [replyTo, setReplyTo] = useState(null);
  const [loading, setLoading] = useState(true);
  const [sending, setSending] = useState(false);
  const [error, setError] = useState(null);
  const [messageMenu, setMessageMenu] = useState({ open: false, x: 0, y: 0, message: null });

  const closeMessageMenu = () => setMessageMenu((current) => current.open ? { ...current, open: false } : current);
  const openMessageMenu = (event, message) => {
    if (!claimContextMenuEvent(event)) return;
    setMessageMenu({ open: true, x: event.clientX, y: event.clientY, message });
  };
  const copyMessage = async (message) => {
    try {
      await navigator.clipboard.writeText(String(message?.text || ''));
      notify.success('Сообщение скопировано');
    } catch {
      notify.warn('Не удалось скопировать сообщение');
    }
  };

  const lastMessageIdRef = useRef(null);
  const isMountedRef = useRef(true);
  const bottomRef = useRef(null);

  const chatId = chat?.id || chat?.chatId || chat?.ticketId || ticketId;
  const title = useMemo(() => (isAdminView ? `Чат с ${userLabel(chat?.user)}` : 'Чат с поддержкой'), [chat?.user, isAdminView]);

  const fetchChat = useCallback(async ({ silent = false } = {}) => {
    try {
      const data = ticketId ? await getSupportTicket(ticketId) : await getSupportChat();
      if (!isMountedRef.current) return;

      const nextChat = data.chat || data.ticket || null;
      const nextMessages = (data.messages || []).map(normalizeMessage).filter(Boolean);
      setChat(nextChat);
      setMessages(nextMessages);

      const last = pickLastMessage(nextMessages);
      const lastId = last?.id || `${last?.createdAt || ''}-${last?.text || ''}`;
      const prev = lastMessageIdRef.current;
      lastMessageIdRef.current = lastId;

      if (prev && last && lastId !== prev && last.isFromAdmin && !isAdminView) {
        notifyOnce(`support_msg_${nextChat?.id || ticketId}_${lastId}`, () => notify.info('Техподдержка ответила'), 6000);
      }
    } catch (err) {
      if (!silent) {
        const parsed = handleApiError(err, notify, 'Не удалось загрузить чат поддержки');
        setError(parsed);
      }
    } finally {
      if (!silent) setLoading(false);
    }
  }, [isAdminView, notify, ticketId]);

  useEffect(() => {
    isMountedRef.current = true;
    setLoading(true);
    setError(null);
    setReplyTo(null);
    lastMessageIdRef.current = null;
    fetchChat();
    return () => {
      isMountedRef.current = false;
    };
  }, [fetchChat]);

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: 'smooth', block: 'end' });
  }, [messages.length]);

  useEffect(() => {
    if (!chatId || !access) return undefined;

    let conn = null;
    let disposed = false;
    let onReceive = null;

    const setupRealtime = async () => {
      try {
        conn = await ensureSupportHubStarted(access);
        if (!conn || disposed) return;

        const join = async () => {
          try {
            await conn.invoke('JoinTicket', chatId);
          } catch {}
        };

        await join();
        conn.onreconnected(async () => {
          if (disposed) return;
          await join();
        });

        onReceive = (incomingTicketId, msg) => {
          if (!isMountedRef.current) return;
          if (String(incomingTicketId) !== String(chatId)) return;
          const m = normalizeMessage(msg);
          if (!m) return;

          setMessages((prev) => {
            if (m.id && prev.some((x) => x.id === m.id)) return prev;
            return [...prev, m].sort((a, b) => new Date(a.createdAt) - new Date(b.createdAt));
          });

          const lastId = m?.id || `${m?.createdAt || ''}-${m?.text || ''}`;
          const prev = lastMessageIdRef.current;
          lastMessageIdRef.current = lastId;

          if (prev && m.isFromAdmin && !isAdminView) {
            notifyOnce(`support_msg_${chatId}_${lastId}`, () => notify.info('Техподдержка ответила'), 6000);
          }
        };

        conn.on('ReceiveMessage', onReceive);
      } catch {}
    };

    setupRealtime();

    return () => {
      disposed = true;
      try {
        if (conn) {
          if (onReceive) conn.off('ReceiveMessage', onReceive);
          conn.invoke('LeaveTicket', chatId).catch(() => {});
        }
      } catch {}
    };
  }, [chatId, access, isAdminView, notify]);

  const send = async (e = null) => {
    e?.preventDefault?.();
    const txt = newMessage.trim();
    if (!txt || sending) return;

    try {
      setSending(true);
      setError(null);
      const payload = { message: txt, replyToMessageId: replyTo?.id || null };
      if (ticketId) {
        await sendSupportMessage(ticketId, payload);
      } else {
        await sendSupportChatMessage(payload);
      }
      setNewMessage('');
      setReplyTo(null);
      await fetchChat({ silent: true });
    } catch (err) {
      const parsed = handleApiError(err, notify, 'Не удалось отправить сообщение');
      setError(parsed);
    } finally {
      setSending(false);
    }
  };

  return (
    <>
      <div className="max-w-4xl mx-auto">
        <div className="mb-4 flex items-start justify-between gap-4">
          <div>
            <h1 className="text-2xl font-semibold">{title}</h1>
            <p className="mt-1 text-sm text-neutral-500 dark:text-neutral-400">
              {isAdminView ? 'Личная переписка с пользователем. ID чата скрыт из интерфейса пользователя.' : 'Здесь можно напрямую написать в поддержку. Это один постоянный чат.'}
            </p>
          </div>
          {isAdminView ? <Link to="/admin/support" className="btn-outline">К списку чатов</Link> : null}
        </div>

        {error ? <div className="mb-4"><AppErrorPanel error={error} title="Проблема в чате поддержки" /></div> : null}

        {loading ? (
          <div>Загрузка…</div>
        ) : (
          <Card>
            {isAdminView && chat?.user ? (
              <div className="mb-4 rounded-2xl bg-neutral-50 dark:bg-neutral-900 border border-neutral-200 dark:border-neutral-800 p-4 text-sm">
                <div className="font-semibold text-neutral-900 dark:text-neutral-100">{userLabel(chat.user)}</div>
                <div className="text-neutral-500 dark:text-neutral-400">
                  {chat.user.login ? `@${chat.user.login}` : 'логин не указан'}
                  {chat.user.email || chat.user.maskedEmail ? ` · ${chat.user.email || chat.user.maskedEmail}` : ''}
                </div>
              </div>
            ) : null}

            <div className="space-y-4 mb-4 max-h-[62vh] overflow-y-auto pr-1">
              {messages.length === 0 ? (
                <div className="text-neutral-500 dark:text-neutral-400 text-center py-10">
                  Сообщений пока нет. Напишите первое сообщение в поддержку.
                </div>
              ) : (
                messages.map((m) => (
                  <MessageBubble key={m.id || `${m.createdAt}-${m.text}`} message={m} isAdminView={isAdminView} onReply={setReplyTo} onContextMenu={openMessageMenu} />
                ))
              )}
              <div ref={bottomRef} />
            </div>

            <form onSubmit={send} className="space-y-3">
              {replyTo ? (
                <div className="rounded-2xl border border-brand-200 dark:border-brand-800 bg-brand-50 dark:bg-brand-950/40 p-3 text-sm flex items-start justify-between gap-3">
                  <div>
                    <div className="font-medium">Ответ на сообщение</div>
                    <div className="text-neutral-600 dark:text-neutral-300 line-clamp-2">{replyTo.text}</div>
                  </div>
                  <button type="button" className="text-sm text-neutral-500 hover:underline" onClick={() => setReplyTo(null)}>убрать</button>
                </div>
              ) : null}
              <Field label={isAdminView ? 'Ответ поддержки' : 'Ваше сообщение'}>
                <Textarea
                  value={newMessage}
                  onChange={(e) => setNewMessage(e.target.value)}
                  rows={4}
                  placeholder={isAdminView ? 'Напишите ответ пользователю…' : 'Напишите сообщение в поддержку…'}
                  onKeyDown={(event) => {
                    if ((event.ctrlKey || event.metaKey) && event.key === 'Enter' && newMessage.trim() && !sending) {
                      event.preventDefault();
                      void send();
                    }
                  }}
                />
              </Field>
              <div className="flex justify-end">
                <Button type="submit" disabled={sending || !newMessage.trim()} title="Ctrl+Enter">{sending ? 'Отправка…' : 'Отправить'}</Button>
              </div>
            </form>
          </Card>
        )}
      </div>

      <ContextMenu open={messageMenu.open} x={messageMenu.x} y={messageMenu.y} onClose={closeMessageMenu} ariaLabel="Действия сообщения">
        {messageMenu.message ? (
          <>
            <ContextMenuLabel>Сообщение</ContextMenuLabel>
            <ContextMenuItem icon={Reply} onClick={() => { const message = messageMenu.message; closeMessageMenu(); setReplyTo(message); }}>Ответить</ContextMenuItem>
            <ContextMenuItem icon={Copy} onClick={() => { const message = messageMenu.message; closeMessageMenu(); void copyMessage(message); }}>Скопировать текст</ContextMenuItem>
            {isAdminView && messageMenu.message.id ? (
              <>
                <ContextMenuSeparator />
                <ContextMenuItem icon={Copy} onClick={() => { const message = messageMenu.message; closeMessageMenu(); navigator.clipboard.writeText(String(message.id)).then(() => notify.success('ID сообщения скопирован')).catch(() => notify.warn('Не удалось скопировать ID')); }}>Скопировать ID</ContextMenuItem>
              </>
            ) : null}
          </>
        ) : null}
      </ContextMenu>
    </>
  );
}
