import React, { useEffect, useMemo, useState } from 'react';
import { Card, Button, Field, Input, Textarea, Badge } from '../../components/ui';
import { createGroup, deleteGroup, getAdminGroups, updateGroup } from '../../api/groups';
import { useNotify } from '../../components/notify/NotifyProvider';
import { handleApiError } from '../../utils/handleApiError';
import { ContextMenu, ContextMenuItem, ContextMenuLabel, ContextMenuSeparator } from '../../components/ui/ContextMenu';
import useSaveShortcut from '../../hooks/useSaveShortcut';
import { Copy, Plus, Save, Trash2 } from 'lucide-react';

const empty = {
  name: '',
  code: '',
  description: '',
  isActive: true,
  color: '',
  icon: '',
  externalId: '',
  notes: '',
  tagsJson: '',
};

export default function AdminGroupsPage() {
  const notify = useNotify();
  const [items, setItems] = useState([]);
  const [loading, setLoading] = useState(true);
  const [err, setErr] = useState('');

  const [q, setQ] = useState('');
  const [creating, setCreating] = useState(false);
  const [form, setForm] = useState({ ...empty });
  const [activeGroupId, setActiveGroupId] = useState('');
  const [contextMenu, setContextMenu] = useState({ open: false, x: 0, y: 0, group: null });

  const load = async () => {
    try {
      setLoading(true);
      setErr('');
      const list = await getAdminGroups();
      setItems(Array.isArray(list) ? list : []);
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось загрузить группы');
      setErr(parsed?.userMessage || 'Не удалось загрузить группы');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    load();
    
  }, []);

  const closeContextMenu = () => setContextMenu((current) => current.open ? { ...current, open: false } : current);
  const openContextMenu = (event, group) => {
    if (event.target.closest('input, textarea, select, [contenteditable="true"]')) return;
    event.preventDefault();
    event.stopPropagation();
    setActiveGroupId(String(group?.id || ''));
    setContextMenu({ open: true, x: event.clientX, y: event.clientY, group });
  };
  const copyValue = async (value, label) => {
    if (!value) return;
    try { await navigator.clipboard.writeText(String(value)); notify.success(`${label} скопирован`); }
    catch { notify.warn('Не удалось скопировать'); }
  };

  const filtered = useMemo(() => {
    const qq = q.toLowerCase();
    return (items || []).filter((g) =>
      ((g.name || '') + ' ' + (g.code || '') + ' ' + (g.description || '')).toLowerCase().includes(qq)
    );
  }, [items, q]);

  const startCreate = () => {
    setCreating(true);
    setActiveGroupId('__new__');
    setForm({ ...empty });
  };

  const cancelCreate = () => {
    setCreating(false);
    setActiveGroupId('');
    setForm({ ...empty });
  };

  const onChange = (k, v) => setForm((p) => ({ ...p, [k]: v }));

  const doCreate = async () => {
    try {
      const payload = {
        name: (form.name || '').trim(),
        code: (form.code || '').trim(),
        description: (form.description || '').trim() || null,
        isActive: !!form.isActive,
        color: (form.color || '').trim() || null,
        icon: (form.icon || '').trim() || null,
        externalId: (form.externalId || '').trim() || null,
        notes: (form.notes || '').trim() || null,
        tagsJson: (form.tagsJson || '').trim() || null,
      };
      const created = await createGroup(payload);
      notify.success('Группа создана');
      setErr('');
      setCreating(false);
      setActiveGroupId('');
      setForm({ ...empty });
      await load();
      
      if (created?.id) {
        const el = document.getElementById('group-' + created.id);
        if (el) el.scrollIntoView({ behavior: 'smooth', block: 'center' });
      }
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось создать группу');
      setErr(parsed?.userMessage || 'Не удалось создать группу');
    }
  };

  const doUpdate = async (g) => {
    try {
      const payload = {
        name: (g.name || '').trim(),
        code: (g.code || '').trim(),
        description: (g.description || '').trim() || null,
        isActive: !!g.isActive,
        color: (g.color || '').trim() || null,
        icon: (g.icon || '').trim() || null,
        externalId: (g.externalId || '').trim() || null,
        notes: (g.notes || '').trim() || null,
        tagsJson: (g.tagsJson || '').trim() || null,
      };
      await updateGroup(g.id, payload);
      notify.success('Сохранено');
      setErr('');
      await load();
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось сохранить');
      setErr(parsed?.userMessage || 'Не удалось сохранить');
    }
  };

  const doDelete = async (g) => {
    const ok = await notify.confirm({
      title: 'Удалить группу?',
      message: `Группа: ${g.name} (${g.code}). Действие необратимо.`,
      okText: 'Удалить',
      cancelText: 'Отмена',
    });
    if (!ok) return;

    try {
      await deleteGroup(g.id);
      notify.success('Удалено');
      setErr('');
      await load();
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось удалить');
      setErr(parsed?.userMessage || 'Не удалось удалить');
    }
  };

  const updateLocal = (id, patch) => {
    setItems((prev) => prev.map((x) => (x.id === id ? { ...x, ...patch } : x)));
  };

  useSaveShortcut(() => {
    if (creating && activeGroupId === '__new__') return doCreate();
    const current = items.find((item) => String(item.id) === String(activeGroupId));
    if (current) return doUpdate(current);
    return undefined;
  }, { enabled: (creating && activeGroupId === '__new__') || Boolean(activeGroupId) });

  return (
    <>
      <div className="flex items-center justify-between mb-6">
        <h1 className="text-2xl font-semibold">Группы пользователей</h1>
        {!creating ? (
          <Button onClick={startCreate} title="Создать группу">
            <Plus size={16} /> <span className="ml-1">Создать</span>
          </Button>
        ) : (
          <div className="flex gap-2">
            <Button onClick={doCreate} title="Сохранить (Ctrl+S)">
              <Save size={16} /> <span className="ml-1">Сохранить</span>
            </Button>
            <Button variant="outline" onClick={cancelCreate}>
              Отмена
            </Button>
          </div>
        )}
      </div>

      {err && <div className="text-red-500 mb-4">{err}</div>}
      {loading && <div className="text-neutral-500 mb-4">Загрузка…</div>}

      <Card className="mb-6">
        <Input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Поиск по названию / коду…" />
      </Card>

      {creating && (
        <Card className="mb-6" onFocusCapture={() => setActiveGroupId('__new__')} onPointerDownCapture={() => setActiveGroupId('__new__')}>
          <div className="grid md:grid-cols-2 gap-4">
            <Field label="Название">
              <Input value={form.name} onChange={(e) => onChange('name', e.target.value)} />
            </Field>
            <Field label="Код (уникальный)">
              <Input value={form.code} onChange={(e) => onChange('code', e.target.value)} />
            </Field>
            <div className="md:col-span-2">
              <Field label="Описание">
                <Textarea rows={3} value={form.description} onChange={(e) => onChange('description', e.target.value)} />
              </Field>
            </div>
            <Field label="Цвет">
              <Input value={form.color} onChange={(e) => onChange('color', e.target.value)} placeholder="#22c55e" />
            </Field>
            <Field label="Иконка">
              <Input value={form.icon} onChange={(e) => onChange('icon', e.target.value)} placeholder="users" />
            </Field>
            <Field label="ExternalId">
              <Input value={form.externalId} onChange={(e) => onChange('externalId', e.target.value)} />
            </Field>
            <Field label="Активна">
              <label className="flex items-center gap-2 mt-2 select-none">
                <input type="checkbox" checked={form.isActive} onChange={(e) => onChange('isActive', e.target.checked)} />
                <span className="text-sm">Да</span>
              </label>
            </Field>
            <div className="md:col-span-2">
              <Field label="Заметки">
                <Textarea rows={2} value={form.notes} onChange={(e) => onChange('notes', e.target.value)} />
              </Field>
            </div>
            <div className="md:col-span-2">
              <Field label="TagsJson (на будущее)">
                <Textarea rows={2} value={form.tagsJson} onChange={(e) => onChange('tagsJson', e.target.value)} placeholder='{"tags":["...", "..."]}' />
              </Field>
            </div>
          </div>
        </Card>
      )}

      <div className="space-y-4">
        {filtered.map((g) => (
          <Card key={g.id} id={'group-' + g.id} onContextMenu={(event) => openContextMenu(event, g)} onFocusCapture={() => setActiveGroupId(String(g.id))} onPointerDownCapture={() => setActiveGroupId(String(g.id))}>
            <div className="flex flex-col lg:flex-row lg:items-start lg:justify-between gap-4">
              <div className="flex-1 grid md:grid-cols-2 gap-4">
                <Field label="Название">
                  <Input value={g.name || ''} onChange={(e) => updateLocal(g.id, { name: e.target.value })} />
                </Field>
                <Field label="Код">
                  <Input value={g.code || ''} onChange={(e) => updateLocal(g.id, { code: e.target.value })} />
                </Field>
                <div className="md:col-span-2">
                  <Field label="Описание">
                    <Textarea rows={2} value={g.description || ''} onChange={(e) => updateLocal(g.id, { description: e.target.value })} />
                  </Field>
                </div>
                <Field label="Активна">
                  <label className="flex items-center gap-2 mt-2 select-none">
                    <input type="checkbox" checked={!!g.isActive} onChange={(e) => updateLocal(g.id, { isActive: e.target.checked })} />
                    <span className="text-sm">Да</span>
                  </label>
                </Field>
                <Field label="Участников">
                  <div className="mt-2">
                    <Badge intent="secondary">{g.membersCount ?? 0}</Badge>
                  </div>
                </Field>
                <Field label="Цвет">
                  <Input value={g.color || ''} onChange={(e) => updateLocal(g.id, { color: e.target.value })} />
                </Field>
                <Field label="Иконка">
                  <Input value={g.icon || ''} onChange={(e) => updateLocal(g.id, { icon: e.target.value })} />
                </Field>
                <Field label="ExternalId">
                  <Input value={g.externalId || ''} onChange={(e) => updateLocal(g.id, { externalId: e.target.value })} />
                </Field>
                <div className="md:col-span-2">
                  <Field label="Заметки">
                    <Textarea rows={2} value={g.notes || ''} onChange={(e) => updateLocal(g.id, { notes: e.target.value })} />
                  </Field>
                </div>
                <div className="md:col-span-2">
                  <Field label="TagsJson">
                    <Textarea rows={2} value={g.tagsJson || ''} onChange={(e) => updateLocal(g.id, { tagsJson: e.target.value })} />
                  </Field>
                </div>
              </div>

              <div className="shrink-0 flex lg:flex-col gap-2">
                <Button onClick={() => doUpdate(g)} title="Сохранить (Ctrl+S)">
                  <Save size={16} /> <span className="ml-1">Сохранить</span>
                </Button>
                <Button
                  variant="outline"
                  className="text-red-600 border-red-300 hover:bg-red-50 dark:hover:bg-red-900/20"
                  onClick={() => doDelete(g)}
                  title="Удалить"
                >
                  <Trash2 size={16} /> <span className="ml-1">Удалить</span>
                </Button>
              </div>
            </div>
          </Card>
        ))}
      </div>

      {!loading && filtered.length === 0 && (
        <div className="card-muted p-8 mt-6 text-center text-neutral-500">Пока групп нет.</div>
      )}

      <ContextMenu open={contextMenu.open} x={contextMenu.x} y={contextMenu.y} onClose={closeContextMenu} ariaLabel="Действия группы">
        {contextMenu.group ? (
          <>
            <ContextMenuLabel>Группа</ContextMenuLabel>
            <ContextMenuItem icon={Save} shortcut="Ctrl+S" onClick={() => { const group = contextMenu.group; closeContextMenu(); void doUpdate(group); }}>Сохранить изменения</ContextMenuItem>
            <ContextMenuSeparator />
            <ContextMenuItem icon={Copy} disabled={!contextMenu.group.code} onClick={() => { const group = contextMenu.group; closeContextMenu(); void copyValue(group.code, 'Код'); }}>Скопировать код</ContextMenuItem>
            <ContextMenuItem icon={Copy} onClick={() => { const group = contextMenu.group; closeContextMenu(); void copyValue(group.id, 'ID'); }}>Скопировать ID</ContextMenuItem>
            <ContextMenuSeparator />
            <ContextMenuItem icon={Trash2} danger onClick={() => { const group = contextMenu.group; closeContextMenu(); void doDelete(group); }}>Удалить группу</ContextMenuItem>
          </>
        ) : null}
      </ContextMenu>
    </>
  );
}
