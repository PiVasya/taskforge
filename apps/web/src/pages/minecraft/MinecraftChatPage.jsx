import React, { useEffect, useRef, useState } from 'react';
import { Button, Card, Input } from '../../components/ui';
import { useNotify } from '../../components/notify/NotifyProvider';
import { useAuth } from '../../auth/AuthContext';
import { getMinecraftChatMessages, getMinecraftChatMeta, sendMinecraftChatMessage } from '../../api/minecraftChat';
import { ensureMinecraftChatHubStarted } from '../../realtime/minecraftChatHub';
import { MessageSquare, Send, RefreshCw } from 'lucide-react';
import AppErrorPanel from '../../components/AppErrorPanel';
import { handleApiError } from '../../utils/handleApiError';

const CHAT_H = 'h-[calc(100dvh-8.5rem)] sm:h-[calc(100dvh-11rem)]';

function formatTime(value) {
  if (!value) return '';
  return new Date(value).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

function shouldHideMinecraftMessage(m) {
  const source = String(m?.source ?? m?.Source ?? '');
  const message = String(m?.message ?? m?.Message ?? '').toLowerCase();
  if (source !== 'MinecraftAdvancement') return false;
  return message.includes('recipes/') || message.includes('/root');
}

export default function MinecraftChatPage() {
  const notify = useNotify();
  const { access } = useAuth();
  const [messages, setMessages] = useState([]);
  const [text, setText] = useState('');
  const [loading, setLoading] = useState(true);
  const [sending, setSending] = useState(false);
  const [meta, setMeta] = useState({ onlinePlayers: null, available: false });
  const [pageError, setPageError] = useState(null);
  const mounted = useRef(true);
  const scrollerRef = useRef(null);

  const scrollToBottom = (smooth = false) => {
    const el = scrollerRef.current;
    if (!el) return;
    el.scrollTo({ top: el.scrollHeight, behavior: smooth ? 'smooth' : 'auto' });
  };

  const load = async ({ silent = false } = {}) => {
    try {
      if (!silent) setLoading(true);
      const [data, chatMeta] = await Promise.all([
        getMinecraftChatMessages(120),
        getMinecraftChatMeta().catch(() => ({ onlinePlayers: null, available: false })),
      ]);
      if (!mounted.current) return;
      setPageError(null);
      setMessages((Array.isArray(data) ? data : []).filter((x) => !shouldHideMinecraftMessage(x)));
      setMeta({
        onlinePlayers: chatMeta?.onlinePlayers ?? null,
        available: Boolean(chatMeta?.available),
      });
      requestAnimationFrame(() => scrollToBottom(false));
    } catch (e) {
      if (!silent) {
        const parsed = handleApiError(e, notify, 'Не удалось загрузить Minecraft чат');
        setPageError(parsed);
      }
    } finally {
      if (!silent) setLoading(false);
    }
  };

  useEffect(() => {
    mounted.current = true;
    load();

    let conn = null;
    let onReceive = null;
    let disposed = false;

    const setup = async () => {
      try {
        conn = await ensureMinecraftChatHubStarted(access);
        if (!conn || disposed) return;
        await conn.invoke('JoinChat');

        onReceive = (msg) => {
          if (!mounted.current || !msg) return;
          setMessages((prev) => {
            const id = msg.id ?? msg.Id;
            if (id && prev.some((x) => (x.id ?? x.Id) === id)) return prev;
            if (shouldHideMinecraftMessage(msg)) return prev;
            const next = [...prev, msg].sort((a, b) => new Date(a.createdAtUtc ?? a.CreatedAtUtc) - new Date(b.createdAtUtc ?? b.CreatedAtUtc));
            requestAnimationFrame(() => scrollToBottom(true));
            return next;
          });
        };
        conn.on('ReceiveMessage', onReceive);
      } catch {}
    };

    setup();
    return () => {
      mounted.current = false;
      disposed = true;
      try {
        if (conn && onReceive) conn.off('ReceiveMessage', onReceive);
        if (conn) conn.invoke('LeaveChat').catch(() => {});
      } catch {}
    };
  }, [access]);

  const send = async () => {
    const value = text.trim();
    if (!value) return;
    try {
      setSending(true);
      setPageError(null);
      await sendMinecraftChatMessage(value);
      setText('');
      requestAnimationFrame(() => scrollToBottom(true));
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось отправить сообщение');
      setPageError(parsed);
    } finally {
      setSending(false);
    }
  };

  const renderLine = (m) => {
    const source = String(m.source ?? m.Source);
    const createdAt = m.createdAtUtc ?? m.CreatedAtUtc;
    const author = m.authorName ?? m.AuthorName ?? m.minecraftNick ?? m.MinecraftNick ?? 'Unknown';
    const msg = m.message ?? m.Message ?? '';

    if (source === 'MinecraftJoin' || source === 'MinecraftQuit') {
      return (
        <div key={m.id ?? m.Id ?? `${createdAt}-${msg}`} className="text-sm px-1 py-0.5 text-yellow-700 dark:text-yellow-300 italic">
          <span className="opacity-60 mr-2">{formatTime(createdAt)}</span>
          {msg}
        </div>
      );
    }

    if (source === 'MinecraftAdvancement') {
      return (
        <div key={m.id ?? m.Id ?? `${createdAt}-${msg}`} className="text-sm px-1 py-0.5 text-emerald-700 dark:text-emerald-300">
          <span className="opacity-60 mr-2">{formatTime(createdAt)}</span>
          <span className="font-medium">{msg}</span>
        </div>
      );
    }

    const isMinecraft = source === 'Minecraft';
    return (
      <div key={m.id ?? m.Id ?? `${createdAt}-${msg}`} className="text-sm px-1 py-0.5 break-words">
        <span className="opacity-60 mr-2">{formatTime(createdAt)}</span>
        <span className={`font-semibold ${isMinecraft ? 'text-emerald-700 dark:text-emerald-300' : 'text-fuchsia-700 dark:text-fuchsia-300'}`}>
          {author}
        </span>
        <span className="opacity-60">: </span>
        <span>{msg}</span>
      </div>
    );
  };

  return (
    <>
      <div className={`${CHAT_H} min-h-0 overflow-hidden flex flex-col gap-3 sm:gap-5`}>
        <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
          <div>
            <h1 className="text-xl sm:text-2xl font-semibold flex items-center gap-2"><MessageSquare size={22} /> Minecraft чат</h1>
          </div>
          <Button variant="outline" className="self-start sm:self-auto" onClick={() => load()}><RefreshCw size={16} /><span className="ml-1">Обновить</span></Button>
        </div>

        <Card className="min-h-0 flex flex-1 flex-col p-3 sm:p-4">
            {pageError ? <div className="mb-3"><AppErrorPanel error={pageError} title="Проблема с Minecraft-чатом" compact /></div> : null}
            <div className="mb-3 flex flex-wrap items-center gap-2 text-[11px] sm:gap-3 sm:text-xs text-neutral-500">
              <span className="rounded-full border border-emerald-500/20 px-2 py-1">Minecraft</span>
              <span className="rounded-full border border-fuchsia-500/20 px-2 py-1">TaskForge</span>
              <span className="rounded-full border border-amber-500/20 px-2 py-1">Входы / выходы / ачивки</span>
              <span className="ml-auto rounded-full border border-neutral-200/70 px-2 py-1 text-neutral-700 dark:border-neutral-700 dark:text-neutral-200">
                Онлайн: {meta.available ? (meta.onlinePlayers ?? '—') : '—'}
              </span>
            </div>

            <div ref={scrollerRef} className="min-h-0 flex-1 overflow-y-auto rounded-2xl border border-neutral-200/60 dark:border-neutral-800/60 bg-black/5 dark:bg-black/20 px-2 py-2 sm:px-3 sm:py-3 font-mono text-[12px] sm:text-sm">
              {loading ? (
                <div className="text-neutral-500">Загрузка…</div>
              ) : messages.length === 0 ? (
                <div className="text-neutral-500">Пока сообщений нет.</div>
              ) : (
                <div className="space-y-1">{messages.map(renderLine)}</div>
              )}
            </div>

            <div className="pt-3 mt-3 border-t border-neutral-200/60 dark:border-neutral-800/60 flex flex-col sm:flex-row gap-3">
              <Input className="flex-1" value={text} onChange={(e) => setText(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter') send(); }} placeholder="Написать сообщение в Minecraft..." />
              <Button className="w-full sm:w-auto" onClick={send} disabled={sending}><Send size={16} /><span className="ml-1">Отправить</span></Button>
            </div>
        </Card>
      </div>
    </>
  );
}
