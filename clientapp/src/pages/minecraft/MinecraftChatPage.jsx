import React, { useEffect, useMemo, useRef, useState } from 'react';
import Layout from '../../components/Layout';
import { Button, Card, Input, Badge } from '../../components/ui';
import { useNotify } from '../../components/notify/NotifyProvider';
import { useAuth } from '../../auth/AuthContext';
import { getMinecraftChatMessages, sendMinecraftChatMessage } from '../../api/minecraftChat';
import { ensureMinecraftChatHubStarted } from '../../realtime/minecraftChatHub';
import { MessageSquare, Send, RefreshCw, Trophy, Pickaxe, Globe } from 'lucide-react';

const SCROLLER_HEIGHT = 'calc(100vh - 290px)';

function isAchievementMessage(message = '') {
  const text = String(message).toLowerCase();
  return text.includes('достижен') || text.includes('achievement') || text.includes('advancement') || text.includes('получил ачив') || text.includes('получил достижение');
}

function formatSource(source) {
  if (source === 'Minecraft') return 'Minecraft';
  if (source === 'SiteAdmin') return 'Сайт · админ';
  return 'Сайт';
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
  const shouldStickToBottom = useRef(true);

  const scrollToBottom = (behavior = 'smooth') => {
    const el = scrollerRef.current;
    if (!el) return;
    el.scrollTo({ top: el.scrollHeight, behavior });
  };

  const rememberScrollIntent = () => {
    const el = scrollerRef.current;
    if (!el) return;
    const distance = el.scrollHeight - el.scrollTop - el.clientHeight;
    shouldStickToBottom.current = distance < 100;
  };

  const load = async ({ silent = false } = {}) => {
    try {
      if (!silent) setLoading(true);
      const data = await getMinecraftChatMessages(120);
      if (!mounted.current) return;
      const arr = Array.isArray(data) ? data : [];
      setMessages(arr);
      requestAnimationFrame(() => scrollToBottom('auto'));
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
            requestAnimationFrame(() => {
              if (shouldStickToBottom.current) scrollToBottom('smooth');
            });
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
      shouldStickToBottom.current = true;
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
    achievements: messages.filter((x) => isAchievementMessage(x.message ?? x.Message)).length,
  }), [messages]);

  return (
    <Layout fullWidth>
      <div className="mx-auto max-w-7xl space-y-5">
        <div className="flex flex-col xl:flex-row xl:items-center xl:justify-between gap-3">
          <div>
            <h1 className="text-2xl font-semibold flex items-center gap-2"><MessageSquare size={22} /> Minecraft чат</h1>
            <p className="text-sm text-neutral-500 mt-2">Связанный чат между TaskForge и Minecraft. История живёт отдельно и прокручивается внутри блока, а не растягивает страницу вниз.</p>
          </div>
          <div className="flex flex-wrap gap-2">
            <Button variant="outline" onClick={() => load()}><RefreshCw size={16} /><span className="ml-1">Обновить</span></Button>
          </div>
        </div>

        <div className="grid 2xl:grid-cols-[300px,1fr] gap-5 items-start">
          <div className="grid sm:grid-cols-2 2xl:grid-cols-1 gap-4">
            <Card>
              <div className="text-sm opacity-65">Сообщений всего</div>
              <div className="text-3xl font-semibold mt-2">{stats.total}</div>
            </Card>
            <Card>
              <div className="text-sm opacity-65">Из Minecraft</div>
              <div className="text-3xl font-semibold mt-2 text-emerald-500">{stats.minecraft}</div>
            </Card>
            <Card>
              <div className="text-sm opacity-65">С сайта</div>
              <div className="text-3xl font-semibold mt-2 text-brand-500">{stats.site}</div>
            </Card>
            <Card>
              <div className="text-sm opacity-65">Ачивки в ленте</div>
              <div className="text-3xl font-semibold mt-2 text-amber-500">{stats.achievements}</div>
            </Card>
          </div>

          <Card className="flex flex-col gap-4 p-4 sm:p-5">
            <div className="flex items-center justify-between gap-3 flex-wrap">
              <div className="flex flex-wrap gap-2">
                <Badge intent="secondary"><Pickaxe size={12} className="mr-1 inline" /> Minecraft</Badge>
                <Badge intent="secondary"><Globe size={12} className="mr-1 inline" /> TaskForge</Badge>
                <Badge intent="secondary"><Trophy size={12} className="mr-1 inline" /> Ачивки тоже стараемся ловить</Badge>
              </div>
              <div className="text-xs opacity-60">Последние 120 сообщений</div>
            </div>

            <div
              ref={scrollerRef}
              onScroll={rememberScrollIntent}
              className="rounded-2xl border border-neutral-200/60 dark:border-neutral-800/60 bg-black/5 dark:bg-white/[0.03] overflow-y-auto p-3 sm:p-4"
              style={{ maxHeight: SCROLLER_HEIGHT, minHeight: '460px' }}
            >
              {loading ? (
                <div className="text-neutral-500">Загрузка…</div>
              ) : messages.length === 0 ? (
                <div className="text-neutral-500">Пока сообщений нет.</div>
              ) : (
                <div className="space-y-2.5">
                  {messages.map((m) => {
                    const source = m.source ?? m.Source;
                    const createdAt = m.createdAtUtc ?? m.CreatedAtUtc;
                    const message = m.message ?? m.Message;
                    const author = m.authorName ?? m.AuthorName ?? m.minecraftNick ?? m.MinecraftNick ?? 'Unknown';
                    const isMc = source === 'Minecraft';
                    const isAchievement = isAchievementMessage(message);
                    return (
                      <div
                        key={m.id ?? m.Id ?? `${createdAt}-${message}`}
                        className={[
                          'rounded-2xl border px-3 py-2.5 sm:px-4 sm:py-3 shadow-sm',
                          isAchievement
                            ? 'border-amber-500/30 bg-amber-500/10'
                            : isMc
                              ? 'border-emerald-500/20 bg-emerald-500/5'
                              : 'border-brand-500/20 bg-brand-500/5'
                        ].join(' ')}
                      >
                        <div className="flex items-start justify-between gap-3">
                          <div className="min-w-0">
                            <div className="flex items-center gap-2 flex-wrap">
                              <div className="font-medium leading-tight truncate">{author}</div>
                              <span className="text-[11px] opacity-60">{formatSource(source)}</span>
                              {isAchievement && <Badge intent="secondary"><Trophy size={12} className="mr-1 inline" /> ачивка</Badge>}
                            </div>
                          </div>
                          <div className="text-[11px] opacity-55 shrink-0">{createdAt ? new Date(createdAt).toLocaleString() : ''}</div>
                        </div>
                        <div className="mt-1.5 text-sm whitespace-pre-wrap break-words leading-5">{message}</div>
                      </div>
                    );
                  })}
                </div>
              )}
            </div>

            <div className="flex flex-col sm:flex-row gap-3 pt-1">
              <Input
                value={text}
                onChange={(e) => setText(e.target.value)}
                onKeyDown={(e) => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); send(); } }}
                placeholder="Написать сообщение в Minecraft..."
              />
              <Button onClick={send} disabled={sending || !text.trim()}><Send size={16} /><span className="ml-1">Отправить</span></Button>
            </div>
          </Card>
        </div>
      </div>
    </Layout>
  );
}
