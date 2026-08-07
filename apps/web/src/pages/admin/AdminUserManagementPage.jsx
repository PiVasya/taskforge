import React, { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { Badge, Button, Card, Field, Input, Select, Textarea } from '../../components/ui';
import AppErrorPanel from '../../components/AppErrorPanel';
import { getAdminUser, unlinkAdminTelegram, updateAdminUser } from '../../api/adminUsers';
import {
  blockAccount,
  createAccountOperation,
  getAccountOperations,
  unblockAccount,
} from '../../api/accountIntelligence';
import {
  getAdminMinecraftUserRating,
  restoreAdminMinecraftUserRating,
  unlinkAdminMinecraftLink,
  unlinkAllAdminMinecraftLinks,
} from '../../api/adminMinecraftLinks';
import { deleteUserSolutions, getAdminUserGroupIds, getUserImageSolutions, getUserSolutions, searchUsersOnce } from '../../api/admin';
import { assignFeatureRole, getFeatureRoles, removeFeatureRole } from '../../api/featureRoles';
import { addGroupMember, getAdminGroups, removeGroupMember } from '../../api/groups';
import { getUserMathAttempts } from '../../api/mathTaskAttempts';
import { getUserTaskTestAttempts } from '../../api/taskTestAttempts';
import { handleApiError } from '../../utils/handleApiError';
import { useNotify } from '../../components/notify/NotifyProvider';
import { useAuth } from '../../auth/AuthContext';
import {
  ArrowLeft,
  Ban,
  Bot,
  ExternalLink,
  History,
  Link2,
  Merge,
  Pickaxe,
  RefreshCcw,
  Save,
  Shield,
  ShieldCheck,
  Trash2,
  UserCog,
} from 'lucide-react';

const baseRoles = ['User', 'Editor', 'Admin'];
const activeOperationStatuses = new Set(['queued', 'starting', 'running']);

const formatDate = (value) => {
  if (!value) return '—';
  try { return new Date(value).toLocaleString(); } catch { return '—'; }
};
const userTitle = (user) => user?.fullName || user?.displayName || user?.login || user?.email || 'Пользователь';
const numeric = (value) => Number.isFinite(Number(value)) ? Number(value) : 0;
const telegramHandle = (value) => {
  const raw = String(value || '').trim();
  if (!raw) return null;
  return raw.startsWith('@') ? raw : `@${raw}`;
};
const operationLabel = (type) => ({ block: 'Блокировка', unblock: 'Разблокировка', merge: 'Объединение', delete: 'Удаление' }[type] || type || 'Операция');
const operationIntent = (status) => status === 'completed' ? 'success' : status === 'failed' ? 'danger' : status === 'cancelled' ? 'outline' : 'secondary';

function MetricCard({ label, value, hint }) {
  return <Card><div className="text-sm opacity-70">{label}</div><div className="mt-2 text-3xl font-semibold">{value}</div>{hint ? <div className="mt-2 text-xs text-neutral-500">{hint}</div> : null}</Card>;
}

function ActivityList({ title, rows, emptyText }) {
  return (
    <Card>
      <div className="mb-3 font-semibold">{title}</div>
      <div className="space-y-2">
        {(rows || []).length ? rows.map((row, index) => (
          <div key={row.id || row.attemptId || `${title}-${index}`} className="rounded-2xl border border-[rgb(var(--border))] p-3">
            <div className="flex flex-col gap-2 sm:flex-row sm:items-start sm:justify-between">
              <div className="min-w-0"><div className="truncate font-medium">{row.assignmentTitle || row.title || row.assignmentName || row.assignmentId || 'Без названия'}</div><div className="mt-1 break-all text-xs text-neutral-500">{row.assignmentId || row.id || row.attemptId || ''}</div></div>
              <Badge intent={(row.status === 'Accepted' || row.passed === true || row.isPassed === true) ? 'success' : 'outline'}>{row.status || (row.passed === true || row.isPassed === true ? 'Пройдено' : 'Не пройдено')}</Badge>
            </div>
            <div className="mt-2 text-xs text-neutral-500">{formatDate(row.createdAt || row.submittedAt || row.finishedAt || row.startedAt)}</div>
          </div>
        )) : <div className="text-sm text-neutral-500">{emptyText}</div>}
      </div>
    </Card>
  );
}

export default function AdminUserManagementPage() {
  const { userId } = useParams();
  const navigate = useNavigate();
  const notify = useNotify();
  const auth = useAuth();

  const [loading, setLoading] = useState(true);
  const [pageError, setPageError] = useState(null);
  const [user, setUser] = useState(null);
  const [form, setForm] = useState(null);
  const [rating, setRating] = useState(null);
  const [groups, setGroups] = useState([]);
  const [groupIds, setGroupIds] = useState(new Set());
  const [featureRoles, setFeatureRoles] = useState([]);
  const [codeSolutions, setCodeSolutions] = useState([]);
  const [imageSolutions, setImageSolutions] = useState([]);
  const [testAttempts, setTestAttempts] = useState([]);
  const [mathAttempts, setMathAttempts] = useState([]);
  const [minecraftRating, setMinecraftRating] = useState(null);
  const [operations, setOperations] = useState([]);
  const [mcRestoreAmount, setMcRestoreAmount] = useState('100');
  const [mcRestoreReason, setMcRestoreReason] = useState('');
  const [blockReason, setBlockReason] = useState('Нарушение правил платформы');
  const [blockNote, setBlockNote] = useState('');
  const [deleteReason, setDeleteReason] = useState('Удаление администратором');
  const [deleteConfirmation, setDeleteConfirmation] = useState('');
  const [saving, setSaving] = useState(false);
  const [mcRestoring, setMcRestoring] = useState(false);
  const [actionBusy, setActionBusy] = useState('');

  const roleSet = useMemo(() => new Set(form?.featureRoles || form?.roles || []), [form]);
  const isSelf = String(auth?.user?.id || '').toLowerCase() === String(userId || '').toLowerCase();
  const isActive = user?.accountStatus === 'active';
  const activeOperation = operations.find((item) => activeOperationStatuses.has(item.status));
  const activeMinecraftLinks = Array.isArray(minecraftRating?.activeLinks) ? minecraftRating.activeLinks : [];

  const load = async () => {
    try {
      setLoading(true);
      const [userDto, ratingRows, allGroups, userGroups, allFeatureRoles, codeRows, imageRows, testRows, mathRows, mcRatingDto, operationRows] = await Promise.all([
        getAdminUser(userId),
        searchUsersOnce(userId, 1).catch(() => []),
        getAdminGroups().catch(() => []),
        getAdminUserGroupIds(userId).catch(() => []),
        getFeatureRoles().catch(() => []),
        getUserSolutions(userId, { take: 10 }).catch(() => []),
        getUserImageSolutions(userId, { take: 10 }).catch(() => []),
        getUserTaskTestAttempts(userId, { take: 10 }).catch(() => []),
        getUserMathAttempts(userId, { take: 10 }).catch(() => []),
        getAdminMinecraftUserRating(userId).catch(() => null),
        getAccountOperations({ userId, take: 20 }).catch(() => []),
      ]);

      const ratingDto = Array.isArray(ratingRows) ? ratingRows.find((x) => String(x.userId || x.id).toLowerCase() === String(userId).toLowerCase()) : null;
      setUser(userDto);
      setForm({
        login: userDto?.login || '', email: userDto?.email || '', firstName: userDto?.firstName || '', lastName: userDto?.lastName || '',
        phoneNumber: userDto?.phoneNumber || '', profilePictureUrl: userDto?.profilePictureUrl || '', role: userDto?.role || 'User', accountType: userDto?.accountType || 'human',
        roles: userDto?.roles || [], featureRoles: userDto?.featureRoles || [],
      });
      setRating(ratingDto || null);
      setGroups(Array.isArray(allGroups) ? allGroups : []);
      setGroupIds(new Set((Array.isArray(userGroups) ? userGroups : []).map((x) => String(x).toLowerCase())));
      setFeatureRoles(Array.isArray(allFeatureRoles) ? allFeatureRoles : []);
      setCodeSolutions(Array.isArray(codeRows) ? codeRows : []);
      setImageSolutions(Array.isArray(imageRows) ? imageRows : []);
      setTestAttempts(Array.isArray(testRows) ? testRows : []);
      setMathAttempts(Array.isArray(mathRows) ? mathRows : []);
      setMinecraftRating(mcRatingDto || null);
      setOperations(Array.isArray(operationRows) ? operationRows : []);
      setPageError(null);
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось загрузить профиль пользователя');
      setPageError(parsed);
    } finally { setLoading(false); }
  };

  useEffect(() => { if (userId) load(); }, [userId]);
  const updateForm = (patch) => setForm((prev) => ({ ...(prev || {}), ...patch }));

  const runAction = async (key, action, successText, errorText, refresh = true) => {
    try {
      setActionBusy(key);
      const result = await action();
      notify.success(successText);
      setPageError(null);
      if (refresh) await load();
      return result;
    } catch (e) {
      const parsed = handleApiError(e, notify, errorText);
      setPageError(parsed);
      return null;
    } finally { setActionBusy(''); }
  };

  const saveUser = async () => {
    if (!isActive) return;
    try {
      setSaving(true);
      await updateAdminUser(userId, {
        login: form.login, email: form.email, firstName: form.firstName, lastName: form.lastName,
        phoneNumber: form.phoneNumber || null, profilePictureUrl: form.profilePictureUrl || null, role: form.role, accountType: form.accountType || 'human',
      });
      notify.success('Пользователь сохранён');
      await load();
    } catch (e) { setPageError(handleApiError(e, notify, 'Не удалось сохранить пользователя')); }
    finally { setSaving(false); }
  };

  const toggleGroup = async (group) => {
    const id = String(group.id).toLowerCase();
    const enabled = groupIds.has(id);
    await runAction(`group-${id}`, () => enabled ? removeGroupMember(group.id, userId) : addGroupMember(group.id, userId), enabled ? 'Пользователь удалён из группы' : 'Пользователь добавлен в группу', 'Не удалось изменить группу пользователя');
  };

  const toggleFeatureRole = async (role) => {
    const code = role.code || role.Code || role.title || role.name;
    if (!code) return;
    const enabled = roleSet.has(code);
    await runAction(`role-${code}`, () => enabled ? removeFeatureRole(userId, code) : assignFeatureRole(userId, code), enabled ? 'Роль снята' : 'Роль выдана', 'Не удалось изменить дополнительную роль');
  };

  const restoreMinecraftRating = async () => {
    const amount = Math.floor(Number(mcRestoreAmount || 0));
    if (!Number.isFinite(amount) || amount <= 0) return notify.error('Укажи сумму восстановления больше 0');
    try {
      setMcRestoring(true);
      await restoreAdminMinecraftUserRating(userId, { amount, reason: mcRestoreReason || null });
      notify.success('Minecraft-баланс восстановлен');
      setMcRestoreReason('');
      setMinecraftRating(await getAdminMinecraftUserRating(userId).catch(() => null));
    } catch (e) { setPageError(handleApiError(e, notify, 'Не удалось восстановить Minecraft-баланс')); }
    finally { setMcRestoring(false); }
  };

  const removeTelegram = async () => {
    const ok = await notify.confirm({ title: 'Отвязать Telegram?', message: 'Telegram перестанет использоваться для поддержки и восстановления пароля. История обращений не удаляется.', okText: 'Отвязать', cancelText: 'Отмена' });
    if (ok) await runAction('telegram-unlink', () => unlinkAdminTelegram(userId), 'Telegram отвязан', 'Не удалось отвязать Telegram');
  };

  const removeMinecraftLink = async (link) => {
    const ok = await notify.confirm({ title: 'Отвязать Minecraft-профиль?', message: `${link.nick || link.uuid} будет отвязан. Общий Minecraft-баланс и история операций сохранятся.`, okText: 'Отвязать', cancelText: 'Отмена' });
    if (ok) await runAction(`mc-${link.id}`, () => unlinkAdminMinecraftLink(link.id), 'Minecraft-профиль отвязан', 'Не удалось отвязать Minecraft-профиль');
  };

  const removeAllMinecraftLinks = async () => {
    const ok = await notify.confirm({ title: 'Отвязать все Minecraft-профили?', message: `Будут отключены все активные профили (${activeMinecraftLinks.length}). Общий Minecraft-баланс не изменится.`, okText: 'Отвязать все', cancelText: 'Отмена' });
    if (ok) await runAction('mc-all', () => unlinkAllAdminMinecraftLinks(userId), 'Все Minecraft-профили отвязаны', 'Не удалось отвязать Minecraft-профили');
  };

  const queueBlock = async () => {
    if (isSelf) return notify.error('Нельзя заблокировать собственный аккаунт');
    await runAction('block', () => blockAccount(userId, { reason: blockReason || 'manual', note: blockNote || null }), 'Блокировка поставлена в очередь', 'Не удалось поставить блокировку', false);
    await load();
  };

  const queueUnblock = async () => {
    await runAction('unblock', () => unblockAccount(userId), 'Разблокировка поставлена в очередь', 'Не удалось поставить разблокировку', false);
    await load();
  };

  const deleteCodeSolutions = async () => {
    const ok = await notify.confirm({ title: 'Удалить code-решения?', message: 'Будут удалены все code-сабмиты пользователя. Действие необратимо.', okText: 'Удалить', cancelText: 'Отмена' });
    if (ok) await runAction('delete-code', () => deleteUserSolutions(userId, {}), 'Code-решения удалены', 'Не удалось удалить code-решения');
  };

  const deleteUserAccount = async () => {
    const confirmation = String(deleteConfirmation || '').trim();
    const accepted = [userId, user?.login, user?.displayName, user?.fullName].filter(Boolean).some((x) => String(x).trim().toLowerCase() === confirmation.toLowerCase());
    if (!accepted) return notify.error('Введи точный логин, имя или ID удаляемого аккаунта');
    const operation = await runAction('delete-account', () => createAccountOperation({ type: 'delete', sourceUserId: userId, reason: deleteReason || 'Удаление администратором', confirmation, hardDelete: false }), 'Безопасное удаление поставлено в очередь', 'Не удалось удалить пользователя', false);
    if (operation) navigate('/admin/ai/account-manager?tab=operations');
  };

  const score = numeric(rating?.score ?? rating?.rating ?? rating?.totalScore);
  const solved = numeric(rating?.solved ?? rating?.solvedCount);
  const attempts = numeric(rating?.totalAttempts);
  const totalRecent = codeSolutions.length + imageSolutions.length + testAttempts.length + mathAttempts.length;
  const attemptsShown = attempts > 0 ? attempts : totalRecent;
  const statusLabel = user?.blocked ? 'Заблокирован' : user?.accountStatus === 'merged' ? 'Объединён' : user?.accountStatus === 'deleted' ? 'Удалён' : 'Активен';
  const statusIntent = user?.blocked || user?.accountStatus === 'deleted' ? 'danger' : user?.accountStatus === 'merged' ? 'outline' : 'success';

  return (
    <div className="space-y-4 sm:space-y-6">
      <div className="flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
        <div>
          <Link to="/admin/users" className="mb-2 inline-flex items-center gap-2 text-sm text-neutral-500 hover:text-[rgb(var(--text))]"><ArrowLeft size={16} /> Пользователи</Link>
          <h1 className="flex flex-wrap items-center gap-2 text-xl font-semibold sm:text-2xl"><UserCog size={22} /> {userTitle(user)} {(user?.accountType === 'ai' || user?.isAi) ? <Badge intent="outline"><Bot size={13} className="mr-1 inline" />AI-аккаунт</Badge> : null}</h1>
          <p className="mt-2 break-all text-sm text-neutral-500">Профиль, роли, группы, реальные Telegram/Minecraft-привязки и жизненный цикл аккаунта. ID: {userId}</p>
        </div>
        <div className="flex flex-col gap-2 sm:flex-row"><Button variant="outline" onClick={load}><RefreshCcw size={16} /><span className="ml-1">Обновить</span></Button><Button onClick={saveUser} disabled={!form || saving || !isActive}><Save size={16} /><span className="ml-1">Сохранить</span></Button></div>
      </div>

      {pageError ? <AppErrorPanel error={pageError} title="Не удалось выполнить действие" /> : null}
      {loading ? <div className="text-neutral-500">Загрузка…</div> : null}

      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-6 sm:gap-4">
        <MetricCard label="Состояние" value={statusLabel} hint={user?.blockReason || user?.deletionReason || 'Текущее состояние аккаунта'} />
        <MetricCard label="Рейтинг" value={score} hint="Текущий рейтинг" />
        <MetricCard label="MC баланс" value={minecraftRating?.minecraftBalance ?? minecraftRating?.balance ?? 0} hint="Общий баланс всех Minecraft-профилей" />
        <MetricCard label="Решено" value={solved} hint="Уникальные зачтённые задания" />
        <MetricCard label="Попыток" value={attemptsShown} hint="Последняя доступная статистика" />
        <MetricCard label="Групп" value={groupIds.size} hint="Текущие учебные группы" />
      </div>

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-[1.1fr,0.9fr] sm:gap-6">
        <Card>
          <div className="mb-4 flex items-center gap-2 font-semibold"><Shield size={18} /> Аккаунт</div>
          {form ? <div className="grid grid-cols-1 gap-3 md:grid-cols-2 sm:gap-4">
            <Field label="Логин"><Input disabled={!isActive} value={form.login || ''} onChange={(e) => updateForm({ login: e.target.value })} /></Field>
            <Field label="Email"><Input disabled={!isActive} value={form.email || ''} onChange={(e) => updateForm({ email: e.target.value })} /></Field>
            <Field label="Базовая роль"><Select disabled={!isActive} value={form.role || 'User'} onChange={(e) => updateForm({ role: e.target.value })}>{baseRoles.map((x) => <option key={x} value={x}>{x}</option>)}</Select></Field>
            <Field label="Тип аккаунта"><Select disabled={!isActive} value={form.accountType || 'human'} onChange={(e) => updateForm({ accountType: e.target.value })}><option value="human">Человек</option><option value="ai">AI</option></Select></Field>
            <Field label="Телефон"><Input disabled={!isActive} value={form.phoneNumber || ''} onChange={(e) => updateForm({ phoneNumber: e.target.value })} /></Field>
            <Field label="Имя"><Input disabled={!isActive} value={form.firstName || ''} onChange={(e) => updateForm({ firstName: e.target.value })} /></Field>
            <Field label="Фамилия"><Input disabled={!isActive} value={form.lastName || ''} onChange={(e) => updateForm({ lastName: e.target.value })} /></Field>
            <div className="md:col-span-2"><Field label="Аватар / URL картинки"><Input disabled={!isActive} value={form.profilePictureUrl || ''} onChange={(e) => updateForm({ profilePictureUrl: e.target.value })} /></Field></div>
          </div> : <div className="text-sm text-neutral-500">Нет данных профиля.</div>}
          <div className="mt-5 flex flex-wrap gap-2"><Button onClick={saveUser} disabled={!form || saving || !isActive}><Save size={16} /><span className="ml-1">Сохранить профиль</span></Button><Link to={`/users/${userId}`}><Button variant="outline"><ExternalLink size={16} /><span className="ml-1">Публичный профиль</span></Button></Link></div>
        </Card>

        <Card>
          <div className="mb-4 flex items-center justify-between gap-3"><div className="font-semibold">Жизненный цикл</div><Badge intent={statusIntent}>{statusLabel}</Badge></div>
          <div className="space-y-2 text-sm text-neutral-500">
            <div>Создан: {formatDate(user?.createdAt)}</div><div>Последний вход: {formatDate(user?.lastLoginAt)}</div>
            {user?.blocked ? <><div>Заблокирован: {formatDate(user?.blockedAtUtc)}</div><div>Причина: {user?.blockReason || '—'}</div><div>До: {formatDate(user?.blockExpiresAtUtc)}</div></> : null}
            {user?.mergedIntoUserId ? <div>Объединён в: <Link className="text-[rgb(var(--accent-600))] hover:underline" to={`/admin/users/${user.mergedIntoUserId}`}>{user.mergedIntoUserId}</Link></div> : null}
            {user?.deletedAtUtc ? <div>Удалён: {formatDate(user.deletedAtUtc)}</div> : null}
          </div>
          {activeOperation ? <div className="mt-4 rounded-2xl border border-amber-500/40 bg-amber-500/10 p-3 text-sm"><div className="font-medium">{operationLabel(activeOperation.type)} выполняется</div><div className="mt-1 text-xs text-neutral-500">{activeOperation.progressPercent || 0}% · {activeOperation.phase || activeOperation.status}</div></div> : null}
          <div className="mt-4 grid gap-2">
            {!user?.blocked ? <><Field label="Причина блокировки"><Input value={blockReason} onChange={(e) => setBlockReason(e.target.value)} /></Field><Field label="Комментарий"><Textarea rows={3} value={blockNote} onChange={(e) => setBlockNote(e.target.value)} /></Field><Button variant="outline" className="text-red-600 border-red-300 hover:bg-red-50 dark:hover:bg-red-900/20" disabled={!isActive || isSelf || actionBusy === 'block' || !!activeOperation} onClick={queueBlock}><Ban size={16} /><span className="ml-1">Заблокировать</span></Button></> : <Button variant="outline" disabled={actionBusy === 'unblock' || !!activeOperation} onClick={queueUnblock}><ShieldCheck size={16} /><span className="ml-1">Разблокировать</span></Button>}
            <Link to={`/admin/ai/account-manager?tab=operations`}><Button variant="outline" className="w-full"><History size={16} /><span className="ml-1">Журнал операций</span></Button></Link>
            <Link to="/admin/ai/account-manager"><Button variant="outline" className="w-full"><Merge size={16} /><span className="ml-1">Поиск дублей и объединение</span></Button></Link>
          </div>
        </Card>
      </div>

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-2 sm:gap-6">
        <Card>
          <div className="mb-4 flex items-center justify-between gap-3"><div className="flex items-center gap-2 font-semibold"><Bot size={18} /> Telegram</div><Badge intent={user?.telegramLinked ? 'success' : 'outline'}>{user?.telegramLinked ? 'Привязан' : 'Не привязан'}</Badge></div>
          {user?.telegramLinked ? <div className="space-y-2 text-sm"><div>Username: <strong>{telegramHandle(user.telegramUsername) || 'не задан в Telegram'}</strong></div><div className="break-all">Chat ID: {user.telegramChatId || '—'}</div><div>Привязан: {formatDate(user.telegramLinkedAtUtc)}</div><div>Всего успешных привязок: {user.telegramLinkCount ?? 0}</div><Button variant="outline" className="mt-2 text-red-600 border-red-300 hover:bg-red-50 dark:hover:bg-red-900/20" disabled={actionBusy === 'telegram-unlink' || !isActive} onClick={removeTelegram}><Link2 size={16} /><span className="ml-1">Отвязать Telegram</span></Button></div> : <div className="text-sm text-neutral-500">Telegram не привязан. Новую привязку пользователь выполняет самостоятельно через бота.</div>}
        </Card>

        <Card>
          <div className="mb-4 flex items-center justify-between gap-3"><div className="flex items-center gap-2 font-semibold"><Pickaxe size={18} /> Minecraft-профили</div><Badge intent={activeMinecraftLinks.length ? 'success' : 'outline'}>{activeMinecraftLinks.length} активных</Badge></div>
          <div className="space-y-2">
            {activeMinecraftLinks.length ? activeMinecraftLinks.map((link) => <div key={link.id} className="rounded-2xl border border-[rgb(var(--border))] p-3"><div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between"><div className="min-w-0"><div className="font-medium">{link.nick || 'Без ника'}</div><div className="mt-1 break-all text-xs text-neutral-500">UUID: {link.uuid || 'ещё не закреплён'}</div><div className="mt-1 text-xs text-neutral-500">Привязан: {formatDate(link.linkedAtUtc)}</div></div><Button variant="outline" className="text-red-600 border-red-300 hover:bg-red-50 dark:hover:bg-red-900/20" disabled={actionBusy === `mc-${link.id}` || !isActive} onClick={() => removeMinecraftLink(link)}>Отвязать</Button></div></div>) : <div className="text-sm text-neutral-500">Активных Minecraft-профилей нет.</div>}
          </div>
          {activeMinecraftLinks.length > 1 ? <Button variant="outline" className="mt-3 text-red-600 border-red-300 hover:bg-red-50 dark:hover:bg-red-900/20" disabled={actionBusy === 'mc-all' || !isActive} onClick={removeAllMinecraftLinks}>Отвязать все профили</Button> : null}
          <div className="mt-3 text-xs text-neutral-500">Отвязка не удаляет и не пересчитывает общий Minecraft-баланс пользователя.</div>
        </Card>
      </div>

      <Card>
        <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
          <div className="min-w-0"><div className="flex items-center gap-2 font-semibold"><Pickaxe size={18} /> Minecraft-баланс</div><div className="mt-1 text-sm text-neutral-500">Один общий баланс для всех активных Minecraft-профилей.</div><div className="mt-4 grid grid-cols-2 gap-3 text-sm lg:grid-cols-5"><div className="rounded-2xl border border-[rgb(var(--border))] p-3"><div className="text-xs opacity-60">Основной</div><div className="mt-1 text-xl font-semibold">{minecraftRating?.baseRating ?? score}</div></div><div className="rounded-2xl border border-[rgb(var(--border))] p-3"><div className="text-xs opacity-60">Баланс MC</div><div className="mt-1 text-xl font-semibold">{minecraftRating?.minecraftBalance ?? minecraftRating?.balance ?? 0}</div></div><div className="rounded-2xl border border-[rgb(var(--border))] p-3"><div className="text-xs opacity-60">Потрачено</div><div className="mt-1 text-xl font-semibold">{minecraftRating?.minecraftSpent ?? 0}</div></div><div className="rounded-2xl border border-[rgb(var(--border))] p-3"><div className="text-xs opacity-60">Восстановлено</div><div className="mt-1 text-xl font-semibold">{minecraftRating?.minecraftRestored ?? 0}</div></div><div className="rounded-2xl border border-[rgb(var(--border))] p-3"><div className="text-xs opacity-60">Координаты / сундук / возврат</div><div className="mt-1 text-xl font-semibold">{minecraftRating?.deathCoordinatesCost ?? 10} / {minecraftRating?.deathChestCost ?? 50} / {minecraftRating?.deathTeleportCost ?? 100}</div></div></div></div>
          <div className="w-full rounded-2xl border border-[rgb(var(--border))] p-3 lg:max-w-sm"><div className="mb-3 text-sm font-medium">Восстановить баланс</div><div className="space-y-2"><Field label="Сумма"><Input value={mcRestoreAmount} onChange={(e) => setMcRestoreAmount(e.target.value)} inputMode="numeric" /></Field><Field label="Причина"><Input value={mcRestoreReason} onChange={(e) => setMcRestoreReason(e.target.value)} placeholder="Например: возврат после сбоя" /></Field><Button onClick={restoreMinecraftRating} disabled={mcRestoring}>{mcRestoring ? 'Сохранение…' : 'Восстановить'}</Button></div></div>
        </div>
        <div className="mt-4"><div className="mb-2 text-sm font-medium">Последние операции</div><div className="max-h-[280px] space-y-2 overflow-auto pr-1">{(minecraftRating?.transactions || []).length ? minecraftRating.transactions.map((tx) => <div key={tx.id} className="flex flex-col gap-2 rounded-2xl border border-[rgb(var(--border))] p-3 text-sm sm:flex-row sm:items-center sm:justify-between"><div><div className="font-medium">{tx.reason || tx.kind}</div><div className="mt-1 text-xs text-neutral-500">{formatDate(tx.createdAtUtc)} · {tx.kind}</div></div><Badge intent={Number(tx.delta || 0) >= 0 ? 'success' : 'danger'}>{Number(tx.delta || 0) >= 0 ? '+' : ''}{tx.delta}</Badge></div>) : <div className="text-sm text-neutral-500">Операций Minecraft-баланса пока нет.</div>}</div></div>
      </Card>

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-2 sm:gap-6">
        <Card><div className="mb-3 font-semibold">Группы</div><div className="max-h-[420px] space-y-2 overflow-auto pr-1">{groups.length ? groups.map((group) => { const enabled = groupIds.has(String(group.id).toLowerCase()); return <label key={group.id} className="flex cursor-pointer items-start justify-between gap-3 rounded-2xl border border-[rgb(var(--border))] p-3 hover:bg-white/5"><span><span className="block font-medium">{group.name || group.code || group.id}</span><span className="mt-1 block text-xs text-neutral-500">{group.code || group.description || group.id}</span></span><input type="checkbox" disabled={!isActive || !!activeOperation} checked={enabled} onChange={() => toggleGroup(group)} /></label>; }) : <div className="text-sm text-neutral-500">Групп нет.</div>}</div></Card>
        <Card><div className="mb-3 font-semibold">Дополнительные роли</div><div className="max-h-[420px] space-y-2 overflow-auto pr-1">{featureRoles.length ? featureRoles.map((role) => { const code = role.code || role.Code || role.title || role.name; const enabled = roleSet.has(code); return <label key={role.id || code} className="flex cursor-pointer items-start justify-between gap-3 rounded-2xl border border-[rgb(var(--border))] p-3 hover:bg-white/5"><span><span className="block font-medium">{role.title || role.name || code}</span><span className="mt-1 block text-xs text-neutral-500">{code}</span></span><input type="checkbox" disabled={!isActive || !!activeOperation} checked={enabled} onChange={() => toggleFeatureRole(role)} /></label>; }) : <div className="text-sm text-neutral-500">Дополнительных ролей нет.</div>}</div></Card>
      </div>

      <Card>
        <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
          <div><div className="font-semibold">Опасные действия</div><div className="mt-1 text-sm text-neutral-500">Безопасное удаление проходит через все микросервисы, обезличивает Identity-запись и сохраняет аудит. Прямого DELETE пользователя нет.</div></div>
          <Button variant="outline" onClick={deleteCodeSolutions} disabled={actionBusy === 'delete-code'}>Удалить code-решения</Button>
        </div>
        <div className="mt-4 rounded-2xl border border-red-500/30 bg-red-500/5 p-4">
          <div className="font-medium text-red-600 dark:text-red-300">Удаление аккаунта</div>
          <div className="mt-2 text-sm text-neutral-500">Для подтверждения введи точный логин <strong>{user?.login || '—'}</strong>, полное имя или ID. Аккаунт сначала блокируется, затем очищается по всем сервисам.</div>
          <div className="mt-3 grid gap-3 lg:grid-cols-[1fr,1fr,auto] lg:items-end"><Field label="Причина"><Input value={deleteReason} onChange={(e) => setDeleteReason(e.target.value)} /></Field><Field label="Подтверждение"><Input value={deleteConfirmation} onChange={(e) => setDeleteConfirmation(e.target.value)} placeholder={user?.login || userId} /></Field><Button variant="outline" className="text-red-600 border-red-300 hover:bg-red-50 dark:hover:bg-red-900/20" disabled={!isActive || isSelf || !!activeOperation || actionBusy === 'delete-account'} onClick={deleteUserAccount}><Trash2 size={16} /><span className="ml-1">Безопасно удалить</span></Button></div>
        </div>
      </Card>

      <Card>
        <div className="mb-3 flex items-center justify-between gap-3"><div className="flex items-center gap-2 font-semibold"><History size={18} /> Последние операции аккаунта</div><Link className="text-sm text-[rgb(var(--accent-600))] hover:underline" to="/admin/ai/account-manager?tab=operations">Открыть журнал</Link></div>
        <div className="space-y-2">{operations.length ? operations.slice(0, 8).map((operation) => <div key={operation.id} className="rounded-2xl border border-[rgb(var(--border))] p-3"><div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between"><div><div className="font-medium">{operationLabel(operation.type)}</div><div className="mt-1 text-xs text-neutral-500">{formatDate(operation.createdAtUtc)} · {operation.phase || operation.status}</div></div><div className="flex items-center gap-2"><Badge intent={operationIntent(operation.status)}>{operation.status}</Badge><Badge intent="outline">{operation.progressPercent || 0}%</Badge></div></div></div>) : <div className="text-sm text-neutral-500">Операций жизненного цикла пока нет.</div>}</div>
      </Card>

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-2 sm:gap-6"><ActivityList title="Последние code-решения" rows={codeSolutions} emptyText="Code-решений нет." /><ActivityList title="Последние image-решения" rows={imageSolutions} emptyText="Image-решений нет." /><ActivityList title="Последние тесты" rows={testAttempts} emptyText="Попыток тестов нет." /><ActivityList title="Последние math-попытки" rows={mathAttempts} emptyText="Math-попыток нет." /></div>
    </div>
  );
}
