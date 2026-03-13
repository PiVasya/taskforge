import React, { useEffect, useState } from 'react';
import Layout from '../../components/Layout';
import { Badge, Button, Card, Field, Input, Select } from '../../components/ui';
import { deleteAdminUser, getAdminUsers, updateAdminUser } from '../../api/adminUsers';
import { handleApiError } from '../../utils/handleApiError';
import { useNotify } from '../../components/notify/NotifyProvider';
import { AlertTriangle, Save, Search, Trash2, UserCog } from 'lucide-react';

const roles = ['User', 'Editor', 'Admin'];

export default function AdminUsersPage() {
  const notify = useNotify();
  const [items, setItems] = useState([]);
  const [loading, setLoading] = useState(true);
  const [query, setQuery] = useState('');
  const [role, setRole] = useState('');
  const [linkedOnly, setLinkedOnly] = useState(false);
  const [pageError, setPageError] = useState('');
  const [stats, setStats] = useState({ total: 0, linked: 0, admins: 0 });

  const load = async () => {
    try {
      setLoading(true);
      const res = await getAdminUsers({ query, role, linkedOnly });
      setItems(Array.isArray(res?.items) ? res.items : []);
      setStats(res?.stats || { total: 0, linked: 0, admins: 0 });
      setPageError('');
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось загрузить пользователей');
      setPageError(parsed?.userMessage || e?.message || 'Не удалось загрузить пользователей');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { load(); }, []); // eslint-disable-line

  const updateLocal = (id, patch) => setItems((prev) => prev.map((x) => x.id === id ? { ...x, ...patch } : x));


  const save = async (user) => {
    try {
      await updateAdminUser(user.id, {
        email: user.email,
        firstName: user.firstName,
        lastName: user.lastName,
        role: user.role,
        emailConfirmed: !!user.emailConfirmed,
        lockoutEnabled: !!user.lockoutEnabled,
        phoneNumber: user.phoneNumber || null,
        minecraftNick: user.minecraftNick || null,
        telegramUsername: user.telegramUsername || null,
      });
      notify.success('Пользователь обновлён');
      setPageError('');
      await load();
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось сохранить пользователя');
      setPageError(parsed?.userMessage || e?.message || 'Не удалось сохранить пользователя');
    }
  };


  const removeUser = async (user) => {
    const label = user?.email || user?.fullName || user?.id;
    const ok = window.confirm(`Удалить пользователя ${label}? Будут удалены аккаунт, решения и связанные записи.`);
    if (!ok) return;

    try {
      await deleteAdminUser(user.id);
      notify.success('Пользователь удалён');
      setItems((prev) => prev.filter((x) => x.id !== user.id));
      setStats((prev) => ({
        total: Math.max(0, (prev?.total || 0) - 1),
        linked: Math.max(0, (prev?.linked || 0) - ((user.minecraftLinkedAtUtc || user.telegramLinkedAtUtc) ? 1 : 0)),
        admins: Math.max(0, (prev?.admins || 0) - (user.role === 'Admin' ? 1 : 0)),
      }));
      setPageError('');
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось удалить пользователя');
      setPageError(parsed?.userMessage || e?.message || 'Не удалось удалить пользователя');
    }
  };

  return (
    <Layout>
      <div className="space-y-4 sm:space-y-6">
        <div className="flex flex-col lg:flex-row lg:items-center lg:justify-between gap-3">
          <div>
            <h1 className="text-xl sm:text-2xl font-semibold flex items-center gap-2"><UserCog size={22} /> Пользователи</h1>
            <p className="text-sm text-neutral-500 mt-2">Поиск, редактирование базовой информации, ролей и интеграций.</p>
          </div>
          <Button onClick={load}><Search size={16} /> <span className="ml-1">Обновить</span></Button>
        </div>

        {pageError ? (
          <Card className="border-rose-300 bg-rose-50 text-rose-700">
            <div className="flex items-start gap-2">
              <AlertTriangle size={18} className="mt-0.5" />
              <div>
                <div className="font-medium">Ошибка админ-раздела</div>
                <div className="text-sm mt-1 whitespace-pre-wrap">{pageError}</div>
              </div>
            </div>
          </Card>
        ) : null}

        <div className="grid grid-cols-1 gap-3 sm:grid-cols-3 sm:gap-4">
          <Card><div className="text-sm opacity-70">Показано пользователей</div><div className="text-3xl font-semibold mt-2">{stats.total}</div></Card>
          <Card><div className="text-sm opacity-70">С интеграциями</div><div className="text-3xl font-semibold mt-2">{stats.linked}</div></Card>
          <Card><div className="text-sm opacity-70">Администраторов</div><div className="text-3xl font-semibold mt-2">{stats.admins}</div></Card>
        </div>

        <Card>
          <div className="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-[1fr,180px,180px,140px] gap-3 items-end">
            <Field label="Поиск"><Input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="email / имя / minecraft / telegram" /></Field>
            <Field label="Базовая роль"><Select value={role} onChange={(e) => setRole(e.target.value)}><option value="">Все</option>{roles.map((x) => <option key={x} value={x}>{x}</option>)}</Select></Field>
            <Field label="Только с привязками"><label className="flex items-center gap-2 mt-3"><input type="checkbox" checked={linkedOnly} onChange={(e) => setLinkedOnly(e.target.checked)} /><span className="text-sm">Да</span></label></Field>
            <Button className="w-full xl:w-auto" onClick={load}>Найти</Button>
          </div>
        </Card>

        {loading && <div className="text-neutral-500">Загрузка…</div>}

        <div className="space-y-4">
          {items.map((user) => (
            <Card key={user.id}>
              <div className="grid grid-cols-1 gap-4 xl:grid-cols-[1.4fr,1fr] xl:gap-5">
                <div className="grid grid-cols-1 md:grid-cols-2 gap-3 sm:gap-4">
                  <Field label="Email"><Input value={user.email || ''} onChange={(e) => updateLocal(user.id, { email: e.target.value })} /></Field>
                  <Field label="Базовая роль"><Select value={user.role || 'User'} onChange={(e) => updateLocal(user.id, { role: e.target.value })}>{roles.map((x) => <option key={x} value={x}>{x}</option>)}</Select></Field>
                  <Field label="Имя"><Input value={user.firstName || ''} onChange={(e) => updateLocal(user.id, { firstName: e.target.value })} /></Field>
                  <Field label="Фамилия"><Input value={user.lastName || ''} onChange={(e) => updateLocal(user.id, { lastName: e.target.value })} /></Field>
                  <Field label="Телефон"><Input value={user.phoneNumber || ''} onChange={(e) => updateLocal(user.id, { phoneNumber: e.target.value })} /></Field>
                  <Field label="Minecraft nick"><Input value={user.minecraftNick || ''} onChange={(e) => updateLocal(user.id, { minecraftNick: e.target.value })} /></Field>
                  <Field label="Telegram username"><Input value={user.telegramUsername || ''} onChange={(e) => updateLocal(user.id, { telegramUsername: e.target.value })} /></Field>
                  <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                    <Field label="Email подтверждён"><label className="flex items-center gap-2 mt-3"><input type="checkbox" checked={!!user.emailConfirmed} onChange={(e) => updateLocal(user.id, { emailConfirmed: e.target.checked })} /><span className="text-sm">Да</span></label></Field>
                    <Field label="Lockout enabled"><label className="flex items-center gap-2 mt-3"><input type="checkbox" checked={!!user.lockoutEnabled} onChange={(e) => updateLocal(user.id, { lockoutEnabled: e.target.checked })} /><span className="text-sm">Да</span></label></Field>
                  </div>
                </div>
                <div className="space-y-4">
                  <div>
                    <div className="text-sm opacity-70">Решённые задания</div>
                    <div className="flex flex-wrap gap-2 mt-2">
                      <Badge>Code: {user.codeSolutions ?? 0}</Badge>
                      <Badge>Test: {user.passedTests ?? 0}</Badge>
                      <Badge>Image: {user.imageSolutions ?? 0}</Badge>
                    </div>
                  </div>
                  <div>
                    <div className="text-sm opacity-70">Доп. роли</div>
                    <div className="flex flex-wrap gap-2 mt-2">{(user.featureRoles || []).length ? user.featureRoles.map((x) => <Badge key={x} intent="outline">{x}</Badge>) : <span className="text-sm opacity-60">Нет</span>}</div>
                  </div>
                  <div className="text-sm opacity-70 space-y-1 break-words">
                    <div>Создан: {user.createdAt ? new Date(user.createdAt).toLocaleString() : '—'}</div>
                    <div>Последний вход: {user.lastLoginAt ? new Date(user.lastLoginAt).toLocaleString() : '—'}</div>
                    <div>Minecraft привязан: {user.minecraftLinkedAtUtc ? new Date(user.minecraftLinkedAtUtc).toLocaleString() : 'нет'}</div>
                    <div>Telegram привязан: {user.telegramLinkedAtUtc ? new Date(user.telegramLinkedAtUtc).toLocaleString() : 'нет'}</div>
                  </div>
                  <div className="flex flex-col sm:flex-row flex-wrap gap-2">
                    <Button className="w-full sm:w-auto" onClick={() => save(user)}><Save size={16} /> <span className="ml-1">Сохранить</span></Button>
                    <Button intent="danger" className="w-full sm:w-auto" onClick={() => removeUser(user)}><Trash2 size={16} /> <span className="ml-1">Удалить</span></Button>
                  </div>
                </div>
              </div>
            </Card>
          ))}
        </div>
      </div>
    </Layout>
  );
}
