import React, { useEffect, useMemo, useRef, useState } from 'react';
import Layout from '../../components/Layout';
import { Button, Card, Input } from '../../components/ui';
import { useNotify } from '../../components/notify/NotifyProvider';
import { useAuth } from '../../auth/AuthContext';
import { getMinecraftChatMessages, sendMinecraftChatMessage } from '../../api/minecraftChat';
import { ensureMinecraftChatHubStarted } from '../../realtime/minecraftChatHub';
import { MessageSquare, Send, RefreshCw } from 'lucide-react';

const CHAT_H = 'h-[calc(100vh-180px)]';

function formatTime(value) {
  if (!value) return '';
  return new Date(value).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

export default function MinecraftChatPage() {
  const notify = useNotify();
  const { access } = useAuth();
  const [messages, setMessages] = useState([]);
  const [text, setText] = useState('');
  const [loading, setLoading] = useState(true);
  const [sending, setSending] = useState(false);
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
      const data = await getMinecraftChatMessages(120);
      if (!mounted.current) return;
      setMessages(Array.isArray(data) ? data : []);
      requestAnimationFrame(() => scrollToBottom(false));
    } catch (e) {
      if (!silent) notify.error(e?.message || 'Не удалось загрузить Minecraft чат');
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
      await sendMinecraftChatMessage(value);
      setText('');
      requestAnimationFrame(() => scrollToBottom(true));
    } catch (e) {
      notify.error(e?.message || 'Не удалось отправить сообщение');
    } finally {
      setSending(false);
    }
  };

  const stats = useMemo(() => ({
    total: messages.length,
    minecraft: messages.filter((x) => String(x.source ?? x.Source).startsWith('Minecraft')).length,
    site: messages.filter((x) => !String(x.source ?? x.Source).startsWith('Minecraft')).length,
    advancements: messages.filter((x) => String(x.source ?? x.Source) === 'MinecraftAdvancement').length,
  }), [messages]);

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
    <Layout hideFooter>
      <div className={`${CHAT_H} overflow-hidden flex flex-col gap-5`}>
        <div className="flex items-start justify-between gap-3">
          <div>
            <h1 className="text-2xl font-semibold flex items-center gap-2"><MessageSquare size={22} /> Minecraft чат</h1>
            <p className="text-sm text-neutral-500 mt-2">Компактная лента в стиле игрового чата. Входы, выходы и ачивки тоже прилетают сюда.</p>
          </div>
          <Button variant="outline" onClick={() => load()}><RefreshCw size={16} /><span className="ml-1">Обновить</span></Button>
        </div>

        <div className="grid xl:grid-cols-[220px,1fr] gap-5 min-h-0 flex-1">
          <div className="space-y-4">
            <Card className="p-4"><div className="text-sm text-neutral-500">Сообщений всего</div><div className="mt-2 text-4xl font-semibold">{stats.total}</div></Card>
            <Card className="p-4"><div className="text-sm text-neutral-500">Из Minecraft</div><div className="mt-2 text-4xl font-semibold text-emerald-500">{stats.minecraft}</div></Card>
            <Card className="p-4"><div className="text-sm text-neutral-500">С сайта</div><div className="mt-2 text-4xl font-semibold text-fuchsia-500">{stats.site}</div></Card>
            <Card className="p-4"><div className="text-sm text-neutral-500">Ачивки в ленте</div><div className="mt-2 text-4xl font-semibold text-amber-500">{stats.advancements}</div></Card>
          </div>

          <Card className="p-4 flex flex-col min-h-0">
            <div className="flex items-center justify-between gap-3 text-xs text-neutral-500 mb-3">
              <div className="flex flex-wrap gap-2">
                <span className="rounded-full border border-emerald-500/20 px-2 py-1">Minecraft</span>
                <span className="rounded-full border border-fuchsia-500/20 px-2 py-1">TaskForge</span>
                <span className="rounded-full border border-amber-500/20 px-2 py-1">Входы / выходы / ачивки</span>
              </div>
              <div>Последние 120 сообщений</div>
            </div>

            <div ref={scrollerRef} className="min-h-0 flex-1 overflow-y-auto rounded-2xl border border-neutral-200/60 dark:border-neutral-800/60 bg-black/5 dark:bg-black/20 px-3 py-3 font-mono">
              {loading ? (
                <div className="text-neutral-500">Загрузка…</div>
              ) : messages.length === 0 ? (
                <div className="text-neutral-500">Пока сообщений нет.</div>
              ) : (
                <div className="space-y-1">{messages.map(renderLine)}</div>
              )}
            </div>

            <div className="pt-3 mt-3 border-t border-neutral-200/60 dark:border-neutral-800/60 flex gap-3">
              <Input value={text} onChange={(e) => setText(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter') send(); }} placeholder="Написать сообщение в Minecraft..." />
              <Button onClick={send} disabled={sending}><Send size={16} /><span className="ml-1">Отправить</span></Button>
            </div>
          </Card>
        </div>
      </div>
    </Layout>
  );
}
