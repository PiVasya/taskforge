import React, { useEffect, useMemo, useRef, useState } from 'react';
import Layout from '../../components/Layout';
import { Button, Card, Input } from '../../components/ui';
import { useNotify } from '../../components/notify/NotifyProvider';
import { useAuth } from '../../auth/AuthContext';
import { getMinecraftChatMessages, sendMinecraftChatMessage } from '../../api/minecraftChat';
import { ensureMinecraftChatHubStarted } from '../../realtime/minecraftChatHub';
import { MessageSquare, Send, RefreshCw } from 'lucide-react';

export default function MinecraftChatPage() {
  const notify = useNotify();
  const { access } = useAuth();
  const [messages, setMessages] = useState([]);
  const [text, setText] = useState('');
  const [loading, setLoading] = useState(true);
  const [sending, setSending] = useState(false);
  const mounted = useRef(true);

  const load = async ({ silent = false } = {}) => {
    try {
      if (!silent) setLoading(true);
      const data = await getMinecraftChatMessages(80);
      if (!mounted.current) return;
      setMessages(Array.isArray(data) ? data : []);
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
            return [...prev, msg].sort((a, b) => new Date(a.createdAtUtc ?? a.CreatedAtUtc) - new Date(b.createdAtUtc ?? b.CreatedAtUtc));
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
    } catch (e) {
      notify.error(e?.message || 'Не удалось отправить сообщение');
    } finally {
      setSending(false);
    }
  };

  const stats = useMemo(() => ({
    total: messages.length,
    minecraft: messages.filter((x) => String(x.source ?? x.Source) === 'Minecraft').length,
    site: messages.filter((x) => String(x.source ?? x.Source) !== 'Minecraft').length,
  }), [messages]);

  return (
    <Layout>
      <div className="space-y-6">
        <div className="flex flex-col lg:flex-row lg:items-center lg:justify-between gap-3">
          <div>
            <h1 className="text-2xl font-semibold flex items-center gap-2"><MessageSquare size={22} /> Minecraft чат</h1>
            <p className="text-sm text-neutral-500 mt-2">Связанный чат между TaskForge и Minecraft. Сообщения с сайта уходят в игру, сообщения из игры прилетают сюда.</p>
          </div>
          <Button variant="outline" onClick={() => load()}><RefreshCw size={16} /><span className="ml-1">Обновить</span></Button>
        </div>

        <div className="grid xl:grid-cols-[280px,1fr] gap-6">
          <Card>
            <div className="space-y-3 text-sm">
              <div>Всего сообщений: <b>{stats.total}</b></div>
              <div>Из Minecraft: <b>{stats.minecraft}</b></div>
              <div>С сайта: <b>{stats.site}</b></div>
            </div>
          </Card>

          <Card className="min-h-[540px] flex flex-col gap-4">
            <div className="flex-1 space-y-3 overflow-auto">
              {loading ? (
                <div className="text-neutral-500">Загрузка…</div>
              ) : messages.length === 0 ? (
                <div className="text-neutral-500">Пока сообщений нет.</div>
              ) : messages.map((m) => {
                const source = m.source ?? m.Source;
                const createdAt = m.createdAtUtc ?? m.CreatedAtUtc;
                const author = m.authorName ?? m.AuthorName ?? m.minecraftNick ?? m.MinecraftNick ?? 'Unknown';
                return (
                  <div key={m.id ?? m.Id ?? `${createdAt}-${m.message ?? m.Message}`} className={`rounded-2xl p-4 border ${source === 'Minecraft' ? 'border-emerald-500/20 bg-emerald-500/5' : 'border-brand-500/20 bg-brand-500/5'}`}>
                    <div className="flex items-center justify-between gap-3 text-sm mb-2">
                      <div className="font-medium">{author}</div>
                      <div className="opacity-60">{createdAt ? new Date(createdAt).toLocaleString() : ''}</div>
                    </div>
                    <div className="text-xs opacity-70 mb-2">{source}</div>
                    <div className="text-sm whitespace-pre-wrap">{m.message ?? m.Message}</div>
                  </div>
                );
              })}
            </div>

            <div className="flex flex-col sm:flex-row gap-3 pt-2 border-t border-neutral-200/60 dark:border-neutral-800/60">
              <Input value={text} onChange={(e) => setText(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter') send(); }} placeholder="Написать сообщение в Minecraft..." />
              <Button onClick={send} disabled={sending}><Send size={16} /><span className="ml-1">Отправить</span></Button>
            </div>
          </Card>
        </div>
      </div>
    </Layout>
  );
}
