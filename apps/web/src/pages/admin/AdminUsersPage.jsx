import React, { useEffect, useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { Badge, Button, Card, Field, Input, Select } from '../../components/ui';
import { getAdminUsers } from '../../api/adminUsers';
import { getAdminMinecraftLinks } from '../../api/adminMinecraftLinks';
import { searchUsersOnce } from '../../api/admin';
import { handleApiError } from '../../utils/handleApiError';
import { useNotify } from '../../components/notify/NotifyProvider';
import AppErrorPanel from '../../components/AppErrorPanel';
import { ContextMenu, ContextMenuItem, ContextMenuLabel, ContextMenuSeparator } from '../../components/ui/ContextMenu';
import { Ban, Bot, Copy, ExternalLink, Link2, RefreshCcw, Search, ShieldCheck, UserCog } from 'lucide-react';

const roles = ['User', 'Editor', 'Admin'];

const sortOptions = [
  { value: 'createdAt', label: 'Дата регистрации' },
  { value: 'lastLoginAt', label: 'Дата последнего входа' },
  { value: 'fullName', label: 'Имя и фамилия' },
  { value: 'login', label: 'Логин' },
  { value: 'email', label: 'Email' },
  { value: 'role', label: 'Базовая роль' },
  { value: 'accountType', label: 'Тип аккаунта' },
  { value: 'minecraftNick', label: 'Minecraft nick' },
  { value: 'telegramUsername', label: 'Telegram username' },
  { value: 'minecraftLinkedAt', label: 'Дата привязки Minecraft' },
  { value: 'telegramLinkedAt', label: 'Дата привязки Telegram' },
  { value: 'totalSolved', label: 'Всего решённых' },
  { value: 'rating', label: 'Рейтинг' },
  { value: 'linked', label: 'Наличие привязок' },
  { value: 'blocked', label: 'Блокировка' },
];

const normalize = (value) => String(value || '').trim().toLowerCase();
const dateValue = (value) => value ? new Date(value).getTime() || 0 : 0;
const formatDate = (value) => value ? new Date(value).toLocaleString() : '—';
const formatTelegramHandle = (value) => {
  const raw = String(value || '').trim();
  if (!raw) return null;
  return raw.startsWith('@') ? raw : `@${raw}`;
};

const accountStatusLabel = (user) => {
  if (user.blocked) return 'Заблокирован';
  if (user.accountStatus === 'merged') return 'Объединён';
  if (user.accountStatus === 'deleted') return 'Удалён';
  return 'Активен';
};

const accountStatusIntent = (user) => {
  if (user.blocked || user.accountStatus === 'deleted') return 'danger';
  if (user.accountStatus === 'merged') return 'outline';
  return 'success';
};

const totalSolved = (user) => Number(user.codeSolutions || 0)
  + Number(user.passedTests || 0)
  + Number(user.imageSolutions || 0)
  + Number(user.mathSolutions || 0);

function Metric({ label, value, hint, icon: Icon }) {
  return (
    <Card>
      <div className="flex items-center justify-between gap-3">
        <div>
          <div className="text-sm opacity-70">{label}</div>
          <div className="mt-2 text-3xl font-semibold">{value}</div>
          {hint ? <div className="mt-2 text-xs text-neutral-500">{hint}</div> : null}
        </div>
        {Icon ? <Icon size={24} className="opacity-50" /> : null}
      </div>
    </Card>
  );
}

export default function AdminUsersPage() {
  const notify = useNotify();
  const navigate = useNavigate();
  const [items, setItems] = useState([]);
  const [loading, setLoading] = useState(true);
  const [query, setQuery] = useState('');
  const [role, setRole] = useState('');
  const [accountType, setAccountType] = useState('');
  const [status, setStatus] = useState('active');
  const [linkedOnly, setLinkedOnly] = useState(false);
  const [sortBy, setSortBy] = useState('createdAt');
  const [sortDir, setSortDir] = useState('desc');
  const [pageError, setPageError] = useState(null);
  const [contextMenu, setContextMenu] = useState({ open: false, x: 0, y: 0, user: null });

  const closeContextMenu = () => setContextMenu((current) => current.open ? { ...current, open: false } : current);
  const openContextMenu = (event, selectedUser) => {
    event.preventDefault();
    event.stopPropagation();
    setContextMenu({ open: true, x: event.clientX, y: event.clientY, user: selectedUser });
  };
  const copyUserValue = async (value, label) => {
    if (!value) return;
    try {
      await navigator.clipboard.writeText(String(value));
      notify.success(`${label} скопирован`);
    } catch {
      notify.warn('Не удалось скопировать');
    }
  };

  const load = async () => {
    try {
      setLoading(true);
      const [res, ratingRows, minecraftRows] = await Promise.all([
        getAdminUsers({ includeInactive: true, sortBy: 'createdAt', sortDir: 'desc', take: 500 }),
        searchUsersOnce('', 1000).catch(() => []),
        getAdminMinecraftLinks({}).catch(() => []),
      ]);

      const ratingById = new Map((ratingRows || []).map((x) => [String(x.id || x.userId).toLowerCase(), x]));
      const minecraftByUserId = new Map((minecraftRows || []).map((x) => [String(x.userId || '').toLowerCase(), x]));
      const mergedItems = (Array.isArray(res?.items) ? res.items : []).map((user) => {
        const key = String(user.id || '').toLowerCase();
        const rating = ratingById.get(key) || {};
        const minecraft = minecraftByUserId.get(key) || null;
        const activeLinks = Array.isArray(minecraft?.activeLinks) ? minecraft.activeLinks : [];
        return {
          ...user,
          score: rating.score ?? rating.rating ?? rating.totalScore ?? 0,
          codeSolutions: rating.codeSolutions ?? user.codeSolutions ?? 0,
          passedTests: rating.passedTests ?? user.passedTests ?? 0,
          imageSolutions: rating.imageSolutions ?? user.imageSolutions ?? 0,
          mathSolutions: rating.mathSolutions ?? user.mathSolutions ?? 0,
          minecraft,
          minecraftLinks: activeLinks,
        };
      });

      setItems(mergedItems);
      setPageError(null);
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось загрузить пользователей');
      setPageError(parsed);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { load(); }, []);

  const filteredItems = useMemo(() => {
    const search = normalize(query);
    const rows = items.filter((user) => {
      if (role && user.role !== role) return false;
      if (accountType && (user.accountType || 'human') !== accountType) return false;
      if (status === 'active' && (user.accountStatus !== 'active' || user.blocked)) return false;
      if (status === 'blocked' && !user.blocked) return false;
      if (status === 'merged' && user.accountStatus !== 'merged') return false;
      if (status === 'deleted' && user.accountStatus !== 'deleted') return false;

      const hasTelegram = !!user.telegramLinked;
      const hasMinecraft = (user.minecraftLinks || []).length > 0;
      if (linkedOnly && !hasTelegram && !hasMinecraft) return false;

      if (!search) return true;
      const haystack = [
        user.id,
        user.login,
        user.email,
        user.firstName,
        user.lastName,
        user.fullName,
        user.telegramUsername,
        user.telegramChatId,
        user.accountType,
        ...(user.minecraftLinks || []).flatMap((link) => [link.nick, link.uuid]),
      ].map(normalize).join(' ');
      return haystack.includes(search);
    });

    const valueFor = (user) => {
      const firstMinecraft = (user.minecraftLinks || [])[0] || {};
      switch (sortBy) {
        case 'createdAt': return dateValue(user.createdAt);
        case 'lastLoginAt': return dateValue(user.lastLoginAt);
        case 'fullName': return normalize(user.fullName || user.displayName);
        case 'login': return normalize(user.login);
        case 'email': return normalize(user.email);
        case 'role': return normalize(user.role);
        case 'accountType': return normalize(user.accountType || 'human');
        case 'minecraftNick': return normalize(firstMinecraft.nick);
        case 'telegramUsername': return normalize(user.telegramUsername);
        case 'minecraftLinkedAt': return dateValue(firstMinecraft.linkedAtUtc);
        case 'telegramLinkedAt': return dateValue(user.telegramLinkedAtUtc);
        case 'totalSolved': return totalSolved(user);
        case 'rating': return Number(user.score || 0);
        case 'linked': return (user.telegramLinked ? 1 : 0) + ((user.minecraftLinks || []).length ? 1 : 0);
        case 'blocked': return user.blocked ? 1 : 0;
        default: return 0;
      }
    };

    return [...rows].sort((a, b) => {
      const av = valueFor(a);
      const bv = valueFor(b);
      let result;
      if (typeof av === 'string' || typeof bv === 'string') result = String(av).localeCompare(String(bv), 'ru');
      else result = Number(av) - Number(bv);
      return sortDir === 'asc' ? result : -result;
    });
  }, [accountType, items, linkedOnly, query, role, sortBy, sortDir, status]);

  const stats = useMemo(() => ({
    total: items.length,
    active: items.filter((x) => x.accountStatus === 'active' && !x.blocked).length,
    blocked: items.filter((x) => x.blocked).length,
    linked: items.filter((x) => x.telegramLinked || (x.minecraftLinks || []).length > 0).length,
    ai: items.filter((x) => x.accountType === 'ai' || x.isAi).length,
  }), [items]);

  return (
    <div className="space-y-4 sm:space-y-6">
      <div className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
        <div>
          <h1 className="flex items-center gap-2 text-xl font-semibold sm:text-2xl"><UserCog size={22} /> Управление пользователями</h1>
          <p className="mt-2 text-sm text-neutral-500">Актуальные аккаунты, блокировки, Telegram и все активные Minecraft-профили. Изменения выполняются в карточке пользователя.</p>
        </div>
        <Button onClick={load}><RefreshCcw size={16} /><span className="ml-1">Обновить</span></Button>
      </div>

      {pageError ? <AppErrorPanel error={pageError} title="Не удалось загрузить админ-раздел" /> : null}

      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-5 sm:gap-4">
        <Metric label="Всего аккаунтов" value={stats.total} hint="Включая удалённые и объединённые" icon={UserCog} />
        <Metric label="Активные" value={stats.active} hint="Можно редактировать и использовать" icon={ShieldCheck} />
        <Metric label="Заблокированные" value={stats.blocked} hint="Вход отключён на всей платформе" icon={Ban} />
        <Metric label="С интеграциями" value={stats.linked} hint="Telegram или Minecraft" icon={Link2} />
        <Metric label="AI-аккаунты" value={stats.ai} hint="Самодекларированные автоматизированные пользователи" icon={Bot} />
      </div>

      <Card>
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-[1fr,150px,150px,180px,180px,170px,150px] items-end">
          <Field label="Поиск"><Input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="логин / email / имя / Telegram / Minecraft nick / UUID" /></Field>
          <Field label="Базовая роль"><Select value={role} onChange={(e) => setRole(e.target.value)}><option value="">Все</option>{roles.map((x) => <option key={x} value={x}>{x}</option>)}</Select></Field>
          <Field label="Тип аккаунта"><Select value={accountType} onChange={(e) => setAccountType(e.target.value)}><option value="">Все</option><option value="human">Человек</option><option value="ai">AI</option></Select></Field>
          <Field label="Состояние"><Select value={status} onChange={(e) => setStatus(e.target.value)}><option value="active">Активные</option><option value="blocked">Заблокированные</option><option value="merged">Объединённые</option><option value="deleted">Удалённые</option><option value="">Все состояния</option></Select></Field>
          <Field label="Сортировать по"><Select value={sortBy} onChange={(e) => setSortBy(e.target.value)}>{sortOptions.map((x) => <option key={x.value} value={x.value}>{x.label}</option>)}</Select></Field>
          <Field label="Порядок"><Select value={sortDir} onChange={(e) => setSortDir(e.target.value)}><option value="desc">По убыванию</option><option value="asc">По возрастанию</option></Select></Field>
          <Field label="Только с привязками"><label className="mt-3 flex items-center gap-2"><input type="checkbox" checked={linkedOnly} onChange={(e) => setLinkedOnly(e.target.checked)} /><span className="text-sm">Да</span></label></Field>
        </div>
      </Card>

      {loading ? <div className="text-neutral-500">Загрузка…</div> : null}
      {!loading && filteredItems.length === 0 ? <Card><div className="text-sm text-neutral-500">Пользователи по выбранным условиям не найдены.</div></Card> : null}

      <div className="space-y-3">
        {filteredItems.map((user) => {
          const telegram = formatTelegramHandle(user.telegramUsername);
          const minecraftLinks = user.minecraftLinks || [];
          return (
            <Card key={user.id} className="p-4 sm:p-5" onContextMenu={(event) => openContextMenu(event, user)}>
              <div className="grid gap-4 xl:grid-cols-[1.1fr,1fr,0.9fr,auto] xl:items-center">
                <div className="min-w-0">
                  <button type="button" className="text-left group min-w-0" onClick={() => navigate(`/admin/users/${user.id}`)}>
                    <div className="truncate text-lg font-semibold group-hover:text-[rgb(var(--accent-600))]">{user.fullName || user.displayName || user.login || user.id}</div>
                    <div className="mt-1 text-sm text-neutral-500">{user.login || 'без логина'} · {user.email || 'без email'}</div>
                    <div className="mt-1 break-all text-xs text-neutral-500">{user.id}</div>
                  </button>
                  <div className="mt-3 flex flex-wrap gap-2">
                    <Badge intent={accountStatusIntent(user)}>{accountStatusLabel(user)}</Badge>
                    <Badge intent="outline">{user.role || 'User'}</Badge>
                    {(user.accountType === 'ai' || user.isAi) ? <Badge intent="outline"><Bot size={12} className="mr-1 inline" />AI</Badge> : null}
                    {user.mergedIntoUserId ? <Badge intent="outline">Основной: {String(user.mergedIntoUserId).slice(0, 8)}…</Badge> : null}
                  </div>
                </div>

                <div>
                  <div className="text-xs font-medium uppercase tracking-wide text-neutral-500">Интеграции</div>
                  <div className="mt-2 space-y-2 text-sm">
                    <div className="rounded-xl border border-[rgb(var(--border))] px-3 py-2">
                      <div className="font-medium">Telegram</div>
                      <div className="mt-1 text-xs text-neutral-500">{user.telegramLinked ? `${telegram || 'username не задан'} · ${formatDate(user.telegramLinkedAtUtc)}` : 'Не привязан'}</div>
                    </div>
                    <div className="rounded-xl border border-[rgb(var(--border))] px-3 py-2">
                      <div className="font-medium">Minecraft · {minecraftLinks.length}</div>
                      <div className="mt-1 text-xs text-neutral-500 break-words">
                        {minecraftLinks.length ? minecraftLinks.slice(0, 4).map((link) => link.nick || link.uuid).join(', ') : 'Активных профилей нет'}
                        {minecraftLinks.length > 4 ? ` и ещё ${minecraftLinks.length - 4}` : ''}
                      </div>
                    </div>
                  </div>
                </div>

                <div className="grid grid-cols-2 gap-2 text-sm">
                  <div className="rounded-xl border border-[rgb(var(--border))] p-3"><div className="text-xs text-neutral-500">Рейтинг</div><div className="mt-1 text-xl font-semibold">{user.score || 0}</div></div>
                  <div className="rounded-xl border border-[rgb(var(--border))] p-3"><div className="text-xs text-neutral-500">Решено</div><div className="mt-1 text-xl font-semibold">{totalSolved(user)}</div></div>
                  <div className="col-span-2 rounded-xl border border-[rgb(var(--border))] p-3 text-xs text-neutral-500">
                    <div>Создан: {formatDate(user.createdAt)}</div>
                    <div className="mt-1">Последний вход: {formatDate(user.lastLoginAt)}</div>
                  </div>
                </div>

                <Button variant="outline" className="w-full xl:w-auto" onClick={() => navigate(`/admin/users/${user.id}`)}>
                  <ExternalLink size={16} /><span className="ml-1">Открыть</span>
                </Button>
              </div>
            </Card>
          );
        })}
      </div>

      <ContextMenu open={contextMenu.open} x={contextMenu.x} y={contextMenu.y} onClose={closeContextMenu} ariaLabel="Действия пользователя">
        {contextMenu.user ? (
          <>
            <ContextMenuLabel>Пользователь</ContextMenuLabel>
            <ContextMenuItem icon={UserCog} onClick={() => { const selected = contextMenu.user; closeContextMenu(); navigate(`/admin/users/${selected.id}`); }}>Управление пользователем</ContextMenuItem>
            <ContextMenuItem icon={ExternalLink} onClick={() => { const selected = contextMenu.user; closeContextMenu(); navigate(`/users/${selected.id}`); }}>Публичный профиль</ContextMenuItem>
            <ContextMenuSeparator />
            <ContextMenuItem icon={Copy} disabled={!contextMenu.user.login} onClick={() => { const selected = contextMenu.user; closeContextMenu(); void copyUserValue(selected.login, 'Логин'); }}>Скопировать логин</ContextMenuItem>
            <ContextMenuItem icon={Copy} disabled={!contextMenu.user.email} onClick={() => { const selected = contextMenu.user; closeContextMenu(); void copyUserValue(selected.email, 'Email'); }}>Скопировать email</ContextMenuItem>
            <ContextMenuItem icon={Copy} onClick={() => { const selected = contextMenu.user; closeContextMenu(); void copyUserValue(selected.id, 'ID'); }}>Скопировать ID</ContextMenuItem>
          </>
        ) : null}
      </ContextMenu>
    </div>
  );
}
