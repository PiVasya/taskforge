
import React, { useEffect, useMemo, useState } from 'react';

import { Field, Input, Textarea, Button, Card, Badge } from '../components/ui';
import { getCourse, updateCourse, deleteCourse } from '../api/courses';
import { getGroups } from '../api/groups';
import { searchUsersOnce } from '../api/admin';
import { getAdminUsers } from '../api/adminUsers';
import { useNavigate, useParams } from 'react-router-dom';
import { Save, Trash2, ArrowLeft, Layers, UserPlus, X } from 'lucide-react';

import { useNotify } from '../components/notify/NotifyProvider';
import { handleApiError } from '../utils/handleApiError';
import { getApiErrorMessage } from '../api/http';
import { useEditorMode } from '../contexts/EditorModeContext';

const GUID_RE = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[1-5][0-9a-fA-F]{3}-[89abAB][0-9a-fA-F]{3}-[0-9a-fA-F]{12}$/;


export default function CourseEditPage() {
  const { courseId } = useParams();
  const nav = useNavigate();
  const notify = useNotify();
  const { isAdmin } = useEditorMode();

  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [isPublic, setIsPublic] = useState(false);
  const [visibleGroupIds, setVisibleGroupIds] = useState([]);
  const [ownerIds, setOwnerIds] = useState([]);
  const [groups, setGroups] = useState([]);

  const [ownerQuery, setOwnerQuery] = useState('');
  const [ownerSearchBusy, setOwnerSearchBusy] = useState(false);
  const [ownerCandidates, setOwnerCandidates] = useState([]);
  const [ownerProfiles, setOwnerProfiles] = useState({});
  const [manualOwnerId, setManualOwnerId] = useState('');

  const [err, setErr] = useState('');
  const [busy, setBusy] = useState(false);
  const [loading, setLoading] = useState(true);

  const ownerIdSet = useMemo(() => new Set(ownerIds.map((x) => String(x).toLowerCase())), [ownerIds]);

  const rememberOwners = (users) => {
    const list = Array.isArray(users) ? users : [];
    if (!list.length) return;
    setOwnerProfiles((prev) => {
      const next = { ...prev };
      list.forEach((u) => {
        const id = String(u?.id || u?.userId || '').toLowerCase();
        if (id) next[id] = u;
      });
      return next;
    });
  };

  const ownerLabel = (id) => {
    const u = ownerProfiles[String(id).toLowerCase()];
    return u?.displayName || u?.fullName || [u?.firstName, u?.lastName].filter(Boolean).join(' ').trim() || u?.login || u?.email || 'Пользователь';
  };

  const ownerEmail = (id) => ownerProfiles[String(id).toLowerCase()]?.email || '';

  useEffect(() => {
    (async () => {
      try {
        setErr('');
        setLoading(true);

        const [c, g] = await Promise.all([
          getCourse(courseId),
          getGroups().catch(() => []),
        ]);

        if (c?.canEdit === false) {
          notify.warn('Редактирование курса недоступно');
          nav(`/course/${courseId}`, { replace: true });
          return;
        }

        setTitle(c.title || '');
        setDescription(c.description || '');
        setIsPublic(!!c.isPublic);
        setVisibleGroupIds(Array.isArray(c.visibleGroupIds) ? c.visibleGroupIds : []);
        const loadedOwnerIds = Array.isArray(c.ownerIds) && c.ownerIds.length ? c.ownerIds : (c.ownerId ? [c.ownerId] : []);
        setOwnerIds(loadedOwnerIds);

        if (isAdmin && loadedOwnerIds.length) {
          try {
            const result = await getAdminUsers({ take: 500 });
            const users = result?.items || [];
            rememberOwners((Array.isArray(users) ? users : []).filter((u) => loadedOwnerIds.map((x) => String(x).toLowerCase()).includes(String(u?.id || u?.userId || '').toLowerCase())));
          } catch {}
        }

        setGroups(Array.isArray(g) ? g : []);
      } catch (e) {
        handleApiError(e, notify, 'Не удалось загрузить курс');
        setErr(e?.userMessage || e?.message || 'Не удалось загрузить курс');
      } finally {
        setLoading(false);
      }
    })();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [courseId, nav, isAdmin]);

  const toggleGroup = (id) => {
    const sid = String(id);
    setVisibleGroupIds((prev) => {
      const s = new Set((prev || []).map(String));
      if (s.has(sid)) s.delete(sid);
      else s.add(sid);
      return Array.from(s);
    });
  };

  const removeOwner = (id) => {
    const sid = String(id).toLowerCase();
    setOwnerIds((prev) => (prev || []).filter((x) => String(x).toLowerCase() !== sid));
  };

  const addOwnerId = (id, user = null) => {
    const sid = String(id).trim();
    if (!GUID_RE.test(sid)) {
      notify.warn('Неверный формат GUID');
      return;
    }
    if (user) rememberOwners([{ ...user, id: user.id || user.userId || sid }]);
    if (ownerIdSet.has(sid.toLowerCase())) return;
    setOwnerIds((prev) => [...(prev || []), sid]);
  };

  const doOwnerSearch = async () => {
    if (!isAdmin) return;
    const q = ownerQuery.trim();
    if (q.length < 2) {
      setOwnerCandidates([]);
      return;
    }
    setOwnerSearchBusy(true);
    try {
      let rows = [];
      try {
        const result = await getAdminUsers({ q, take: 10 });
        rows = result?.items || [];
      } catch {
        const list = await searchUsersOnce(q, 10);
        rows = Array.isArray(list) ? list : [];
      }
      setOwnerCandidates(rows);
      rememberOwners(rows);
    } catch (e) {
      setOwnerCandidates([]);
    } finally {
      setOwnerSearchBusy(false);
    }
  };

  const save = async () => {
    setBusy(true);
    setErr('');
    try {
      const payload = {
        title,
        description,
        isPublic,
        visibleGroupIds: isPublic ? [] : (visibleGroupIds || []),
        ownerIds: ownerIds || [],
      };

      await updateCourse(courseId, payload);
      notify.success('Курс обновлён');
      nav(`/course/${courseId}`);
    } catch (e) {
      if (e?.response?.status === 403) {
        notify.error(getApiErrorMessage(e, 'Недостаточно прав')); 
        nav(`/course/${courseId}`, { replace: true });
        return;
      }
      handleApiError(e, notify, 'Не удалось сохранить');
      setErr(e?.userMessage || e?.message || 'Не удалось сохранить');
    } finally {
      setBusy(false);
    }
  };

  const remove = async () => {
    if (!window.confirm('Удалить курс?')) return;
    try {
      await deleteCourse(courseId);
      notify.success('Курс удалён');
      nav('/courses');
    } catch (e) {
      if (e?.response?.status === 403) {
        notify.error(getApiErrorMessage(e, 'Недостаточно прав')); 
        nav(`/course/${courseId}`, { replace: true });
        return;
      }
      handleApiError(e, notify, 'Не удалось удалить');
      setErr(e?.userMessage || e?.message || 'Не удалось удалить');
    }
  };

  return (
    <>
      <div className="flex items-center justify-between mb-6">
        <div className="flex items-center gap-2">
          <Layers size={20} />
          <h1 className="text-2xl font-semibold">Редактирование курса</h1>
        </div>
        <Button variant="ghost" className="inline-flex items-center gap-2" onClick={() => nav(`/course/${courseId}`)}>
          <ArrowLeft className="inline" size={16} /> к заданиям
        </Button>
      </div>

      {err && <div className="text-red-500 mb-4">{err}</div>}
      {loading && <div className="text-neutral-500 mb-4">Загрузка…</div>}

      <div className="grid lg:grid-cols-3 gap-6">
        <div className="lg:col-span-2 space-y-6">
          <Card>
            <div className="grid sm:grid-cols-2 gap-4">
              <Field label="Название">
                <Input value={title} onChange={(e) => setTitle(e.target.value)} placeholder="Например: Основы C++" />
              </Field>

              <div className="sm:col-span-2">
                <Field label="Описание">
                  <Textarea
                    rows={8}
                    value={description}
                    onChange={(e) => setDescription(e.target.value)}
                    placeholder="Кратко опишите, чему посвящён курс…"
                  />
                </Field>
              </div>

              <div className="sm:col-span-2">
                <label className="flex items-center gap-3 select-none">
                  <input
                    type="checkbox"
                    className="h-4 w-4 rounded border-neutral-300"
                    checked={isPublic}
                    onChange={(e) => setIsPublic(e.target.checked)}
                  />
                  <span className="text-sm">
                    Публичный курс <span className="text-neutral-500">(виден всем)</span>
                  </span>
                </label>
              </div>

              {!isPublic && (
                <div className="sm:col-span-2">
                  <div className="text-sm font-medium mb-2">Группы видимости</div>
                  <div className="text-xs text-neutral-500 mb-3">
                    Если группы не выбраны — курс виден только Admin и Editor.
                  </div>

                  {groups.length === 0 ? (
                    <div className="text-sm text-neutral-500">Группы не загружены.</div>
                  ) : (
                    <div className="grid sm:grid-cols-2 gap-2">
                      {groups.map((g) => (
                        <label key={g.id} className="flex items-center gap-2 p-2 rounded-xl border border-neutral-200 dark:border-neutral-700">
                          <input
                            type="checkbox"
                            className="h-4 w-4"
                            checked={visibleGroupIds.map(String).includes(String(g.id))}
                            onChange={() => toggleGroup(g.id)}
                          />
                          <div className="min-w-0">
                            <div className="text-sm font-medium truncate">{g.name}</div>
                            <div className="text-xs text-neutral-500 truncate">{g.code}</div>
                          </div>
                          {g.isActive === false ? <Badge intent="secondary">Неактивна</Badge> : null}
                        </label>
                      ))}
                    </div>
                  )}
                </div>
              )}
            </div>
          </Card>

          <Card>
            <div className="flex items-center justify-between gap-3 mb-2">
              <div>
                <div className="text-sm font-medium">Владельцы курса (owners)</div>
                <div className="text-xs text-neutral-500">Editor может редактировать курс только если он в owners.</div>
              </div>
            </div>

            <div className="flex flex-wrap gap-2 mb-3">
              {(ownerIds || []).length === 0 ? (
                <span className="text-sm text-neutral-500">Нет владельцев</span>
              ) : (
                ownerIds.map((id) => (
                  <span key={id} className="inline-flex items-center gap-2 px-3 py-1 rounded-full border border-neutral-200 dark:border-neutral-700 text-sm">
                    <span className="font-medium">{ownerLabel(id)}</span>
                    {ownerEmail(id) ? <span className="text-xs text-neutral-500">{ownerEmail(id)}</span> : null}
                    <button className="opacity-70 hover:opacity-100" onClick={() => removeOwner(id)} title="Убрать владельца">
                      <X size={14} />
                    </button>
                  </span>
                ))
              )}
            </div>

            {isAdmin ? (
              <div className="space-y-3">
                <div className="flex gap-2">
                  <Input
                    value={ownerQuery}
                    onChange={(e) => setOwnerQuery(e.target.value)}
                    placeholder="Поиск пользователя (email/имя)…"
                  />
                  <Button variant="outline" onClick={doOwnerSearch} disabled={ownerSearchBusy} title="Найти">
                    <UserPlus size={16} />
                    <span className="ml-1">Найти</span>
                  </Button>
                </div>

                {ownerCandidates.length > 0 && (
                  <div className="border border-neutral-200 dark:border-neutral-700 rounded-xl overflow-hidden">
                    {ownerCandidates.map((u) => (
                      <button
                        key={u.id}
                        type="button"
                        className="w-full text-left px-4 py-2 hover:bg-neutral-50 dark:hover:bg-neutral-800 flex items-center justify-between gap-3"
                        onClick={() => addOwnerId(u.id || u.userId, u)}
                      >
                        <div className="min-w-0">
                          <div className="text-sm font-medium truncate">{u.displayName || u.fullName || u.login || u.email || 'Пользователь'}</div>
                          <div className="text-xs text-neutral-500 truncate">{u.email || 'email не указан'}</div>
                        </div>
                        {ownerIdSet.has(String(u.id).toLowerCase()) ? <Badge intent="secondary">уже</Badge> : <Badge intent="success">Добавить</Badge>}
                      </button>
                    ))}
                  </div>
                )}
              </div>
            ) : (
              <div className="space-y-2">
                <div className="text-xs text-neutral-500">
                  Добавление владельцев доступно через ввод GUID пользователя.
                </div>
                <div className="flex gap-2">
                  <Input
                    value={manualOwnerId}
                    onChange={(e) => setManualOwnerId(e.target.value)}
                    placeholder="GUID пользователя"
                  />
                  <Button
                    variant="outline"
                    onClick={() => {
                      addOwnerId(manualOwnerId);
                      setManualOwnerId('');
                    }}
                  >
                    <UserPlus size={16} />
                    <span className="ml-1">Добавить</span>
                  </Button>
                </div>
              </div>
            )}
          </Card>
        </div>

        <div className="space-y-4">
          <Card>
            <div className="flex gap-2">
              <Button onClick={save} disabled={busy} className="flex-1">
                <Save size={16} /> {busy ? 'Сохраняю…' : 'Сохранить'}
              </Button>
              <Button
                variant="outline"
                onClick={remove}
                className="flex-1 text-red-600 border-red-300 hover:bg-red-50 dark:hover:bg-red-900/20"
              >
                <Trash2 size={16} /> Удалить
              </Button>
            </div>
          </Card>
        </div>
      </div>
    </>
  );
}
