import React from 'react';
import { Button, Card, Input, Textarea } from '../../../components/ui';
import {
  EmailRevealControl,
  MiniProfilePreview,
  ReadOnlyValue,
  formatDate,
  maskEmail,
  profileRole,
} from './SettingsPrimitives';

function ProfileSettingsSection({
  loading,
  profile,
  extra,
  profileId,
  profileDirty,
  profileLoginLooksOk,
  setProfileField,
  setExtraField,
  emailReveal,
  openPreview,
}) {
  if (loading && !profile) return <Card className="p-4 text-sm text-neutral-500 dark:text-neutral-400">Загрузка профиля…</Card>;
  if (!profile) return <Card className="p-4 text-sm text-neutral-500 dark:text-neutral-400">Профиль не загрузился.</Card>;

  return (
    <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_320px]">
      <div className="space-y-4">
        <Card className="p-4 space-y-5">
          <div className="font-semibold">Основные данные</div>
          <div className="grid gap-4 md:grid-cols-2">
            <div className="md:col-span-2">
              <label htmlFor="settings-profile-login" className="text-sm text-neutral-500 dark:text-neutral-400">Логин</label>
              <Input id="settings-profile-login" value={profile.login || ''} onChange={(event) => setProfileField('login', event.target.value)} autoComplete="username" placeholder="krytoichel" />
              {!profileLoginLooksOk ? <div className="mt-1 text-xs text-red-500">От 3 до 64 символов: латинские буквы, цифры, точка, дефис или подчёркивание.</div> : null}
            </div>
            <div><label htmlFor="settings-profile-first-name" className="text-sm text-neutral-500 dark:text-neutral-400">Имя</label><Input id="settings-profile-first-name" autoComplete="given-name" value={profile.firstName || ''} onChange={(event) => setProfileField('firstName', event.target.value)} /></div>
            <div><label htmlFor="settings-profile-last-name" className="text-sm text-neutral-500 dark:text-neutral-400">Фамилия</label><Input id="settings-profile-last-name" autoComplete="family-name" value={profile.lastName || ''} onChange={(event) => setProfileField('lastName', event.target.value)} /></div>
            <EmailRevealControl
              maskedEmail={profile.maskedEmail || maskEmail(profile.email)}
              revealedEmail={emailReveal.revealedEmail}
              password={emailReveal.password}
              open={emailReveal.open}
              loading={emailReveal.loading}
              error={emailReveal.error}
              onOpen={emailReveal.onOpen}
              onClose={emailReveal.onClose}
              onPasswordChange={emailReveal.onPasswordChange}
              onReveal={emailReveal.onReveal}
            />
            <div><label htmlFor="settings-profile-phone" className="text-sm text-neutral-500 dark:text-neutral-400">Телефон</label><Input id="settings-profile-phone" autoComplete="tel" placeholder="+375 (__) ___-__-__" value={profile.phoneNumber || ''} onChange={(event) => setProfileField('phoneNumber', event.target.value)} /></div>
            <div className="md:col-span-2"><label htmlFor="settings-profile-avatar" className="text-sm text-neutral-500 dark:text-neutral-400">Ссылка на аватар</label><Input id="settings-profile-avatar" type="url" placeholder="https://example.com/avatar.jpg" value={profile.profilePictureUrl || ''} onChange={(event) => setProfileField('profilePictureUrl', event.target.value)} /></div>
            <ReadOnlyValue label="Доступ" value={profileRole(profile)} />
          </div>
        </Card>

        <Card className="p-4 space-y-4">
          <div className="font-semibold">Публичный профиль</div>
          <div><label htmlFor="settings-profile-bio" className="text-sm text-neutral-500 dark:text-neutral-400">О себе</label><Textarea id="settings-profile-bio" rows={4} placeholder="Например: студент ИТ, люблю C#, делаю проекты на TaskForge…" value={extra.bio} onChange={(event) => setExtraField('bio', event.target.value)} /></div>
          <div className="grid gap-4 md:grid-cols-2">
            <div><label htmlFor="settings-profile-location" className="text-sm text-neutral-500 dark:text-neutral-400">Город / место учёбы</label><Input id="settings-profile-location" placeholder="Минск, БГУИР, ITD-21" value={extra.location} onChange={(event) => setExtraField('location', event.target.value)} /></div>
            <div><label htmlFor="settings-profile-education" className="text-sm text-neutral-500 dark:text-neutral-400">Образование / группа</label><Input id="settings-profile-education" placeholder="Факультет АИС, ITD-21" value={extra.education} onChange={(event) => setExtraField('education', event.target.value)} /></div>
          </div>
          <div><label htmlFor="settings-profile-skills" className="text-sm text-neutral-500 dark:text-neutral-400">Навыки</label><Input id="settings-profile-skills" placeholder="C#, C++, SQL, React" value={extra.skillsText} onChange={(event) => setExtraField('skillsText', event.target.value)} /></div>
        </Card>

        <Card className="p-4 space-y-4">
          <div className="font-semibold">Ссылки</div>
          <div className="grid gap-4 md:grid-cols-2">
            <div><label htmlFor="settings-profile-github" className="text-sm text-neutral-500 dark:text-neutral-400">GitHub</label><Input id="settings-profile-github" type="url" placeholder="https://github.com/..." value={extra.github} onChange={(event) => setExtraField('github', event.target.value)} /></div>
            <div><label htmlFor="settings-profile-telegram" className="text-sm text-neutral-500 dark:text-neutral-400">Telegram</label><Input id="settings-profile-telegram" placeholder="@username или https://t.me/username" value={extra.telegram} onChange={(event) => setExtraField('telegram', event.target.value)} /></div>
            <div className="md:col-span-2"><label htmlFor="settings-profile-website" className="text-sm text-neutral-500 dark:text-neutral-400">Личный сайт / портфолио</label><Input id="settings-profile-website" type="url" placeholder="https://..." value={extra.website} onChange={(event) => setExtraField('website', event.target.value)} /></div>
          </div>
        </Card>

        <Card className="p-4 flex items-center justify-between gap-4">
          <div><div className="font-semibold">Участие в рейтинге</div></div>
          <Button variant={extra.showInLeaderboard ? 'primary' : 'outline'} onClick={() => setExtraField('showInLeaderboard', !extra.showInLeaderboard)}>{extra.showInLeaderboard ? 'Включено' : 'Выключено'}</Button>
        </Card>
      </div>

      <aside className="space-y-4 xl:sticky xl:top-24 xl:self-start">
        <MiniProfilePreview profile={profile} extra={extra} />
        <Card className="p-4 space-y-2 text-sm text-neutral-500 dark:text-neutral-400">
          <div className="font-semibold text-neutral-900 dark:text-neutral-100">Аккаунт</div>
          <div>Логин: @{profile.login || '—'}</div>
          <div>Создан: {formatDate(profile.createdAt)}</div>
          <div>Последний вход: {formatDate(profile.lastLoginAt)}</div>
          {profileDirty ? <div className="text-[rgb(var(--accent))]">Есть несохранённые изменения</div> : null}
          <div className="pt-2"><Button variant="outline" disabled={!profileId} onClick={openPreview}>Предпросмотр</Button></div>
        </Card>
      </aside>
    </div>
  );
}

export default React.memo(ProfileSettingsSection);
