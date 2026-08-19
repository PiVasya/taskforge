import React from 'react';
import { Button, Card, Input } from '../../../components/ui';
import { InlineError, maskEmail } from './SettingsPrimitives';

function SecuritySettingsSection({ profile, email, password }) {
  return (
    <div className="space-y-4">
      <Card className="p-4 space-y-4">
        <div><div className="font-semibold">Смена email</div><div className="text-sm text-neutral-500 dark:text-neutral-400">Текущий email скрыт: {profile?.maskedEmail || maskEmail(profile?.email)}</div></div>
        <InlineError value={email.error} />
        <form onSubmit={email.submit} className="space-y-4">
          <div><label htmlFor="settings-security-email" className="text-sm text-neutral-500 dark:text-neutral-400">Новый email</label><Input id="settings-security-email" type="email" autoComplete="email" value={email.form.newEmail} onChange={(event) => email.setForm((value) => ({ ...value, newEmail: event.target.value }))} placeholder="you@example.com" /></div>
          <div><label htmlFor="settings-security-email-password" className="text-sm text-neutral-500 dark:text-neutral-400">Текущий пароль</label><Input id="settings-security-email-password" type="password" autoComplete="current-password" value={email.form.password} onChange={(event) => email.setForm((value) => ({ ...value, password: event.target.value }))} /></div>
          <div className="flex justify-end"><Button type="submit" disabled={email.saving || !email.form.newEmail || !email.form.password}>{email.saving ? 'Сохранение…' : 'Сменить email'}</Button></div>
        </form>
      </Card>
      <Card className="p-4 space-y-4">
        <div><div className="font-semibold">Смена пароля</div><div className="text-sm text-neutral-500 dark:text-neutral-400">После смены используй новый пароль при следующем входе.</div></div>
        <InlineError value={password.error} />
        <form onSubmit={password.submit} className="space-y-4">
          <div><label htmlFor="settings-security-current-password" className="text-sm text-neutral-500 dark:text-neutral-400">Текущий пароль</label><Input id="settings-security-current-password" type="password" autoComplete="current-password" value={password.form.currentPassword} onChange={(event) => password.setForm((value) => ({ ...value, currentPassword: event.target.value }))} /></div>
          <div className="grid gap-4 md:grid-cols-2">
            <div><label htmlFor="settings-security-new-password" className="text-sm text-neutral-500 dark:text-neutral-400">Новый пароль</label><Input id="settings-security-new-password" type="password" autoComplete="new-password" value={password.form.newPassword} onChange={(event) => password.setForm((value) => ({ ...value, newPassword: event.target.value }))} /></div>
            <div><label htmlFor="settings-security-confirm-password" className="text-sm text-neutral-500 dark:text-neutral-400">Повторите новый пароль</label><Input id="settings-security-confirm-password" type="password" autoComplete="new-password" value={password.form.confirmNewPassword} onChange={(event) => password.setForm((value) => ({ ...value, confirmNewPassword: event.target.value }))} /></div>
          </div>
          <div className="flex justify-end"><Button type="submit" disabled={password.saving || !password.form.currentPassword || !password.form.newPassword || password.form.newPassword !== password.form.confirmNewPassword}>{password.saving ? 'Сохранение…' : 'Сменить пароль'}</Button></div>
        </form>
      </Card>
    </div>
  );
}

export default React.memo(SecuritySettingsSection);
