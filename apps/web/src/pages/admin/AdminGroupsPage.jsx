import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { Plus, Save, Trash2, Users, UserPlus, X } from 'lucide-react';
import { Card, Button, Field, Input, Textarea, Badge } from '../../components/ui';
import { useNotify } from '../../components/notify/NotifyProvider';
import { handleApiError } from '../../utils/handleApiError';
import { useAuth } from '../../auth/AuthContext';
import { getAdminUsers } from '../../api/adminUsers';
import {
  addGroupMember,
  createGroup,
  deleteGroup,
  getAdminGroupMemberIds,
  getAdminGroups,
  removeGroupMember,
  updateGroup,
} from '../../api/groups';

const emptyForm = { name: '', description: '' };
const emptyEditing = { ...emptyForm, ownerUserIds: [] };

function userIdOf(user) {
  return String(user?.id || user?.userId || '');
}

function userLabel(user) {
  return user?.displayName || user?.fullName || user?.login || user?.email || userIdOf(user) || 'Пользователь';
}

function isOwnerCandidate(user) {
  const role = String(user?.role || '').toLowerCase();
  return role === 'admin' || role === 'superadmin';
}

export default function AdminGroupsPage() {
  const notify = useNotify();
  const { user } = useAuth();
  const isSuperAdmin = String(user?.role || '').toLowerCase() === 'superadmin';

  const [items, setItems] = useState([]);
  const [loading, setLoading] = useState(true);
  const [err, setErr] = useState('');
  const [q, setQ] = useState('');
  const [mineOnly, setMineOnly] = useState(false);
  const [creating, setCreating] = useState(false);
  const [form, setForm] = useState({ ...emptyForm });
  const [selectedId, setSelectedId] = useState('');
  const [editing, setEditing] = useState({ ...emptyEditing });
  const [memberIds, setMemberIds] = useState([]);
  const [members, setMembers] = useState([]);
  const [memberQuery, setMemberQuery] = useState('');
  const [candidates, setCandidates] = useState([]);
  const [memberBusy, setMemberBusy] = useState(false);
  const [ownerQuery, setOwnerQuery] = useState('');
  const [ownerCandidates, setOwnerCandidates] = useState([]);
  const [ownerBusy, setOwnerBusy] = useState(false);
  const [userDirectory, setUserDirectory] = useState([]);

  const load = useCallback(async () => {
    try {
      setLoading(true);
      setErr('');
      const list = await getAdminGroups({ mineOnly: isSuperAdmin && mineOnly });
      setItems(Array.isArray(list) ? list : []);
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось загрузить группы');
      setErr(parsed?.userMessage || 'Не удалось загрузить группы');
    } finally {
      setLoading(false);
    }
  }, [isSuperAdmin, mineOnly, notify]);

  useEffect(() => { load(); }, [load]);

  const filtered = useMemo(() => {
    const needle = q.trim().toLowerCase();
    if (!needle) return items;
    return items.filter((group) => `${group.name || ''} ${group.description || ''}`.toLowerCase().includes(needle));
  }, [items, q]);

  const selected = useMemo(() => items.find((group) => String(group.id) === String(selectedId)) || null, [items, selectedId]);

  const usersById = useMemo(() => {
    const map = new Map();
    userDirectory.forEach((candidate) => {
      const id = userIdOf(candidate).toLowerCase();
      if (id) map.set(id, candidate);
    });
    return map;
  }, [userDirectory]);

  const rememberUsers = useCallback((users) => {
    setUserDirectory((current) => {
      const map = new Map(current.map((candidate) => [userIdOf(candidate).toLowerCase(), candidate]));
      (users || []).forEach((candidate) => {
        const id = userIdOf(candidate).toLowerCase();
        if (id) map.set(id, candidate);
      });
      return Array.from(map.values());
    });
  }, []);

  const loadMembers = useCallback(async (groupId) => {
    if (!groupId) return;
    setMemberBusy(true);
    try {
      const [ids, result] = await Promise.all([
        getAdminGroupMemberIds(groupId),
        getAdminUsers({ take: 500 }),
      ]);
      const all = Array.isArray(result?.items) ? result.items : [];
      rememberUsers(all);
      setMemberIds(ids.map(String));
      const wanted = new Set(ids.map((id) => String(id).toLowerCase()));
      setMembers(all.filter((candidate) => wanted.has(userIdOf(candidate).toLowerCase())));
    } catch (e) {
      handleApiError(e, notify, 'Не удалось загрузить участников');
    } finally {
      setMemberBusy(false);
    }
  }, [notify, rememberUsers]);

  const openGroup = async (group) => {
    setSelectedId(String(group.id));
    setEditing({
      name: group.name || '',
      description: group.description || '',
      ownerUserIds: Array.isArray(group.ownerUserIds) ? group.ownerUserIds.map(String) : [],
    });
    setMemberQuery('');
    setCandidates([]);
    setOwnerQuery('');
    setOwnerCandidates([]);
    await loadMembers(group.id);
  };

  const create = async () => {
    if (!form.name.trim()) {
      notify.warn('Введите название группы');
      return;
    }
    try {
      const created = await createGroup({ name: form.name.trim(), description: form.description.trim() || null });
      notify.success('Группа создана');
      setCreating(false);
      setForm({ ...emptyForm });
      await load();
      if (created?.id) await openGroup(created);
    } catch (e) {
      handleApiError(e, notify, 'Не удалось создать группу');
    }
  };

  const save = async () => {
    if (!selected) return;
    if (!editing.name.trim()) {
      notify.warn('Введите название группы');
      return;
    }
    if (isSuperAdmin && !editing.ownerUserIds.length) {
      notify.warn('У группы должен быть хотя бы один владелец');
      return;
    }
    try {
      await updateGroup(selected.id, {
        name: editing.name.trim(),
        description: editing.description.trim() || null,
        ...(isSuperAdmin ? { ownerUserIds: editing.ownerUserIds } : {}),
      });
      notify.success('Группа сохранена');
      await load();
    } catch (e) {
      handleApiError(e, notify, 'Не удалось сохранить группу');
    }
  };

  const remove = async () => {
    if (!selected) return;
    const confirmed = await notify.confirm({
      title: 'Удалить группу?',
      message: `Удалить «${selected.name}» и её список участников?`,
      okText: 'Удалить',
      cancelText: 'Отмена',
    });
    if (!confirmed) return;
    try {
      await deleteGroup(selected.id);
      notify.success('Группа удалена');
      setSelectedId('');
      setMembers([]);
      setMemberIds([]);
      await load();
    } catch (e) {
      handleApiError(e, notify, 'Не удалось удалить группу');
    }
  };

  const searchMembers = async () => {
    const needle = memberQuery.trim();
    if (needle.length < 2) {
      setCandidates([]);
      return;
    }
    setMemberBusy(true);
    try {
      const result = await getAdminUsers({ q: needle, take: 20 });
      const found = result?.items || [];
      rememberUsers(found);
      const existing = new Set(memberIds.map((id) => id.toLowerCase()));
      setCandidates(found.filter((candidate) => !existing.has(userIdOf(candidate).toLowerCase())));
    } catch (e) {
      handleApiError(e, notify, 'Не удалось найти пользователей');
    } finally {
      setMemberBusy(false);
    }
  };

  const searchOwners = async () => {
    const needle = ownerQuery.trim();
    if (needle.length < 2) {
      setOwnerCandidates([]);
      return;
    }
    setOwnerBusy(true);
    try {
      const [admins, superAdmins] = await Promise.all([
        getAdminUsers({ q: needle, role: 'Admin', take: 30 }),
        getAdminUsers({ q: needle, role: 'SuperAdmin', take: 30 }),
      ]);
      const found = [...(admins?.items || []), ...(superAdmins?.items || [])].filter(isOwnerCandidate);
      rememberUsers(found);
      const existing = new Set(editing.ownerUserIds.map((id) => id.toLowerCase()));
      setOwnerCandidates(found.filter((candidate) => !existing.has(userIdOf(candidate).toLowerCase())));
    } catch (e) {
      handleApiError(e, notify, 'Не удалось найти администратора');
    } finally {
      setOwnerBusy(false);
    }
  };

  const addOwner = (candidate) => {
    const id = userIdOf(candidate);
    if (!id || !isOwnerCandidate(candidate)) return;
    setEditing((current) => ({
      ...current,
      ownerUserIds: Array.from(new Set([...current.ownerUserIds, id])),
    }));
    rememberUsers([candidate]);
    setOwnerQuery('');
    setOwnerCandidates([]);
  };

  const removeOwner = (ownerId) => {
    if (editing.ownerUserIds.length <= 1) {
      notify.warn('У группы должен остаться хотя бы один владелец');
      return;
    }
    setEditing((current) => ({
      ...current,
      ownerUserIds: current.ownerUserIds.filter((id) => id.toLowerCase() !== String(ownerId).toLowerCase()),
    }));
  };

  const addMember = async (candidate) => {
    if (!selected) return;
    const id = userIdOf(candidate);
    if (!id) return;
    setMemberBusy(true);
    try {
      await addGroupMember(selected.id, id);
      setMemberQuery('');
      setCandidates([]);
      await loadMembers(selected.id);
      await load();
      notify.success('Пользователь добавлен');
    } catch (e) {
      handleApiError(e, notify, 'Не удалось добавить пользователя');
    } finally {
      setMemberBusy(false);
    }
  };

  const removeMember = async (member) => {
    if (!selected) return;
    const id = userIdOf(member);
    if (!id) return;
    setMemberBusy(true);
    try {
      await removeGroupMember(selected.id, id);
      await loadMembers(selected.id);
      await load();
      notify.success('Пользователь удалён из группы');
    } catch (e) {
      handleApiError(e, notify, 'Не удалось удалить пользователя из группы');
    } finally {
      setMemberBusy(false);
    }
  };

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="text-2xl font-semibold">Группы</h1>
          <div className="mt-1 text-sm text-neutral-500">Название, описание, владельцы и участники — без технических полей.</div>
        </div>
        <Button onClick={() => setCreating(true)}><Plus size={16} /><span className="ml-1">Создать</span></Button>
      </div>

      {err ? <div className="text-red-500">{err}</div> : null}

      {creating ? (
        <Card>
          <div className="grid gap-4 md:grid-cols-[1fr_1.4fr_auto] md:items-end">
            <Field label="Название">
              <Input autoFocus value={form.name} onChange={(e) => setForm((prev) => ({ ...prev, name: e.target.value }))} placeholder="Например: ИСП-31" />
            </Field>
            <Field label="Описание (необязательно)">
              <Input value={form.description} onChange={(e) => setForm((prev) => ({ ...prev, description: e.target.value }))} placeholder="Короткое описание группы" />
            </Field>
            <div className="flex gap-2">
              <Button onClick={create}>Создать</Button>
              <Button variant="outline" onClick={() => { setCreating(false); setForm({ ...emptyForm }); }}>Отмена</Button>
            </div>
          </div>
        </Card>
      ) : null}

      <div className="flex flex-wrap gap-2">
        <Input className="max-w-xl" value={q} onChange={(e) => setQ(e.target.value)} placeholder="Поиск по названию или описанию…" />
        {isSuperAdmin ? (
          <Button variant="outline" onClick={() => setMineOnly((value) => !value)}>{mineOnly ? 'Показать все' : 'Только мои'}</Button>
        ) : null}
      </div>

      <div className="grid gap-6 xl:grid-cols-[minmax(0,1fr)_minmax(360px,0.8fr)]">
        <div className="space-y-3">
          {loading ? <div className="text-sm text-neutral-500">Загрузка…</div> : null}
          {!loading && filtered.length === 0 ? <Card><div className="py-8 text-center text-neutral-500">Групп пока нет.</div></Card> : null}
          {filtered.map((group) => (
            <button key={group.id} type="button" onClick={() => openGroup(group)} className="block w-full text-left">
              <Card className={`transition hover:border-[rgb(var(--accent))] ${String(selectedId) === String(group.id) ? 'border-[rgb(var(--accent))]' : ''}`}>
                <div className="flex items-start justify-between gap-4">
                  <div className="min-w-0">
                    <div className="font-semibold truncate">{group.name}</div>
                    <div className="mt-1 text-sm text-neutral-500 line-clamp-2">{group.description || 'Без описания'}</div>
                  </div>
                  <Badge intent="secondary"><Users size={13} className="mr-1 inline" />{group.membersCount ?? 0}</Badge>
                </div>
              </Card>
            </button>
          ))}
        </div>

        <div>
          {selected ? (
            <Card className="xl:sticky xl:top-4">
              <div className="space-y-5">
                <div className="flex items-start justify-between gap-3">
                  <div>
                    <div className="text-lg font-semibold">{selected.name}</div>
                    <div className="text-xs text-neutral-500">{selected.membersCount ?? memberIds.length} участников</div>
                  </div>
                  <Button variant="ghost" onClick={() => setSelectedId('')} title="Закрыть"><X size={16} /></Button>
                </div>

                <Field label="Название">
                  <Input value={editing.name} onChange={(e) => setEditing((prev) => ({ ...prev, name: e.target.value }))} />
                </Field>
                <Field label="Описание">
                  <Textarea rows={3} value={editing.description} onChange={(e) => setEditing((prev) => ({ ...prev, description: e.target.value }))} placeholder="Необязательно" />
                </Field>

                <div className="border-t border-[rgb(var(--border))] pt-5">
                  <div className="mb-3 flex items-center justify-between gap-2">
                    <div className="font-medium">Владельцы</div>
                    <Badge intent="secondary">{editing.ownerUserIds.length}</Badge>
                  </div>
                  <div className="space-y-2">
                    {editing.ownerUserIds.map((ownerId) => {
                      const owner = usersById.get(String(ownerId).toLowerCase());
                      return (
                        <div key={ownerId} className="flex items-center justify-between gap-3 rounded-xl border border-[rgb(var(--border))] p-2">
                          <div className="min-w-0">
                            <div className="truncate text-sm font-medium">{owner ? userLabel(owner) : ownerId}</div>
                            <div className="truncate text-xs text-neutral-500">{owner?.role || 'Admin'}{owner?.login ? ` · ${owner.login}` : ''}</div>
                          </div>
                          {isSuperAdmin ? <Button variant="ghost" onClick={() => removeOwner(ownerId)} title="Убрать владельца"><X size={15} /></Button> : null}
                        </div>
                      );
                    })}
                    {!editing.ownerUserIds.length ? <div className="text-sm text-neutral-500">Владельцы ещё не назначены.</div> : null}
                  </div>
                  {isSuperAdmin ? (
                    <>
                      <div className="mt-3 flex gap-2">
                        <Input value={ownerQuery} onChange={(e) => setOwnerQuery(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter') searchOwners(); }} placeholder="Найти Admin или SuperAdmin" />
                        <Button variant="outline" onClick={searchOwners} disabled={ownerBusy}><UserPlus size={16} /></Button>
                      </div>
                      {ownerCandidates.length ? (
                        <div className="mt-2 space-y-2">
                          {ownerCandidates.map((candidate) => (
                            <button key={userIdOf(candidate)} type="button" className="flex w-full items-center justify-between gap-3 rounded-xl border border-[rgb(var(--border))] p-2 text-left hover:bg-white/5" onClick={() => addOwner(candidate)}>
                              <span className="min-w-0">
                                <span className="block truncate text-sm font-medium">{userLabel(candidate)}</span>
                                <span className="block truncate text-xs text-neutral-500">{candidate.role}{candidate.login ? ` · ${candidate.login}` : ''}</span>
                              </span>
                              <Plus size={15} />
                            </button>
                          ))}
                        </div>
                      ) : null}
                    </>
                  ) : (
                    <div className="mt-2 text-xs text-neutral-500">Состав владельцев меняет SuperAdmin.</div>
                  )}
                </div>

                <div className="flex gap-2">
                  <Button onClick={save}><Save size={16} /><span className="ml-1">Сохранить</span></Button>
                  <Button variant="outline" className="text-red-600" onClick={remove}><Trash2 size={16} /><span className="ml-1">Удалить</span></Button>
                </div>

                <div className="border-t border-[rgb(var(--border))] pt-5">
                  <div className="mb-3 font-medium">Добавить участника</div>
                  <div className="flex gap-2">
                    <Input value={memberQuery} onChange={(e) => setMemberQuery(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter') searchMembers(); }} placeholder="Имя, логин или почта" />
                    <Button variant="outline" onClick={searchMembers} disabled={memberBusy}><UserPlus size={16} /></Button>
                  </div>
                  {candidates.length ? (
                    <div className="mt-2 space-y-2">
                      {candidates.map((candidate) => (
                        <button key={userIdOf(candidate)} type="button" className="flex w-full items-center justify-between gap-3 rounded-xl border border-[rgb(var(--border))] p-2 text-left hover:bg-white/5" onClick={() => addMember(candidate)}>
                          <span className="min-w-0">
                            <span className="block truncate text-sm font-medium">{userLabel(candidate)}</span>
                            <span className="block truncate text-xs text-neutral-500">{candidate.email || candidate.login || userIdOf(candidate)}</span>
                          </span>
                          <Plus size={15} />
                        </button>
                      ))}
                    </div>
                  ) : null}
                </div>

                <div>
                  <div className="mb-3 font-medium">Участники</div>
                  {memberBusy && !members.length ? <div className="text-sm text-neutral-500">Загрузка…</div> : null}
                  {!memberBusy && !memberIds.length ? <div className="text-sm text-neutral-500">В группе пока никого нет.</div> : null}
                  <div className="space-y-2">
                    {members.map((member) => (
                      <div key={userIdOf(member)} className="flex items-center justify-between gap-3 rounded-xl border border-[rgb(var(--border))] p-2">
                        <div className="min-w-0">
                          <div className="truncate text-sm font-medium">{userLabel(member)}</div>
                          <div className="truncate text-xs text-neutral-500">{member.email || member.login || userIdOf(member)}</div>
                        </div>
                        <Button variant="ghost" onClick={() => removeMember(member)} disabled={memberBusy} title="Убрать из группы"><X size={15} /></Button>
                      </div>
                    ))}
                    {memberIds.filter((id) => !members.some((member) => userIdOf(member).toLowerCase() === id.toLowerCase())).map((id) => (
                      <div key={id} className="flex items-center justify-between gap-3 rounded-xl border border-[rgb(var(--border))] p-2">
                        <div className="truncate text-xs text-neutral-500">{id}</div>
                        <Button variant="ghost" onClick={() => removeMember({ id })} disabled={memberBusy} title="Убрать из группы"><X size={15} /></Button>
                      </div>
                    ))}
                  </div>
                </div>
              </div>
            </Card>
          ) : (
            <Card><div className="py-10 text-center text-sm text-neutral-500">Выберите группу, чтобы изменить её или добавить людей.</div></Card>
          )}
        </div>
      </div>
    </div>
  );
}
