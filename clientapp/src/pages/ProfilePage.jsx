import React, { useEffect, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import Layout from '../components/Layout';
import { Card, Button, Input, Textarea } from '../components/ui';
import { getProfile, updateProfile, changeEmail, changePassword } from '../api/profile';
import { parseProfileExtra, buildProfileExtra } from '../utils/profileExtra';

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

  // навигация для перехода после сохранения
  const navigate = useNavigate();

  // Загрузка профиля при монтировании
  useEffect(() => {
    (async () => {
      try {
        setLoading(true);
        const data = await getProfile();
        setProfile(data);
        setExtra(parseProfileExtra(data.additionalDataJson));
      } catch (e) {
        console.error(e);
        setError('Не удалось загрузить профиль');
      } finally {
        setLoading(false);
      }
    })();
  }, []);

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
      console.error(err);
      setError('Не удалось сохранить профиль');
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
      setEmailError(
        err?.response?.data?.message ||
          err?.response?.data?.title ||
          err?.message ||
          'Не удалось обновить email'
      );
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
      setPasswordError(
        err?.response?.data?.message ||
          err?.response?.data?.title ||
          err?.message ||
          'Не удалось изменить пароль'
      );
    } finally {
      setSavingPassword(false);
    }
  };

  return (
    <Layout>
      <div className="max-w-3xl mx-auto space-y-6">
        <h1 className="text-2xl font-semibold">Профиль</h1>
        {loading && <div>Загрузка…</div>}
        {error && (
          <div className="text-sm text-red-500 bg-red-50 dark:bg-red-900/20 px-3 py-2 rounded-xl">
            {error}
          </div>
        )}
        {saved && (
          <div className="text-sm text-emerald-600 bg-emerald-50 dark:bg-emerald-900/20 px-3 py-2 rounded-xl">
            Профиль сохранён
          </div>
        )}
        {profile && (
          <>
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
                  <label className="text-sm text-neutral-500">Telegram</label>
                  <Input
                    placeholder="@ник или ссылка"
                    value={extra.telegram}
                    onChange={handleChangeExtra('telegram')}
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