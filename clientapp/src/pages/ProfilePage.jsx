import React, { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import Layout from '../components/Layout';
import { Card, Button, Input, Textarea } from '../components/ui';
import { getProfile, updateProfile, changeEmail, changePassword } from '../api/profile';
import { getTelegramStatus, generateTelegramCode, unlinkTelegram } from '../api/telegramLink';
import { getMinecraftStatus, requestMinecraftLink, confirmMinecraftLink, unlinkMinecraft } from '../api/minecraftLink';
import { parseProfileExtra, buildProfileExtra } from '../utils/profileExtra';
import { useAuth } from '../auth/AuthContext';
import AppErrorPanel from '../components/AppErrorPanel';
import { handleApiError } from '../utils/handleApiError';
import { useNotify } from '../components/notify/NotifyProvider';

/**
 * Страница профиля для текущего пользователя.
 *
 * После сохранения мы не полагаемся на возвращаемое значение updateProfile,
 * потому что оно возвращает лишь булево значение. Вместо этого мы
 * обновляем локальное состояние теми же данными, что отправили на сервер,
 * чтобы поля в форме не очищались и пользователь видел актуальные значения.
 */
export default function ProfilePage() {
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState(null); 
  const [saved, setSaved] = useState(false);

  const [profile, setProfile] = useState(null);
  const [extra, setExtra] = useState(parseProfileExtra(null));

  // state for email change form
  const [emailForm, setEmailForm] = useState({ newEmail: '', password: '' });
  const [emailError, setEmailError] = useState(null);
  const [emailSuccess, setEmailSuccess] = useState(null);
  const [savingEmail, setSavingEmail] = useState(false);

  // state for password change form
  const [passwordForm, setPasswordForm] = useState({ currentPassword: '', newPassword: '', confirmNewPassword: '' });
  const [passwordError, setPasswordError] = useState(null);
  const [passwordSuccess, setPasswordSuccess] = useState(null);
  const [savingPassword, setSavingPassword] = useState(false);

  // Telegram link
  const [tgStatus, setTgStatus] = useState(null);
  const [tgCode, setTgCode] = useState(null);
  const [tgExpires, setTgExpires] = useState(null);
  const [tgLoading, setTgLoading] = useState(false);
  const [tgError, setTgError] = useState(null);

  // Minecraft link
  const [mcStatus, setMcStatus] = useState(null);
  const [mcNick, setMcNick] = useState('');
  const [mcGeneratedCode, setMcGeneratedCode] = useState('');
  const [mcInputCode, setMcInputCode] = useState('');
  const [mcExpires, setMcExpires] = useState(null);
  const [mcDelivery, setMcDelivery] = useState(null);
  const [mcLoading, setMcLoading] = useState(false);
  const [mcError, setMcError] = useState(null);

  // навигация для перехода после сохранения
  const navigate = useNavigate();
  const { refresh } = useAuth();
  const notify = useNotify();

  // Загрузка профиля при монтировании
  useEffect(() => {
    (async () => {
      try {
        setLoading(true);
        const data = await getProfile();
        setProfile(data);
        setExtra(parseProfileExtra(data.additionalDataJson));

        // Telegram status (не мешаем загрузке профиля)
        try {
          const st = await getTelegramStatus();
          setTgStatus(st);
        } catch {
          // ignore
        }

        // Minecraft status (не мешаем загрузке профиля)
        try {
          const st2 = await getMinecraftStatus();
          setMcStatus(st2);
          if (st2?.nick) setMcNick(st2.nick);
        } catch {
          // ignore
        }
      } catch (e) {
        const parsed = handleApiError(e, notify, 'Не удалось загрузить профиль');
        setError(parsed);
      } finally {
        setLoading(false);
      }
    })();
  }, []);

  const refreshTgStatus = async () => {
    try {
      const st = await getTelegramStatus();
      setTgStatus(st);
    } catch {
      // ignore
    }
  };

  const refreshMcStatus = async () => {
    try {
      const st = await getMinecraftStatus();
      setMcStatus(st);
      if (st?.nick) setMcNick(st.nick);
    } catch {
      // ignore
    }
  };

  const handleRequestMc = async () => {
    const nick = (mcNick || '').trim();
    if (!nick) {
      setMcError('Введи ник на сервере Minecraft.');
      return;
    }
    try {
      setMcLoading(true);
      setMcError(null);
      const dto = await requestMinecraftLink(nick);
      setMcInputCode('');
      setMcExpires(dto.expiresAtUtc);
      // Код прилетает в игре. На сайте показываем только как запасной вариант.
      setMcGeneratedCode(dto.code);
      setMcDelivery(dto.delivery || null);
      setMcStatus(dto.status);
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось отправить код привязки Minecraft');
      setMcError(parsed);
    } finally {
      setMcLoading(false);
    }
  };

  const handleConfirmMc = async () => {
    const code = (mcInputCode || '').trim();
    if (!code) {
      setMcError('Введи код, который пришёл тебе в игре.');
      return;
    }
    try {
      setMcLoading(true);
      setMcError(null);
      const st = await confirmMinecraftLink(code);
      setMcStatus(st);
      // очищаем, чтобы не светить код
      setMcInputCode('');
      setMcExpires(null);
      setMcDelivery(null);
      try { await refresh(); } catch {}
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось подтвердить код Minecraft');
      setMcError(parsed);
    } finally {
      setMcLoading(false);
    }
  };

  const handleUnlinkMc = async () => {
    if (!window.confirm('Отвязать Minecraft от аккаунта?')) return;
    try {
      setMcLoading(true);
      setMcError(null);
      await unlinkMinecraft();
      setMcGeneratedCode('');
      setMcInputCode('');
      setMcExpires(null);
      setMcDelivery(null);
      try { await refresh(); } catch {}
      await refreshMcStatus();
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось отвязать Minecraft');
      setMcError(parsed);
    } finally {
      setMcLoading(false);
    }
  };

  const handleCopyMcCode = async () => {
    if (!mcGeneratedCode) return;
    try {
      await navigator.clipboard.writeText(mcGeneratedCode);
    } catch {
      // ignore
    }
  };

  const handleGenerateTgCode = async () => {
    try {
      setTgLoading(true);
      setTgError(null);
      const dto = await generateTelegramCode();
      setTgCode(dto.code);
      setTgExpires(dto.expiresAtUtc);
      setTgStatus(dto.status);
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось сгенерировать код Telegram');
      setTgError(parsed);
    } finally {
      setTgLoading(false);
    }
  };

  const handleCopyTgCode = async () => {
    if (!tgCode) return;
    try {
      await navigator.clipboard.writeText(tgCode);
    } catch {
      // ignore
    }
  };

  const handleUnlinkTg = async () => {
    if (!window.confirm('Отвязать Telegram от аккаунта?')) return;
    try {
      setTgLoading(true);
      setTgError(null);
      await unlinkTelegram();
      setTgCode(null);
      setTgExpires(null);
      await refreshTgStatus();
    } catch (e) {
      const parsed = handleApiError(e, notify, 'Не удалось отвязать Telegram');
      setTgError(parsed);
    } finally {
      setTgLoading(false);
    }
  };

  // Универсальный обработчик изменений дополнительных полей
  const handleChangeExtra = (field) => (eOrValue) => {
    const value =
      eOrValue && eOrValue.target !== undefined
        ? eOrValue.target.type === 'checkbox'
          ? eOrValue.target.checked
          : eOrValue.target.value
        : eOrValue;
    setExtra((prev) => ({
      ...prev,
      [field]: value,
    }));
  };

  // Сохранение профиля
  const handleSave = async (e) => {
    e.preventDefault();
    if (!profile) return;
    setSaving(true);
    setError(null);
    setSaved(false);
    try {
      // DTO для отправки
      const dto = {
        ...profile,
        additionalDataJson: buildProfileExtra(extra),
      };
      // Выполняем запрос на обновление; возвращаемый ответ — булево
      await updateProfile(dto);
      // Обновляем локальное состояние теми же данными
      setProfile(dto);
      setExtra(parseProfileExtra(dto.additionalDataJson));
      setSaved(true);

      // После успешного сохранения перенаправляем на главную страницу,
      // чтобы форма не выглядела пустой и пользователь вернулся к задачам.
      navigate('/', { replace: true });
    } catch (err) {
      const parsed = handleApiError(err, notify, 'Не удалось сохранить профиль');
      setError(parsed);
    } finally {
      setSaving(false);
    }
  };

  // Handler for email change submission
  const handleEmailSubmit = async (e) => {
    e.preventDefault();
    const { newEmail, password } = emailForm;
    if (!newEmail.trim() || !password) return;
    setSavingEmail(true);
    setEmailError(null);
    setEmailSuccess(null);
    try {
      await changeEmail(newEmail.trim(), password);
      // update local profile email so it reflects the change immediately
      setProfile((p) => (p ? { ...p, email: newEmail.trim() } : p));
      setEmailSuccess('Email обновлён. Подтвердите новый адрес, если требуется.');
      setEmailForm({ newEmail: '', password: '' });
    } catch (err) {
      const parsed = handleApiError(err, notify, 'Не удалось обновить email');
      setEmailError(parsed);
    } finally {
      setSavingEmail(false);
    }
  };

  // Handler for password change submission
  const handlePasswordSubmit = async (e) => {
    e.preventDefault();
    const { currentPassword, newPassword, confirmNewPassword } = passwordForm;
    if (!currentPassword || !newPassword || newPassword !== confirmNewPassword) return;
    setSavingPassword(true);
    setPasswordError(null);
    setPasswordSuccess(null);
    try {
      await changePassword(currentPassword, newPassword);
      setPasswordSuccess('Пароль изменён.');
      setPasswordForm({ currentPassword: '', newPassword: '', confirmNewPassword: '' });
    } catch (err) {
      const parsed = handleApiError(err, notify, 'Не удалось изменить пароль');
      setPasswordError(parsed);
    } finally {
      setSavingPassword(false);
    }
  };

  return (
    <Layout>
      <div className="max-w-3xl mx-auto space-y-6">
        <h1 className="text-2xl font-semibold">Профиль</h1>
        {loading && <div>Загрузка…</div>}
        {error ? <AppErrorPanel error={error} title="Проблема с профилем" /> : null}
        {saved && (
          <div className="text-sm text-emerald-600 bg-emerald-50 dark:bg-emerald-900/20 px-3 py-2 rounded-xl">
            Профиль сохранён
          </div>
        )}
        {profile && (
          <>
          <Card className="p-4">
            <div className="text-sm text-neutral-600 dark:text-neutral-300 flex flex-wrap gap-x-6 gap-y-2">
              <div>Рейтинг: <span className="font-semibold">{profile.score ?? 0}</span></div>
              <div>Решено задач: <span className="font-semibold">{profile.solvedAssignments ?? 0}</span></div>
            </div>
          </Card>
          <form onSubmit={handleSave} className="space-y-6">
            {/* Основное */}
            <Card className="p-4 space-y-4">
              <h2 className="font-semibold">Основное</h2>
              {/*
                Размещаем основные поля пользователя в сетке. Помимо имени и
                фамилии, сюда добавлены телефон и ссылка на аватар. Email
                остаётся только для просмотра, поскольку его изменение
                требует отдельного подтверждения на бэке.
              */}
              <div className="grid gap-4 md:grid-cols-2">
                {/* Имя */}
                <div>
                  <label className="text-sm text-neutral-500">Имя</label>
                  <Input
                    value={profile.firstName || ''}
                    onChange={(e) =>
                      setProfile((p) => ({ ...p, firstName: e.target.value }))
                    }
                  />
                </div>
                {/* Фамилия */}
                <div>
                  <label className="text-sm text-neutral-500">Фамилия</label>
                  <Input
                    value={profile.lastName || ''}
                    onChange={(e) =>
                      setProfile((p) => ({ ...p, lastName: e.target.value }))
                    }
                  />
                </div>
                {/* Email (для просмотра) */}
                <div>
                  <label className="text-sm text-neutral-500">Email</label>
                  <Input type="email" value={profile.email || ''} disabled />
                </div>
                {/* Телефон */}
                <div>
                  <label className="text-sm text-neutral-500">Телефон</label>
                  <Input
                    placeholder="+375 (__) ___-__-__"
                    value={profile.phoneNumber || ''}
                    onChange={(e) =>
                      setProfile((p) => ({ ...p, phoneNumber: e.target.value }))
                    }
                  />
                </div>
                {/* Ссылка на аватар */}
                <div className="md:col-span-2">
                  <label className="text-sm text-neutral-500">Ссылка на аватар</label>
                  <Input
                    placeholder="https://example.com/avatar.jpg"
                    value={profile.profilePictureUrl || ''}
                    onChange={(e) =>
                      setProfile((p) => ({ ...p, profilePictureUrl: e.target.value }))
                    }
                  />
                </div>
              </div>
            </Card>
            {/* О себе */}
            <Card className="p-4 space-y-4">
              <h2 className="font-semibold">О себе</h2>
              <div>
                <label className="text-sm text-neutral-500">Краткое описание</label>
                <Textarea
                  rows={4}
                  placeholder="Например: студент ИТ, люблю C#, делаю проекты на TaskForge…"
                  value={extra.bio}
                  onChange={handleChangeExtra('bio')}
                />
              </div>
              <div className="grid gap-4 md:grid-cols-2">
                <div>
                  <label className="text-sm text-neutral-500">Город / место учёбы</label>
                  <Input
                    placeholder="Минск, БГУИР, ITD-21"
                    value={extra.location}
                    onChange={handleChangeExtra('location')}
                  />
                </div>
                <div>
                  <label className="text-sm text-neutral-500">Образование / группа</label>
                  <Input
                    placeholder="Факультет АИС, ITD-21"
                    value={extra.education}
                    onChange={handleChangeExtra('education')}
                  />
                </div>
              </div>
              <div>
                <label className="text-sm text-neutral-500">Навыки</label>
                <Input
                  placeholder="C#, C++, SQL, React"
                  value={extra.skillsText}
                  onChange={handleChangeExtra('skillsText')}
                />
                <p className="mt-1 text-xs text-neutral-400">
                  Перечисли через запятую — они будут показаны в профиле и топе.
                </p>
              </div>
            </Card>
            {/* Ссылки */}
            <Card className="p-4 space-y-4">
              <h2 className="font-semibold">Ссылки</h2>
              <div className="space-y-3">
                <div>
                  <label className="text-sm text-neutral-500">GitHub</label>
                  <Input
                    placeholder="https://github.com/..."
                    value={extra.github}
                    onChange={handleChangeExtra('github')}
                  />
                </div>
                <div>
                  <label className="text-sm text-neutral-500">Личный сайт / портфолио</label>
                  <Input
                    placeholder="https://..."
                    value={extra.website}
                    onChange={handleChangeExtra('website')}
                  />
                </div>
              </div>
            </Card>

            {/* Telegram */}
            <Card className="p-4 space-y-3">
              <div className="flex items-center justify-between gap-3">
                <h2 className="font-semibold">Telegram</h2>
                {tgStatus && (
                  <div className="text-xs text-neutral-500">
                    Привязки: {tgStatus.linkCount ?? 0}/2
                  </div>
                )}
              </div>

              {tgError ? <AppErrorPanel error={tgError} title="Проблема с Telegram" compact /> : null}

              {tgStatus?.linked ? (
                <div className="space-y-2">
                  <div className="text-sm">
                    ✅ Привязан: <b>{tgStatus.username || 'Telegram'}</b>
                  </div>
                  <div className="flex gap-2 flex-wrap">
                    <Button type="button" variant="secondary" onClick={handleUnlinkTg} disabled={tgLoading}>
                      Удалить привязку
                    </Button>
                    <Button type="button" variant="ghost" onClick={refreshTgStatus} disabled={tgLoading}>
                      Обновить
                    </Button>
                  </div>
                </div>
              ) : (
                <div className="space-y-2">
                  <div className="text-sm text-neutral-600 dark:text-neutral-300">
                    Нажми «Сгенерировать код», потом отправь этот код боту{' '}
                    <b>{tgStatus?.botUsername || ''}</b> в личку.
                    После ответа бота нажми «Обновить».
                  </div>

                  {tgCode && (
                    <div className="rounded-2xl border border-neutral-200 dark:border-neutral-800 p-3">
                      <div className="text-xs text-neutral-500">Твой код</div>
                      <div className="mt-1 font-mono text-lg tracking-wider">{tgCode}</div>
                      {tgExpires && (
                        <div className="mt-1 text-xs text-neutral-500">
                          Действует до: {new Date(tgExpires).toLocaleString()}
                        </div>
                      )}
                      <div className="mt-2 flex gap-2 flex-wrap">
                        <Button type="button" variant="secondary" onClick={handleCopyTgCode}>
                          Копировать
                        </Button>
                        <Button type="button" variant="ghost" onClick={refreshTgStatus} disabled={tgLoading}>
                          Обновить
                        </Button>
                      </div>
                    </div>
                  )}

                  <div className="flex gap-2 flex-wrap">
                    <Button type="button" onClick={handleGenerateTgCode} disabled={tgLoading}>
                      {tgLoading ? 'Генерация…' : 'Сгенерировать код'}
                    </Button>
                    <Button type="button" variant="ghost" onClick={refreshTgStatus} disabled={tgLoading}>
                      Обновить
                    </Button>
                  </div>
                </div>
              )}
            </Card>

            {/* Minecraft */}
            <Card className="p-4 space-y-3">
              <div className="flex items-center justify-between gap-3">
                <h2 className="font-semibold">Minecraft</h2>
                {mcStatus && (
                  <div className="text-xs text-neutral-500">
                    Привязки: {mcStatus.linkCount ?? 0}/2
                  </div>
                )}
              </div>

              {mcError ? <AppErrorPanel error={mcError} title="Проблема с Minecraft" compact /> : null}

              {mcStatus?.linked ? (
                <div className="space-y-2">
                  <div className="text-sm">
                    ✅ Привязан ник: <b>{mcStatus.nick}</b>
                    {mcStatus.uuid ? <span className="text-xs text-neutral-500"> (uuid: {mcStatus.uuid})</span> : null}
                  </div>
                  <div className="text-xs text-neutral-500">
                    Штраф за неделю входа на сервер считается на стороне TaskForge. Если у игрока не хватает рейтинга —
                    на сервере будут дебафы (замедление / слепота / замедление копания).
                  </div>
                  <div className="flex gap-2 flex-wrap">
                    <Button type="button" variant="secondary" onClick={handleUnlinkMc} disabled={mcLoading}>
                      Удалить привязку
                    </Button>
                    <Button type="button" variant="ghost" onClick={refreshMcStatus} disabled={mcLoading}>
                      Обновить
                    </Button>
                  </div>
                </div>
              ) : (
                <div className="space-y-3">
                  <div className="text-sm text-neutral-600 dark:text-neutral-300">
                    1) Введи свой ник на сервере Minecraft.
                    <br />
                    2) Нажми «Отправить код в игру». Игрок должен быть <b>онлайн</b>.
                    <br />
                    3) Код придёт тебе в ЛС в игре. Введи его ниже и нажми «Подтвердить».
                  </div>

                  <div className="space-y-2">
                    <label className="text-sm text-neutral-500">Ник</label>
                    <Input
                      placeholder="Player_123"
                      value={mcNick}
                      onChange={(e) => setMcNick(e.target.value)}
                      disabled={mcLoading}
                    />
                  </div>

                  <div className="flex gap-2 flex-wrap">
                    <Button type="button" onClick={handleRequestMc} disabled={mcLoading}>
                      {mcLoading ? 'Отправка…' : 'Отправить код в игру'}
                    </Button>
                    <Button type="button" variant="ghost" onClick={refreshMcStatus} disabled={mcLoading}>
                      Обновить
                    </Button>
                  </div>

                  <div className="space-y-2">
                    <label className="text-sm text-neutral-500">Код</label>
                    <Input
                      placeholder="ABCD-EFGH"
                      value={mcInputCode}
                      onChange={(e) => setMcInputCode(e.target.value)}
                      disabled={mcLoading}
                    />
                    {mcExpires && (
                      <div className="text-xs text-neutral-500">
                        Код действует до: {new Date(mcExpires).toLocaleString()}
                      </div>
                    )}

                    {mcStatus?.linked && (
                      <div className="text-xs text-neutral-500">
                        <div>
                          Рейтинг (с учётом штрафов):{' '}
                          <span className={"font-semibold " + (mcStatus.debuffed ? 'text-amber-600 dark:text-amber-300' : 'text-neutral-700 dark:text-neutral-200')}>
                            {mcStatus.effectiveScore}
                          </span>
                          <span className="text-neutral-500 dark:text-neutral-400"> (база {mcStatus.score}, штраф {mcStatus.penaltyTotal})</span>
                        </div>
                        <div className="text-neutral-500 dark:text-neutral-400">
                          Штраф за первую неделю входа: {mcStatus.weeklyPenaltyCurrent}
                        </div>
                        {mcStatus.debuffed && (
                          <div className="text-amber-700 dark:text-amber-300">
                            На сервере будут постоянные дебафы, пока эффективный рейтинг &lt; 0.
                          </div>
                        )}
                      </div>
                    )}

                    {mcDelivery && (
                      <div className={`text-xs px-3 py-2 rounded-xl ${mcDelivery.delivered ? 'text-emerald-700 bg-emerald-50 dark:bg-emerald-900/20' : 'text-amber-700 bg-amber-50 dark:bg-amber-900/20'}`}>
                        {mcDelivery.attempted ? (
                          mcDelivery.delivered
                            ? '✅ Код отправлен в игру. Проверь личные сообщения в Minecraft.'
                            : `⚠️ Не удалось доставить код в игру: ${mcDelivery.message}`
                        ) : (
                          '⚠️ Плагин Minecraft ещё не настроен. Код можно ввести вручную (см. ниже).'
                        )}
                      </div>
                    )}

                    {!!mcGeneratedCode && (
                      <div className="text-xs text-neutral-500">
                        Запасной вариант (если код не дошёл): <b>{mcGeneratedCode}</b>
                      </div>
                    )}

                    <div className="flex gap-2 flex-wrap">
                      <Button type="button" variant="secondary" onClick={handleConfirmMc} disabled={mcLoading}>
                        Подтвердить
                      </Button>
                      <Button type="button" variant="ghost" onClick={handleCopyMcCode} disabled={!mcGeneratedCode}>
                        Копировать запасной код
                      </Button>
                    </div>
                    <div className="text-xs text-neutral-500">
                      (Если код не пришёл в игру — значит плагин ещё не установлен или игрок был оффлайн.)
                    </div>
                  </div>
                </div>
              )}
            </Card>
            {/* Переключатель показа в топе */}
            <Card className="p-4 flex items-center justify-between gap-4">
              <div>
                <div className="font-medium">Показывать меня в топе</div>
                <div className="text-xs text-neutral-500">
                  Если выключить, профиль не будет отображаться в общем рейтинге.
                </div>
              </div>
              <label className="inline-flex items-center gap-2 cursor-pointer">
                <input
                  type="checkbox"
                  className="h-4 w-4 rounded border-neutral-300 text-brand-600 focus:ring-brand-500"
                  checked={extra.showInLeaderboard}
                  onChange={handleChangeExtra('showInLeaderboard')}
                />
                <span className="text-sm text-neutral-700 dark:text-neutral-200">Включено</span>
              </label>
            </Card>
            <div className="flex justify-end">
              <Button type="submit" disabled={saving}>
                {saving ? 'Сохранение…' : 'Сохранить'}
              </Button>
            </div>
          </form>

          {/* Change Email Section */}
          <Card className="p-4 space-y-4 mt-6">
            <h2 className="font-semibold">Изменить email</h2>
            {emailError && (
              <div className="text-sm text-red-500 bg-red-50 dark:bg-red-900/20 px-3 py-2 rounded-xl">
                {emailError}
              </div>
            )}
            {emailSuccess && (
              <div className="text-sm text-emerald-600 bg-emerald-50 dark:bg-emerald-900/20 px-3 py-2 rounded-xl">
                {emailSuccess}
              </div>
            )}
            <form onSubmit={handleEmailSubmit} className="space-y-4">
              <div>
                <label className="text-sm text-neutral-500">Новый email</label>
                <Input
                  type="email"
                  value={emailForm.newEmail}
                  onChange={(e) =>
                    setEmailForm((f) => ({ ...f, newEmail: e.target.value }))
                  }
                  placeholder="you@example.com"
                />
              </div>
              <div>
                <label className="text-sm text-neutral-500">Текущий пароль</label>
                <Input
                  type="password"
                  value={emailForm.password}
                  onChange={(e) =>
                    setEmailForm((f) => ({ ...f, password: e.target.value }))
                  }
                />
              </div>
              <div className="flex justify-end">
                <Button type="submit" disabled={savingEmail || !emailForm.newEmail || !emailForm.password}>
                  {savingEmail ? 'Сохранение…' : 'Сменить email'}
                </Button>
              </div>
            </form>
          </Card>

          {/* Change Password Section */}
          <Card className="p-4 space-y-4 mt-6">
            <h2 className="font-semibold">Изменить пароль</h2>
            {passwordError && (
              <div className="text-sm text-red-500 bg-red-50 dark:bg-red-900/20 px-3 py-2 rounded-xl">
                {passwordError}
              </div>
            )}
            {passwordSuccess && (
              <div className="text-sm text-emerald-600 bg-emerald-50 dark:bg-emerald-900/20 px-3 py-2 rounded-xl">
                {passwordSuccess}
              </div>
            )}
            <form onSubmit={handlePasswordSubmit} className="space-y-4">
              <div>
                <label className="text-sm text-neutral-500">Текущий пароль</label>
                <Input
                  type="password"
                  value={passwordForm.currentPassword}
                  onChange={(e) =>
                    setPasswordForm((f) => ({ ...f, currentPassword: e.target.value }))
                  }
                />
              </div>
              <div>
                <label className="text-sm text-neutral-500">Новый пароль</label>
                <Input
                  type="password"
                  value={passwordForm.newPassword}
                  onChange={(e) =>
                    setPasswordForm((f) => ({ ...f, newPassword: e.target.value }))
                  }
                />
              </div>
              <div>
                <label className="text-sm text-neutral-500">Повторите новый пароль</label>
                <Input
                  type="password"
                  value={passwordForm.confirmNewPassword}
                  onChange={(e) =>
                    setPasswordForm((f) => ({ ...f, confirmNewPassword: e.target.value }))
                  }
                />
              </div>
              <div className="flex justify-end">
                <Button
                  type="submit"
                  disabled={
                    savingPassword ||
                    !passwordForm.currentPassword ||
                    !passwordForm.newPassword ||
                    passwordForm.newPassword !== passwordForm.confirmNewPassword
                  }
                >
                  {savingPassword ? 'Сохранение…' : 'Сменить пароль'}
                </Button>
              </div>
            </form>
          </Card>
          </>
        )}
      </div>
    </Layout>
  );
}