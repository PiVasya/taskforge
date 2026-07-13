import React, { useEffect, useMemo, useState } from 'react';
import Layout from '../../components/Layout';
import { Badge, Button, Card, Field, Input } from '../../components/ui';
import { getAdminMinecraftLinks } from '../../api/adminMinecraftLinks';
import { handleApiError } from '../../utils/handleApiError';
import { useNotify } from '../../components/notify/NotifyProvider';
import AppErrorPanel from '../../components/AppErrorPanel';
import { Link2, RefreshCcw } from 'lucide-react';

export default function AdminMinecraftLinksPage() {
  const notify = useNotify();
  const [items, setItems] = useState([]);
  const [loading, setLoading] = useState(true);
  const [pageError, setPageError] = useState(null);
  const [query, setQuery] = useState('');

  const load = async () => {
    try {
      setLoading(true);
      const list = await getAdminMinecraftLinks({ query });
      setItems(Array.isArray(list) ? list : []);
      setPageError(null);
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось загрузить связи Minecraft');
      setPageError(parsed);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { load(); }, []); 

  const stats = useMemo(() => ({
    total: items.length,
    totalSpent: items.reduce((s, x) => s + Number(x.minecraftSpent ?? x.totalSpent ?? 0), 0),
    totalRestored: items.reduce((s, x) => s + Number(x.minecraftRestored || 0), 0),
  }), [items]);

  return (
    <Layout>
      <div className="space-y-6">
        {pageError ? <AppErrorPanel error={pageError} title="Не удалось загрузить админ-раздел" /> : null}

        <div className="flex flex-col lg:flex-row lg:items-center lg:justify-between gap-3">
          <div>
            <h1 className="text-2xl font-semibold flex items-center gap-2"><Link2 size={22} /> Связи с Minecraft</h1>
            <p className="text-sm text-neutral-500 mt-2">Кто привязан, сколько рейтинга потрачено и какой Minecraft-баланс доступен.</p>
          </div>
          <Button onClick={load}><RefreshCcw size={16} /> <span className="ml-1">Обновить</span></Button>
        </div>

        <div className="grid md:grid-cols-3 gap-4">
          <Card><div className="text-sm opacity-70">Привязанных</div><div className="text-3xl font-semibold mt-2">{stats.total}</div></Card>
          <Card><div className="text-sm opacity-70">Потрачено рейтинга</div><div className="text-3xl font-semibold mt-2">{stats.totalSpent}</div></Card>
          <Card><div className="text-sm opacity-70">Восстановлено</div><div className="text-3xl font-semibold mt-2">{stats.totalRestored}</div></Card>
        </div>

        <Card>
          <div className="flex gap-2 items-end">
            <div className="flex-1"><Field label="Поиск"><Input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="email / имя / nick / uuid" /></Field></div>
            <Button onClick={load}>Найти</Button>
          </div>
        </Card>

        {loading && <div className="text-neutral-500">Загрузка…</div>}

        <div className="space-y-4">
          {items.map((x) => (
            <Card key={x.id || x.userId || `${x.minecraftUuid}-${x.minecraftNick}`}>
              <div className="grid xl:grid-cols-[1.1fr,1fr,1fr] gap-4">
                <div>
                  <div className="font-medium">{x.fullName || x.email}</div>
                  <div className="text-sm opacity-70 mt-1">{x.email}</div>
                  <div className="text-sm mt-3 space-y-2 opacity-80">
                    <div>Активных привязок: {x.activeLinkCount ?? (x.activeLinks || []).length ?? 0}</div>
                    {(x.activeLinks || []).length ? (x.activeLinks || []).map((link) => (
                      <div key={link.id || `${link.uuid}-${link.nick}`} className="rounded-xl border px-3 py-2">
                        <div>Nick: {link.nick || '—'}</div>
                        <div>UUID: {link.uuid || '—'}</div>
                        <div>Привязан: {link.linkedAtUtc ? new Date(link.linkedAtUtc).toLocaleString() : '—'}</div>
                      </div>
                    )) : (
                      <div>Активных Minecraft-профилей нет</div>
                    )}
                    <div>Всего подтверждений за историю: {x.linkCount ?? 0}</div>
                  </div>
                </div>
                <div className="space-y-3">
                  <div className="grid grid-cols-2 gap-3">
                    <div className="rounded-2xl border px-4 py-3"><div className="text-xs opacity-60">Основной рейтинг</div><div className="text-2xl font-semibold mt-1">{x.totalScore ?? 0}</div></div>
                    <div className="rounded-2xl border px-4 py-3"><div className="text-xs opacity-60">Minecraft-баланс</div><div className="text-2xl font-semibold mt-1">{x.minecraftBalance ?? x.effectiveScore ?? 0}</div></div>
                    <div className="rounded-2xl border px-4 py-3"><div className="text-xs opacity-60">Потрачено</div><div className="text-2xl font-semibold mt-1">{x.minecraftSpent ?? x.totalSpent ?? 0}</div></div>
                    <div className="rounded-2xl border px-4 py-3"><div className="text-xs opacity-60">Восстановлено</div><div className="text-2xl font-semibold mt-1">{x.minecraftRestored ?? 0}</div></div>
                  </div>
                </div>
                <div className="space-y-3">
                  <div className="flex flex-wrap gap-2">
                    {(x.featureRoles || []).length ? x.featureRoles.map((r) => <Badge key={r} intent="outline">{r}</Badge>) : <Badge intent="secondary">Без доп. ролей</Badge>}
                    {(x.activeLinkCount ?? (x.activeLinks || []).length) > 0
                      ? <Badge intent="success">Активно</Badge>
                      : <Badge intent="secondary">Нет активных привязок</Badge>}
                  </div>
                  <div className="text-sm opacity-70">Последняя трата: {x.lastSpentAtUtc ? new Date(x.lastSpentAtUtc).toLocaleString() : 'ещё не было'}</div>
                </div>
              </div>
            </Card>
          ))}
        </div>
      </div>
    </Layout>
  );
}
