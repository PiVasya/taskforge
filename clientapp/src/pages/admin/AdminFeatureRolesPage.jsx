import React, { useEffect, useMemo, useState } from 'react';
import Layout from '../../components/Layout';
import { Badge, Button, Card, Field, Input, Textarea } from '../../components/ui';
import { assignFeatureRole, createFeatureRole, deleteFeatureRole, getFeatureRoles, removeFeatureRole, searchFeatureRoleUsers, updateFeatureRole } from '../../api/featureRoles';
import { useNotify } from '../../components/notify/NotifyProvider';
import { handleApiError } from '../../utils/handleApiError';
import { AlertTriangle, Plus, Save, Shield, Trash2, UserPlus, UserX } from 'lucide-react';

const empty = { code: '', name: '', description: '', isActive: true };

export default function AdminFeatureRolesPage() {
  const notify = useNotify();
  const [roles, setRoles] = useState([]);
  const [users, setUsers] = useState([]);
  const [loading, setLoading] = useState(true);
  const [q, setQ] = useState('');
  const [userQuery, setUserQuery] = useState('');
  const [creating, setCreating] = useState(false);
  const [pageError, setPageError] = useState('');
  const [form, setForm] = useState({ ...empty });

  const loadRoles = async () => {
    const list = await getFeatureRoles();
    setRoles(Array.isArray(list) ? list : []);
  };

  const loadUsers = async (query = '') => {
    const list = await searchFeatureRoleUsers(query);
    setUsers(Array.isArray(list) ? list : []);
  };

  useEffect(() => {
    (async () => {
      try {
        setLoading(true);
        await Promise.all([loadRoles(), loadUsers('')]);
        setPageError('');
      } catch (e) {
        const parsed = handleApiError(e, notify, 'Не удалось загрузить дополнительные роли');
        setPageError(parsed?.userMessage || 'Не удалось загрузить дополнительные роли');
      } finally {
        setLoading(false);
      }
    })();
  }, []); 

  const filteredRoles = useMemo(() => {
    const qq = (q || '').trim().toLowerCase();
    if (!qq) return roles;
    return roles.filter((x) => `${x.code} ${x.name} ${x.description || ''}`.toLowerCase().includes(qq));
  }, [roles, q]);

  const updateLocalRole = (id, patch) => setRoles((prev) => prev.map((r) => (r.id === id ? { ...r, ...patch } : r)));

  const saveNew = async () => {
    try {
      await createFeatureRole({
        code: (form.code || '').trim(),
        name: (form.name || '').trim(),
        description: (form.description || '').trim() || null,
        isActive: !!form.isActive,
      });
      notify.success('Роль создана');
      setPageError('');
      setCreating(false);
      setForm({ ...empty });
      await loadRoles();
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось создать роль');
      setPageError(parsed?.userMessage || 'Не удалось создать роль');
    }
  };

  const saveExisting = async (role) => {
    try {
      await updateFeatureRole(role.id, {
        code: (role.code || '').trim(),
        name: (role.name || '').trim(),
        description: (role.description || '').trim() || null,
        isActive: !!role.isActive,
      });
      notify.success('Роль сохранена');
      setPageError('');
      await loadRoles();
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось сохранить роль');
      setPageError(parsed?.userMessage || 'Не удалось сохранить роль');
    }
  };

  const removeRoleDef = async (role) => {
    const ok = await notify.confirm({ title: 'Удалить роль?', message: `Роль ${role.code} будет удалена у всех пользователей.`, okText: 'Удалить', cancelText: 'Отмена' });
    if (!ok) return;
    try {
      await deleteFeatureRole(role.id);
      notify.success('Роль удалена');
      setPageError('');
      await loadRoles();
      await loadUsers(userQuery);
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось удалить роль');
      setPageError(parsed?.userMessage || 'Не удалось удалить роль');
    }
  };

  const toggleRole = async (user, roleCode, enabled) => {
    try {
      if (enabled) await removeFeatureRole(user.id, roleCode);
      else await assignFeatureRole(user.id, roleCode);
      notify.success(enabled ? 'Роль снята' : 'Роль выдана');
      setPageError('');
      await loadUsers(userQuery);
      await loadRoles();
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось изменить роль пользователя');
      setPageError(parsed?.userMessage || 'Не удалось изменить роль пользователя');
    }
  };

  const doSearchUsers = async () => {
    try {
      await loadUsers(userQuery);
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось найти пользователей');
      setPageError(parsed?.userMessage || 'Не удалось найти пользователей');
    }
  };

  return (
    <Layout>
      <div className="space-y-6">
        <div className="flex flex-col lg:flex-row lg:items-center lg:justify-between gap-3">
          <div>
            <h1 className="text-2xl font-semibold flex items-center gap-2"><Shield size={22} /> Дополнительные роли</h1>
            <p className="text-sm text-neutral-500 mt-2">Базовая роль пользователя не меняется. Эти роли открывают отдельные ветки и фичи сайта.</p>
          </div>
          <div className="flex gap-2">
            {!creating ? (
              <Button onClick={() => setCreating(true)}><Plus size={16} /> <span className="ml-1">Создать роль</span></Button>
            ) : (
              <>
                <Button onClick={saveNew}><Save size={16} /> <span className="ml-1">Сохранить</span></Button>
                <Button variant="outline" onClick={() => { setCreating(false); setForm({ ...empty }); }}>Отмена</Button>
              </>
            )}
          </div>
        </div>

        {loading && <div className="text-neutral-500">Загрузка…</div>}

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

        <div className="grid xl:grid-cols-[1.1fr,0.9fr] gap-6">
          <div className="space-y-4">
            <Card>
              <Input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Поиск роли по коду / названию" />
            </Card>

            {creating && (
              <Card>
                <div className="grid md:grid-cols-2 gap-4">
                  <Field label="Code"><Input value={form.code} onChange={(e) => setForm((p) => ({ ...p, code: e.target.value }))} placeholder="Minecraft" /></Field>
                  <Field label="Name"><Input value={form.name} onChange={(e) => setForm((p) => ({ ...p, name: e.target.value }))} placeholder="Minecraft" /></Field>
                  <div className="md:col-span-2"><Field label="Описание"><Textarea rows={3} value={form.description} onChange={(e) => setForm((p) => ({ ...p, description: e.target.value }))} /></Field></div>
                  <Field label="Активна"><label className="flex items-center gap-2 mt-2"><input type="checkbox" checked={!!form.isActive} onChange={(e) => setForm((p) => ({ ...p, isActive: e.target.checked }))} /><span className="text-sm">Да</span></label></Field>
                </div>
              </Card>
            )}

            {filteredRoles.map((role) => (
              <Card key={role.id}>
                <div className="grid md:grid-cols-2 gap-4">
                  <Field label="Code"><Input value={role.code || ''} onChange={(e) => updateLocalRole(role.id, { code: e.target.value })} /></Field>
                  <Field label="Name"><Input value={role.name || ''} onChange={(e) => updateLocalRole(role.id, { name: e.target.value })} /></Field>
                  <div className="md:col-span-2"><Field label="Описание"><Textarea rows={2} value={role.description || ''} onChange={(e) => updateLocalRole(role.id, { description: e.target.value })} /></Field></div>
                  <Field label="Активна"><label className="flex items-center gap-2 mt-2"><input type="checkbox" checked={!!role.isActive} onChange={(e) => updateLocalRole(role.id, { isActive: e.target.checked })} /><span className="text-sm">Да</span></label></Field>
                  <Field label="Назначений"><div className="mt-2"><Badge intent="secondary">{role.membersCount ?? 0}</Badge></div></Field>
                </div>
                <div className="flex flex-wrap gap-2 mt-4">
                  <Button onClick={() => saveExisting(role)}><Save size={16} /> <span className="ml-1">Сохранить</span></Button>
                  <Button variant="outline" onClick={() => removeRoleDef(role)}><Trash2 size={16} /> <span className="ml-1">Удалить</span></Button>
                </div>
              </Card>
            ))}
          </div>

          <div className="space-y-4">
            <Card>
              <div className="flex gap-2">
                <Input value={userQuery} onChange={(e) => setUserQuery(e.target.value)} placeholder="Поиск пользователя по email / имени / minecraft nick" />
                <Button onClick={doSearchUsers}>Найти</Button>
              </div>
            </Card>

            {users.map((user) => (
              <Card key={user.id}>
                <div className="flex flex-col gap-3">
                  <div>
                    <div className="font-medium">{user.fullName || user.email}</div>
                    <div className="text-sm opacity-70">{user.email}</div>
                    <div className="text-xs opacity-60 mt-1">Базовая роль: {user.baseRole}{user.minecraftNick ? ` · Minecraft: ${user.minecraftNick}` : ''}</div>
                  </div>

                  <div className="flex flex-wrap gap-2">
                    {roles.map((role) => {
                      const enabled = (user.featureRoles || []).includes(role.code);
                      return (
                        <button
                          key={`${user.id}-${role.code}`}
                          type="button"
                          className={`px-3 py-2 rounded-xl border text-sm transition ${enabled ? 'border-emerald-500/30 bg-emerald-500/10' : 'border-neutral-200/60 dark:border-neutral-800/60 bg-transparent'}`}
                          onClick={() => toggleRole(user, role.code, enabled)}
                          title={enabled ? 'Снять роль' : 'Выдать роль'}
                        >
                          {enabled ? <UserX size={14} className="inline mr-2" /> : <UserPlus size={14} className="inline mr-2" />}
                          {role.code}
                        </button>
                      );
                    })}
                  </div>
                </div>
              </Card>
            ))}
          </div>
        </div>
      </div>
    </Layout>
  );
}
