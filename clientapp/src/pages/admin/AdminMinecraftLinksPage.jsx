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

  useEffect(() => { load(); }, []); // eslint-disable-line

  const stats = useMemo(() => ({
    total: items.length,
    totalPenalty: items.reduce((s, x) => s + Number(x.totalPenalty || 0), 0),
    debuffed: items.filter((x) => Number(x.effectiveScore || 0) < 0).length,
  }), [items]);

  return (
    <Layout>
      <div className="space-y-6">
        {pageError ? <AppErrorPanel error={pageError} title="Ошибка админ-раздела" /> : null}

        <div className="flex flex-col lg:flex-row lg:items-center lg:justify-between gap-3">
          <div>
            <h1 className="text-2xl font-semibold flex items-center gap-2"><Link2 size={22} /> Связи с Minecraft</h1>
            <p className="text-sm text-neutral-500 mt-2">Кто привязан, сколько штрафов уже снято и какой эффективный рейтинг остаётся.</p>
          </div>
          <Button onClick={load}><RefreshCcw size={16} /> <span className="ml-1">Обновить</span></Button>
        </div>

        <div className="grid md:grid-cols-3 gap-4">
          <Card><div className="text-sm opacity-70">Привязанных</div><div className="text-3xl font-semibold mt-2">{stats.total}</div></Card>
          <Card><div className="text-sm opacity-70">Списано рейтинга</div><div className="text-3xl font-semibold mt-2">{stats.totalPenalty}</div></Card>
          <Card><div className="text-sm opacity-70">С отрицат. effective score</div><div className="text-3xl font-semibold mt-2">{stats.debuffed}</div></Card>
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
            <Card key={x.userId}>
              <div className="grid xl:grid-cols-[1.1fr,1fr,1fr] gap-4">
                <div>
                  <div className="font-medium">{x.fullName || x.email}</div>
                  <div className="text-sm opacity-70 mt-1">{x.email}</div>
                  <div className="text-sm mt-3 space-y-1 opacity-80">
                    <div>Nick: {x.minecraftNick || '—'}</div>
                    <div>UUID: {x.minecraftUuid || '—'}</div>
                    <div>Привязан: {x.linkedAtUtc ? new Date(x.linkedAtUtc).toLocaleString() : '—'}</div>
                    <div>Сколько раз привязывал: {x.linkCount ?? 0}</div>
                  </div>
                </div>
                <div className="space-y-3">
                  <div className="grid grid-cols-2 gap-3">
                    <div className="rounded-2xl border px-4 py-3"><div className="text-xs opacity-60">Total score</div><div className="text-2xl font-semibold mt-1">{x.totalScore ?? 0}</div></div>
                    <div className="rounded-2xl border px-4 py-3"><div className="text-xs opacity-60">Effective score</div><div className="text-2xl font-semibold mt-1">{x.effectiveScore ?? 0}</div></div>
                    <div className="rounded-2xl border px-4 py-3"><div className="text-xs opacity-60">Penalty total</div><div className="text-2xl font-semibold mt-1">{x.totalPenalty ?? 0}</div></div>
                    <div className="rounded-2xl border px-4 py-3"><div className="text-xs opacity-60">Weekly joins</div><div className="text-2xl font-semibold mt-1">{x.weeklyJoinEvents ?? 0}</div></div>
                  </div>
                </div>
                <div className="space-y-3">
                  <div className="flex flex-wrap gap-2">
                    {(x.featureRoles || []).length ? x.featureRoles.map((r) => <Badge key={r} intent="outline">{r}</Badge>) : <Badge intent="secondary">Без доп. ролей</Badge>}
                    {(x.effectiveScore ?? 0) < 0 ? <Badge intent="danger">Debuffed</Badge> : <Badge intent="success">OK</Badge>}
                  </div>
                  <div className="text-sm opacity-70">Последний штраф: {x.lastPenaltyAtUtc ? new Date(x.lastPenaltyAtUtc).toLocaleString() : 'ещё не было'}</div>
                </div>
              </div>
            </Card>
          ))}
        </div>
      </div>
    </Layout>
  );
}
