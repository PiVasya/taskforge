import React, { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import Layout from '../../components/Layout';
import { Badge, Button, Card, Field, Input, Select } from '../../components/ui';
import AppErrorPanel from '../../components/AppErrorPanel';
import { deleteAdminUser, getAdminUser, updateAdminUser } from '../../api/adminUsers';
import { getAdminMinecraftUserRating, restoreAdminMinecraftUserRating } from '../../api/adminMinecraftLinks';
import { deleteUserSolutions, getAdminUserGroupIds, getUserImageSolutions, getUserSolutions, searchUsersOnce } from '../../api/admin';
import { assignFeatureRole, getFeatureRoles, removeFeatureRole } from '../../api/featureRoles';
import { addGroupMember, getAdminGroups, removeGroupMember } from '../../api/groups';
import { getUserMathAttempts } from '../../api/mathTaskAttempts';
import { getUserTaskTestAttempts } from '../../api/taskTestAttempts';
import { handleApiError } from '../../utils/handleApiError';
import { useNotify } from '../../components/notify/NotifyProvider';
import { ArrowLeft, ExternalLink, RefreshCcw, Save, Shield, Trash2, UserCog, Pickaxe } from 'lucide-react';

const baseRoles = ['User', 'Editor', 'Admin'];

const formatDate = (value) => {
  if (!value) return '—';
  try {
    return new Date(value).toLocaleString();
  } catch {
    return '—';
  }
};

const userTitle = (user) => user?.fullName || user?.displayName || user?.login || user?.email || 'Пользователь';
const numeric = (value) => Number.isFinite(Number(value)) ? Number(value) : 0;

function MetricCard({ label, value, hint }) {
  return (
    <Card>
      <div className="text-sm opacity-70">{label}</div>
      <div className="text-3xl font-semibold mt-2">{value}</div>
      {hint ? <div className="text-xs text-neutral-500 mt-2">{hint}</div> : null}
    </Card>
  );
}

function ActivityList({ title, rows, emptyText }) {
  return (
    <Card>
      <div className="font-semibold mb-3">{title}</div>
      <div className="space-y-2">
        {(rows || []).length ? rows.map((row, index) => (
          <div key={row.id || row.attemptId || `${title}-${index}`} className="rounded-2xl border border-[rgb(var(--border))] p-3">
            <div className="flex flex-col sm:flex-row sm:items-start sm:justify-between gap-2">
              <div className="min-w-0">
                <div className="font-medium truncate">{row.assignmentTitle || row.title || row.assignmentName || row.assignmentId || 'Без названия'}</div>
                <div className="text-xs text-neutral-500 mt-1 break-all">{row.assignmentId || row.id || row.attemptId || ''}</div>
              </div>
              <Badge intent={(row.status === 'Accepted' || row.passed === true || row.isPassed === true) ? 'success' : 'outline'}>
                {row.status || (row.passed === true || row.isPassed === true ? 'Пройдено' : 'Не пройдено')}
              </Badge>
            </div>
            <div className="text-xs text-neutral-500 mt-2">{formatDate(row.createdAt || row.submittedAt || row.finishedAt || row.startedAt)}</div>
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
  const [mcRestoreAmount, setMcRestoreAmount] = useState('100');
  const [mcRestoreReason, setMcRestoreReason] = useState('');
  const [mcRestoring, setMcRestoring] = useState(false);
  const [saving, setSaving] = useState(false);

  const roleSet = useMemo(() => new Set(form?.featureRoles || form?.roles || []), [form]);

  const load = async () => {
    try {
      setLoading(true);
      const [userDto, ratingRows, allGroups, userGroups, allFeatureRoles, codeRows, imageRows, testRows, mathRows, mcRatingDto] = await Promise.all([
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
      ]);

      const ratingDto = Array.isArray(ratingRows) ? ratingRows.find((x) => String(x.userId || x.id).toLowerCase() === String(userId).toLowerCase()) : null;
      setUser(userDto);
      setForm({
        login: userDto?.login || '',
        email: userDto?.email || '',
        firstName: userDto?.firstName || '',
        lastName: userDto?.lastName || '',
        phoneNumber: userDto?.phoneNumber || '',
        profilePictureUrl: userDto?.profilePictureUrl || '',
        role: userDto?.role || 'User',
        roles: userDto?.roles || [],
        featureRoles: userDto?.featureRoles || [],
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
      setPageError(null);
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось загрузить профиль пользователя');
      setPageError(parsed);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    if (userId) load();
  }, [userId]);

  const updateForm = (patch) => setForm((prev) => ({ ...(prev || {}), ...patch }));

  const saveUser = async () => {
    try {
      setSaving(true);
      const saved = await updateAdminUser(userId, {
        login: form.login,
        email: form.email,
        firstName: form.firstName,
        lastName: form.lastName,
        phoneNumber: form.phoneNumber || null,
        profilePictureUrl: form.profilePictureUrl || null,
        role: form.role,
      });
      notify.success('Пользователь сохранён');
      setPageError(null);
      await load();
      return saved;
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось сохранить пользователя');
      setPageError(parsed);
      return null;
    } finally {
      setSaving(false);
    }
  };

  const toggleGroup = async (group) => {
    const id = String(group.id).toLowerCase();
    const enabled = groupIds.has(id);
    try {
      if (enabled) await removeGroupMember(group.id, userId);
      else await addGroupMember(group.id, userId);
      notify.success(enabled ? 'Пользователь удалён из группы' : 'Пользователь добавлен в группу');
      setGroupIds((prev) => {
        const next = new Set(prev);
        if (enabled) next.delete(id);
        else next.add(id);
        return next;
      });
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось изменить группу пользователя');
      setPageError(parsed);
    }
  };

  const toggleFeatureRole = async (role) => {
    const code = role.code || role.Code || role.title || role.name;
    if (!code) return;
    const enabled = roleSet.has(code);
    try {
      if (enabled) await removeFeatureRole(userId, code);
      else await assignFeatureRole(userId, code);
      notify.success(enabled ? 'Роль снята' : 'Роль выдана');
      await load();
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось изменить дополнительную роль');
      setPageError(parsed);
    }
  };

  const restoreMinecraftRating = async () => {
    const amount = Math.floor(Number(mcRestoreAmount || 0));
    if (!Number.isFinite(amount) || amount <= 0) {
      notify.error('Укажи сумму восстановления больше 0');
      return;
    }
    try {
      setMcRestoring(true);
      await restoreAdminMinecraftUserRating(userId, { amount, reason: mcRestoreReason || null });
      notify.success('Minecraft-баланс восстановлен');
      setMcRestoreReason('');
      const fresh = await getAdminMinecraftUserRating(userId).catch(() => null);
      setMinecraftRating(fresh || null);
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось восстановить Minecraft-баланс');
      setPageError(parsed);
    } finally {
      setMcRestoring(false);
    }
  };

  const deleteCodeSolutions = async () => {
    const ok = await notify.confirm({
      title: 'Удалить code-решения?',
      message: 'Будут удалены все code-сабмиты пользователя. Действие необратимо.',
      okText: 'Удалить',
      cancelText: 'Отмена',
    });
    if (!ok) return;

    try {
      await deleteUserSolutions(userId, {});
      notify.success('Code-решения удалены');
      await load();
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось удалить code-решения');
      setPageError(parsed);
    }
  };

  const deleteUserAccount = async () => {
    const label = userTitle(user);
    const ok = await notify.confirm({
      title: 'Удалить пользователя?',
      message: `Аккаунт ${label} будет удалён. Это действие необратимо.`,
      okText: 'Удалить',
      cancelText: 'Отмена',
    });
    if (!ok) return;

    try {
      await deleteAdminUser(userId);
      notify.success('Пользователь удалён');
      navigate('/admin/users');
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось удалить пользователя');
      setPageError(parsed);
    }
  };

  const score = numeric(rating?.score ?? rating?.rating ?? rating?.totalScore);
  const solved = numeric(rating?.solved ?? rating?.solvedCount);
  const attempts = numeric(rating?.totalAttempts);
  const totalRecent = codeSolutions.length + imageSolutions.length + testAttempts.length + mathAttempts.length;
  const attemptsShown = attempts > 0 ? attempts : totalRecent;

  return (
    <Layout>
      <div className="space-y-4 sm:space-y-6">
        <div className="flex flex-col lg:flex-row lg:items-center lg:justify-between gap-3">
          <div>
            <Link to="/admin/users" className="inline-flex items-center gap-2 text-sm text-neutral-500 hover:text-[rgb(var(--text))] mb-2">
              <ArrowLeft size={16} /> Пользователи
            </Link>
            <h1 className="text-xl sm:text-2xl font-semibold flex items-center gap-2"><UserCog size={22} /> {userTitle(user)}</h1>
            <p className="text-sm text-neutral-500 mt-2 break-all">Полное управление пользователем: профиль, группы, роли, рейтинг и последние решения. ID: {userId}</p>
          </div>
          <div className="flex flex-col sm:flex-row gap-2">
            <Button variant="outline" onClick={load}><RefreshCcw size={16} /> <span className="ml-1">Обновить</span></Button>
            <Button onClick={saveUser} disabled={!form || saving}><Save size={16} /> <span className="ml-1">Сохранить</span></Button>
          </div>
        </div>

        {pageError ? <AppErrorPanel error={pageError} title="Не удалось выполнить действие" /> : null}
        {loading && <div className="text-neutral-500">Загрузка…</div>}

        <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-5 sm:gap-4">
          <MetricCard label="Рейтинг" value={score} hint="По текущему rating решённых заданий" />
          <MetricCard label="MC баланс" value={minecraftRating?.minecraftBalance ?? minecraftRating?.balance ?? 0} hint="Рейтинг с учётом Minecraft-трат" />
          <MetricCard label="Решено" value={solved} hint="Уникальные зачтённые задания" />
          <MetricCard label="Попыток" value={attemptsShown} hint="Все отправки пользователя, включая отклонённые" />
          <MetricCard label="Групп" value={groupIds.size} hint="Текущие учебные группы" />
        </div>

        <div className="grid grid-cols-1 xl:grid-cols-[1.1fr,0.9fr] gap-4 sm:gap-6">
          <Card>
            <div className="flex items-center gap-2 mb-4 font-semibold"><Shield size={18} /> Аккаунт</div>
            {form ? (
              <div className="grid grid-cols-1 md:grid-cols-2 gap-3 sm:gap-4">
                <Field label="Логин"><Input value={form.login || ''} onChange={(e) => updateForm({ login: e.target.value })} /></Field>
                <Field label="Email"><Input value={form.email || ''} onChange={(e) => updateForm({ email: e.target.value })} /></Field>
                <Field label="Базовая роль"><Select value={form.role || 'User'} onChange={(e) => updateForm({ role: e.target.value })}>{baseRoles.map((x) => <option key={x} value={x}>{x}</option>)}</Select></Field>
                <Field label="Телефон"><Input value={form.phoneNumber || ''} onChange={(e) => updateForm({ phoneNumber: e.target.value })} /></Field>
                <Field label="Имя"><Input value={form.firstName || ''} onChange={(e) => updateForm({ firstName: e.target.value })} /></Field>
                <Field label="Фамилия"><Input value={form.lastName || ''} onChange={(e) => updateForm({ lastName: e.target.value })} /></Field>
                <div className="md:col-span-2"><Field label="Аватар / URL картинки"><Input value={form.profilePictureUrl || ''} onChange={(e) => updateForm({ profilePictureUrl: e.target.value })} /></Field></div>
              </div>
            ) : <div className="text-sm text-neutral-500">Нет данных профиля.</div>}
            <div className="flex flex-wrap gap-2 mt-5">
              <Button onClick={saveUser} disabled={!form || saving}><Save size={16} /> <span className="ml-1">Сохранить профиль</span></Button>
              <Button variant="outline" onClick={deleteUserAccount}><Trash2 size={16} /> <span className="ml-1">Удалить пользователя</span></Button>
            </div>
          </Card>

          <Card>
            <div className="font-semibold mb-4">Сводка</div>
            <div className="space-y-2 text-sm text-neutral-500 break-words">
              <div>Создан: {formatDate(user?.createdAt)}</div>
              <div>Последний вход: {formatDate(user?.lastLoginAt)}</div>
              <div>Email: {user?.email || user?.maskedEmail || '—'}</div>
              <div>Публичный профиль: <Link className="text-[rgb(var(--accent-600))] hover:underline" to={`/users/${userId}`}>открыть <ExternalLink size={12} className="inline" /></Link></div>
            </div>
            <div className="flex flex-wrap gap-2 mt-4">
              {(form?.roles || []).length ? form.roles.map((role) => <Badge key={role} intent="outline">{role}</Badge>) : <span className="text-sm text-neutral-500">Дополнительных ролей нет</span>}
            </div>
            <div className="mt-5 rounded-2xl border border-amber-500/40 bg-amber-500/10 p-3 text-sm text-amber-800 dark:text-amber-200">
              Интеграционные поля Minecraft/Telegram в текущей identity-модели не редактируются напрямую. Управление ими лучше держать в отдельных разделах интеграций.
            </div>
          </Card>
        </div>

        <Card>
          <div className="flex flex-col lg:flex-row lg:items-start lg:justify-between gap-4">
            <div className="min-w-0">
              <div className="font-semibold flex items-center gap-2"><Pickaxe size={18} /> Minecraft-баланс</div>
              <div className="text-sm text-neutral-500 mt-1">Основной рейтинг не меняется. Minecraft хранит отдельные списания и восстановления относительно него.</div>
              <div className="grid grid-cols-2 lg:grid-cols-5 gap-3 mt-4 text-sm">
                <div className="rounded-2xl border border-[rgb(var(--border))] p-3"><div className="text-xs opacity-60">Основной</div><div className="text-xl font-semibold">{minecraftRating?.baseRating ?? score}</div></div>
                <div className="rounded-2xl border border-[rgb(var(--border))] p-3"><div className="text-xs opacity-60">Баланс MC</div><div className="text-xl font-semibold">{minecraftRating?.minecraftBalance ?? minecraftRating?.balance ?? 0}</div></div>
                <div className="rounded-2xl border border-[rgb(var(--border))] p-3"><div className="text-xs opacity-60">Потрачено</div><div className="text-xl font-semibold">{minecraftRating?.minecraftSpent ?? 0}</div></div>
                <div className="rounded-2xl border border-[rgb(var(--border))] p-3"><div className="text-xs opacity-60">Восстановлено</div><div className="text-xl font-semibold">{minecraftRating?.minecraftRestored ?? 0}</div></div>
                <div className="rounded-2xl border border-[rgb(var(--border))] p-3"><div className="text-xs opacity-60">Координаты / сундук / возврат</div><div className="text-xl font-semibold">{minecraftRating?.deathCoordinatesCost ?? 10} / {minecraftRating?.deathChestCost ?? 50} / {minecraftRating?.deathTeleportCost ?? 100}</div></div>
              </div>
            </div>
            <div className="w-full lg:max-w-sm rounded-2xl border border-[rgb(var(--border))] p-3">
              <div className="font-medium text-sm mb-3">Восстановить баланс</div>
              <div className="space-y-2">
                <Field label="Сумма"><Input value={mcRestoreAmount} onChange={(e) => setMcRestoreAmount(e.target.value)} inputMode="numeric" /></Field>
                <Field label="Причина"><Input value={mcRestoreReason} onChange={(e) => setMcRestoreReason(e.target.value)} placeholder="Например: возврат после сбоя телепорта" /></Field>
                <Button onClick={restoreMinecraftRating} disabled={mcRestoring}>{mcRestoring ? 'Сохранение…' : 'Восстановить'}</Button>
              </div>
            </div>
          </div>
          <div className="mt-4">
            <div className="font-medium text-sm mb-2">Последние операции</div>
            <div className="space-y-2 max-h-[280px] overflow-auto pr-1">
              {(minecraftRating?.transactions || []).length ? minecraftRating.transactions.map((tx) => (
                <div key={tx.id} className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-2 rounded-2xl border border-[rgb(var(--border))] p-3 text-sm">
                  <div>
                    <div className="font-medium">{tx.reason || tx.kind}</div>
                    <div className="text-xs text-neutral-500 mt-1">{tx.createdAtUtc ? new Date(tx.createdAtUtc).toLocaleString() : '—'} · {tx.kind}</div>
                  </div>
                  <Badge intent={Number(tx.delta || 0) >= 0 ? 'success' : 'danger'}>{Number(tx.delta || 0) >= 0 ? '+' : ''}{tx.delta}</Badge>
                </div>
              )) : <div className="text-sm text-neutral-500">Операций Minecraft-баланса пока нет.</div>}
            </div>
          </div>
        </Card>

        <div className="grid grid-cols-1 xl:grid-cols-2 gap-4 sm:gap-6">
          <Card>
            <div className="font-semibold mb-3">Группы</div>
            <div className="space-y-2 max-h-[420px] overflow-auto pr-1">
              {groups.length ? groups.map((group) => {
                const enabled = groupIds.has(String(group.id).toLowerCase());
                return (
                  <label key={group.id} className="flex items-start justify-between gap-3 rounded-2xl border border-[rgb(var(--border))] p-3 cursor-pointer hover:bg-white/5">
                    <span>
                      <span className="font-medium block">{group.name || group.code || group.id}</span>
                      <span className="text-xs text-neutral-500 block mt-1">{group.code || group.description || group.id}</span>
                    </span>
                    <input type="checkbox" checked={enabled} onChange={() => toggleGroup(group)} />
                  </label>
                );
              }) : <div className="text-sm text-neutral-500">Групп нет.</div>}
            </div>
          </Card>

          <Card>
            <div className="font-semibold mb-3">Дополнительные роли</div>
            <div className="space-y-2 max-h-[420px] overflow-auto pr-1">
              {featureRoles.length ? featureRoles.map((role) => {
                const code = role.code || role.Code || role.title || role.name;
                const enabled = roleSet.has(code);
                return (
                  <label key={role.id || code} className="flex items-start justify-between gap-3 rounded-2xl border border-[rgb(var(--border))] p-3 cursor-pointer hover:bg-white/5">
                    <span>
                      <span className="font-medium block">{role.title || role.name || code}</span>
                      <span className="text-xs text-neutral-500 block mt-1">{code}</span>
                    </span>
                    <input type="checkbox" checked={enabled} onChange={() => toggleFeatureRole(role)} />
                  </label>
                );
              }) : <div className="text-sm text-neutral-500">Дополнительных ролей нет.</div>}
            </div>
          </Card>
        </div>

        <Card>
          <div className="flex flex-col lg:flex-row lg:items-center lg:justify-between gap-3">
            <div>
              <div className="font-semibold">Опасные действия</div>
              <div className="text-sm text-neutral-500 mt-1">Массовая очистка нужна для тестовых аккаунтов и пересдач. Удаление code-решений не трогает test/math/image попытки.</div>
            </div>
            <div className="flex flex-col sm:flex-row gap-2">
              <Button variant="outline" onClick={deleteCodeSolutions}>Удалить code-решения</Button>
              <Button variant="outline" onClick={deleteUserAccount}>Удалить аккаунт</Button>
            </div>
          </div>
        </Card>

        <div className="grid grid-cols-1 xl:grid-cols-2 gap-4 sm:gap-6">
          <ActivityList title="Последние code-решения" rows={codeSolutions} emptyText="Code-решений нет." />
          <ActivityList title="Последние image-решения" rows={imageSolutions} emptyText="Image-решений нет." />
          <ActivityList title="Последние тесты" rows={testAttempts} emptyText="Попыток тестов нет." />
          <ActivityList title="Последние math-попытки" rows={mathAttempts} emptyText="Math-попыток нет." />
        </div>
      </div>
    </Layout>
  );
}
