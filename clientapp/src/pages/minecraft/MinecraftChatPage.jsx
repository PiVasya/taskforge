import React, { useMemo, useState } from 'react';
import Layout from '../../components/Layout';
import { Card, Button, Input, Badge } from '../../components/ui';
import { MessageSquare, Send, Shield, Server } from 'lucide-react';

const initialMessages = [
  { id: 1, side: 'mc', author: 'Steve', text: 'Всем привет с сервера Minecraft 👋', time: '18:41' },
  { id: 2, side: 'site', author: 'TaskForge', text: 'Это демо-ветка чата. Дальше сюда можно подключить мост из игры.', time: '18:42' },
  { id: 3, side: 'mc', author: 'BuilderFox', text: 'Проверка связи между игрой и сайтом выглядит перспективно.', time: '18:43' },
];

export default function MinecraftChatPage() {
  const [messages, setMessages] = useState(initialMessages);
  const [text, setText] = useState('');

  const stats = useMemo(() => ({
    total: messages.length,
    minecraft: messages.filter((x) => x.side === 'mc').length,
    site: messages.filter((x) => x.side === 'site').length,
  }), [messages]);

  const send = () => {
    const value = (text || '').trim();
    if (!value) return;
    setMessages((prev) => [
      ...prev,
      { id: Date.now(), side: 'site', author: 'Ты', text: value, time: new Date().toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) },
    ]);
    setText('');
  };

  return (
    <Layout>
      <div className="space-y-6">
        <div className="flex flex-col lg:flex-row lg:items-center lg:justify-between gap-3">
          <div>
            <h1 className="text-2xl font-semibold flex items-center gap-2">
              <MessageSquare size={24} /> Minecraft чат
            </h1>
            <p className="text-sm text-neutral-500 mt-2">
              Пока это фронтовая заглушка для закрытой Minecraft-ветки. Сюда можно безопасно протянуть мост из сервера позже.
            </p>
          </div>
          <div className="flex flex-wrap gap-2">
            <Badge intent="secondary"><Server size={14} className="inline mr-1" /> demo</Badge>
            <Badge intent="secondary"><Shield size={14} className="inline mr-1" /> доступ по роли Minecraft</Badge>
          </div>
        </div>

        <div className="grid xl:grid-cols-[280px,1fr] gap-6">
          <Card>
            <div className="space-y-3">
              <div className="text-sm font-medium">Сводка</div>
              <div className="text-sm opacity-80">Всего сообщений: <b>{stats.total}</b></div>
              <div className="text-sm opacity-80">Из Minecraft: <b>{stats.minecraft}</b></div>
              <div className="text-sm opacity-80">С сайта: <b>{stats.site}</b></div>
              <div className="pt-3 border-t border-neutral-200/60 dark:border-neutral-800/60 text-sm opacity-80">
                Следующий этап: подцепить реальные сообщения из плагина и отдачу из TaskForge обратно в игру.
              </div>
            </div>
          </Card>

          <Card className="flex flex-col gap-4 min-h-[520px]">
            <div className="flex-1 space-y-3 overflow-auto">
              {messages.map((m) => (
                <div key={m.id} className={`rounded-2xl p-4 border ${m.side === 'mc' ? 'border-emerald-500/20 bg-emerald-500/5' : 'border-brand-500/20 bg-brand-500/5'}`}>
                  <div className="flex items-center justify-between gap-3 text-sm mb-2">
                    <div className="font-medium">{m.author}</div>
                    <div className="opacity-60">{m.time}</div>
                  </div>
                  <div className="text-sm whitespace-pre-wrap">{m.text}</div>
                </div>
              ))}
            </div>

            <div className="flex flex-col sm:flex-row gap-3 pt-2 border-t border-neutral-200/60 dark:border-neutral-800/60">
              <Input
                value={text}
                onChange={(e) => setText(e.target.value)}
                placeholder="Написать сообщение в будущий Minecraft-чат..."
                onKeyDown={(e) => { if (e.key === 'Enter') send(); }}
              />
              <Button onClick={send}>
                <Send size={16} /> <span className="ml-1">Отправить</span>
              </Button>
            </div>
          </Card>
        </div>
      </div>
    </Layout>
  );
}
