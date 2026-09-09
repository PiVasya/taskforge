import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { Badge, Button, Card, Field, Input } from '../../components/ui';
import {
  getAdminMinecraftLinks,
  unlinkAdminMinecraftLink,
  unlinkAllAdminMinecraftLinks,
} from '../../api/adminMinecraftLinks';
import { handleApiError } from '../../utils/handleApiError';
import { useNotify } from '../../components/notify/NotifyProvider';
import AppErrorPanel from '../../components/AppErrorPanel';
import { ExternalLink, Link2, RefreshCcw, Trash2 } from 'lucide-react';

export default function AdminMinecraftLinksPage() {
  const notify = useNotify();
  const [items, setItems] = useState([]);
  const [loading, setLoading] = useState(true);
  const [pageError, setPageError] = useState(null);
  const [query, setQuery] = useState('');
  const [busy, setBusy] = useState('');

  const load = useCallback(async (searchQuery = '') => {
    try {
      setLoading(true);
      const list = await getAdminMinecraftLinks({ query: searchQuery });
      setItems(Array.isArray(list) ? list : []);
      setPageError(null);
    } catch (e) {
      setPageError(handleApiError(e, notify, 'Не удалось загрузить связи Minecraft'));
    } finally { setLoading(false); }
  }, [notify]);

  useEffect(() => { load(''); }, [load]);

  const stats = useMemo(() => ({
    users: items.length,
    profiles: items.reduce((sum, x) => sum + Number(x.activeLinkCount ?? (x.activeLinks || []).length ?? 0), 0),
    totalSpent: items.reduce((sum, x) => sum + Number(x.minecraftSpent ?? x.totalSpent ?? 0), 0),
    totalRestored: items.reduce((sum, x) => sum + Number(x.minecraftRestored || 0), 0),
  }), [items]);

  const unlinkOne = async (link, row) => {
    const ok = await notify.confirm({ title: 'Отвязать Minecraft-профиль?', message: `${link.nick || link.uuid} будет отвязан от ${row.fullName || row.login || row.userId}. Общий баланс сохранится.`, okText: 'Отвязать', cancelText: 'Отмена' });
    if (!ok) return;
    try {
      setBusy(link.id);
      await unlinkAdminMinecraftLink(link.id);
      notify.success('Minecraft-профиль отвязан');
      await load(query);
    } catch (e) { setPageError(handleApiError(e, notify, 'Не удалось отвязать Minecraft-профиль')); }
    finally { setBusy(''); }
  };

  const unlinkAll = async (row) => {
    const count = Number(row.activeLinkCount ?? (row.activeLinks || []).length ?? 0);
    const ok = await notify.confirm({ title: 'Отвязать все Minecraft-профили?', message: `Будут отключены все активные профили пользователя (${count}). Рейтинг и Minecraft-баланс не изменятся.`, okText: 'Отвязать все', cancelText: 'Отмена' });
    if (!ok) return;
    try {
      setBusy(`all-${row.userId}`);
      await unlinkAllAdminMinecraftLinks(row.userId);
      notify.success('Все Minecraft-профили отвязаны');
      await load(query);
    } catch (e) { setPageError(handleApiError(e, notify, 'Не удалось отвязать Minecraft-профили')); }
    finally { setBusy(''); }
  };

  return (
    <div className="space-y-6">
      {pageError ? <AppErrorPanel error={pageError} title="Не удалось загрузить админ-раздел" /> : null}

      <div className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
        <div><h1 className="flex items-center gap-2 text-2xl font-semibold"><Link2 size={22} /> Связи с Minecraft</h1><p className="mt-2 text-sm text-neutral-500">Все активные ники и UUID. У одного TaskForge-аккаунта может быть несколько Minecraft-профилей.</p></div>
        <Button onClick={() => load(query)}><RefreshCcw size={16} /><span className="ml-1">Обновить</span></Button>
      </div>

      <div className="grid gap-4 md:grid-cols-4">
        <Card><div className="text-sm opacity-70">Пользователей</div><div className="mt-2 text-3xl font-semibold">{stats.users}</div></Card>
        <Card><div className="text-sm opacity-70">Активных профилей</div><div className="mt-2 text-3xl font-semibold">{stats.profiles}</div></Card>
        <Card><div className="text-sm opacity-70">Потрачено рейтинга</div><div className="mt-2 text-3xl font-semibold">{stats.totalSpent}</div></Card>
        <Card><div className="text-sm opacity-70">Восстановлено</div><div className="mt-2 text-3xl font-semibold">{stats.totalRestored}</div></Card>
      </div>

      <Card><div className="flex items-end gap-2"><div className="flex-1"><Field label="Поиск"><Input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="имя / логин / email / nick / uuid" /></Field></div><Button onClick={() => load(query)}>Найти</Button></div></Card>
      {loading ? <div className="text-neutral-500">Загрузка…</div> : null}

      <div className="space-y-4">
        {items.map((row) => {
          const links = Array.isArray(row.activeLinks) ? row.activeLinks : [];
          return (
            <Card key={row.userId || row.id}>
              <div className="flex flex-col gap-4 xl:flex-row xl:items-start xl:justify-between">
                <div className="min-w-0">
                  <div className="text-lg font-semibold">{row.fullName || row.displayName || row.login || row.email || row.userId}</div>
                  <div className="mt-1 text-sm text-neutral-500">{row.login || 'без логина'} · {row.email || 'без email'}</div>
                  <div className="mt-1 break-all text-xs text-neutral-500">{row.userId}</div>
                  <div className="mt-3 flex flex-wrap gap-2"><Badge intent={links.length ? 'success' : 'outline'}>{links.length} активных</Badge>{(row.featureRoles || []).map((role) => <Badge key={role} intent="outline">{role}</Badge>)}</div>
                </div>
                <div className="grid grid-cols-2 gap-2 text-sm sm:grid-cols-4">
                  <div className="rounded-xl border border-[rgb(var(--border))] p-3"><div className="text-xs text-neutral-500">Основной</div><div className="mt-1 text-xl font-semibold">{row.totalScore ?? 0}</div></div>
                  <div className="rounded-xl border border-[rgb(var(--border))] p-3"><div className="text-xs text-neutral-500">MC баланс</div><div className="mt-1 text-xl font-semibold">{row.minecraftBalance ?? 0}</div></div>
                  <div className="rounded-xl border border-[rgb(var(--border))] p-3"><div className="text-xs text-neutral-500">Потрачено</div><div className="mt-1 text-xl font-semibold">{row.minecraftSpent ?? 0}</div></div>
                  <div className="rounded-xl border border-[rgb(var(--border))] p-3"><div className="text-xs text-neutral-500">Возвращено</div><div className="mt-1 text-xl font-semibold">{row.minecraftRestored ?? 0}</div></div>
                </div>
                <div className="flex flex-col gap-2 sm:flex-row xl:flex-col">
                  <Link to={`/admin/users/${row.userId}`}><Button variant="outline" className="w-full"><ExternalLink size={16} /><span className="ml-1">Управление</span></Button></Link>
                  {links.length > 1 ? <Button variant="outline" className="text-red-600 border-red-300 hover:bg-red-50 dark:hover:bg-red-900/20" disabled={busy === `all-${row.userId}`} onClick={() => unlinkAll(row)}><Trash2 size={16} /><span className="ml-1">Отвязать все</span></Button> : null}
                </div>
              </div>

              <div className="mt-4 grid gap-3 lg:grid-cols-2 2xl:grid-cols-3">
                {links.map((link) => (
                  <div key={link.id} className="rounded-2xl border border-[rgb(var(--border))] p-3">
                    <div className="flex items-start justify-between gap-3">
                      <div className="min-w-0"><div className="font-medium">{link.nick || 'Без ника'}</div><div className="mt-1 break-all text-xs text-neutral-500">UUID: {link.uuid || 'ещё не закреплён'}</div><div className="mt-1 text-xs text-neutral-500">Привязан: {link.linkedAtUtc ? new Date(link.linkedAtUtc).toLocaleString() : '—'}</div></div>
                      <Button variant="outline" className="text-red-600 border-red-300 hover:bg-red-50 dark:hover:bg-red-900/20" disabled={busy === link.id} onClick={() => unlinkOne(link, row)}>Отвязать</Button>
                    </div>
                  </div>
                ))}
              </div>
            </Card>
          );
        })}
      </div>
    </div>
  );
}
