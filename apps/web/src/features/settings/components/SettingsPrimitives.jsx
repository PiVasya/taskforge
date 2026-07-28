import React from 'react';
import { Eye, EyeOff, LockKeyhole } from 'lucide-react';
import { Badge, Button, Card, Input } from '../../../components/ui';

export function maskEmail(email) {
  const value = String(email || '').trim();
  if (!value || !value.includes('@')) return 'Почта не указана';
  const [name, domain] = value.split('@');
  const nameMask = name.length <= 2 ? `${name.slice(0, 1)}***` : `${name.slice(0, 2)}***`;
  const parts = domain.split('.');
  const zone = parts.length > 1 ? parts.pop() : '';
  const domainName = parts.join('.') || domain;
  const domainMask = domainName.length <= 2 ? `${domainName.slice(0, 1)}***` : `${domainName.slice(0, 2)}***`;
  return zone ? `${nameMask}@${domainMask}.${zone}` : `${nameMask}@${domainMask}`;
}

export function formatDate(value) {
  if (!value) return 'не указано';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? 'не указано' : date.toLocaleDateString('ru-RU');
}

export function profileRole(profile) {
  const roles = Array.isArray(profile?.roles) ? profile.roles.filter(Boolean) : [];
  return roles.length ? roles.join(', ') : profile?.role || 'User';
}

export function displayName(profile) {
  const first = String(profile?.firstName || '').trim();
  const last = String(profile?.lastName || '').trim();
  const full = [last, first].filter(Boolean).join(' ');
  if (full) return full;
  const login = String(profile?.login || '').trim();
  if (login) return login;
  const email = String(profile?.email || '').trim();
  return email ? maskEmail(email) : 'Пользователь';
}

function initials(profile) {
  const first = String(profile?.firstName || '').trim();
  const last = String(profile?.lastName || '').trim();
  const value = `${last ? last[0] : ''}${first ? first[0] : ''}`.trim();
  if (value) return value.toUpperCase();
  const login = String(profile?.login || '').trim();
  if (login) return login[0].toUpperCase();
  const email = String(profile?.email || '').trim();
  return email ? email[0].toUpperCase() : 'TF';
}

export const ChoiceButton = React.memo(function ChoiceButton({ active, title, desc, onClick, disabled = false }) {
  return (
    <button
      type="button"
      disabled={disabled}
      onClick={disabled ? undefined : onClick}
      className={`rounded-2xl border px-4 py-3 text-left transition ${disabled
        ? 'cursor-not-allowed border-[rgba(var(--border)/0.42)] bg-[rgba(var(--card)/0.32)] opacity-50'
        : active
          ? 'border-[rgb(var(--accent))] bg-[rgba(var(--accent)/0.12)] shadow-[0_0_0_1px_rgba(var(--accent)/0.18)]'
          : 'border-[rgba(var(--border)/0.72)] bg-[rgba(var(--card)/0.58)] hover:bg-[rgba(var(--card)/0.86)]'}`}
    >
      <div className="font-semibold leading-snug">{title}</div>
      {desc ? <div className="mt-1 text-sm leading-snug text-neutral-500 dark:text-neutral-400">{desc}</div> : null}
    </button>
  );
});

export const ReadOnlyValue = React.memo(function ReadOnlyValue({ label, value, hint }) {
  return (
    <div>
      <label className="text-sm text-neutral-500 dark:text-neutral-400">{label}</label>
      <div className="mt-1 rounded-xl border border-[rgba(var(--border)/0.65)] bg-neutral-100/60 px-3 py-2 text-neutral-500 dark:bg-neutral-950/35 dark:text-neutral-400">{value}</div>
      {hint ? <div className="mt-1 text-xs text-neutral-500 dark:text-neutral-400">{hint}</div> : null}
    </div>
  );
});

export const EmailRevealControl = React.memo(function EmailRevealControl({
  maskedEmail,
  revealedEmail,
  password,
  open,
  loading,
  error,
  onOpen,
  onClose,
  onPasswordChange,
  onReveal,
}) {
  return (
    <div>
      <label className="text-sm text-neutral-500 dark:text-neutral-400">Почта</label>
      <div className="mt-1 rounded-xl border border-[rgba(var(--border)/0.65)] bg-neutral-100/60 px-3 py-2 dark:bg-neutral-950/35">
        <div className="flex items-center gap-2">
          <span className="min-w-0 flex-1 truncate text-neutral-600 dark:text-neutral-300">{revealedEmail || maskedEmail}</span>
          <button
            type="button"
            className="inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-xl border border-[rgba(var(--border)/0.65)] hover:bg-[rgba(var(--border)/0.18)]"
            onClick={revealedEmail || open ? onClose : onOpen}
            title={revealedEmail || open ? 'Скрыть email' : 'Показать email'}
          >
            {revealedEmail || open ? <EyeOff size={16} /> : <Eye size={16} />}
          </button>
        </div>
        {open && !revealedEmail ? (
          <div className="mt-3 rounded-2xl border border-[rgba(var(--border)/0.55)] bg-[rgba(var(--card)/0.55)] p-3">
            <div className="mb-2 flex items-center gap-2 text-xs text-neutral-500 dark:text-neutral-400">
              <LockKeyhole size={14} />
              <span>Текущий пароль</span>
            </div>
            <div className="flex flex-col gap-2 sm:flex-row">
              <Input
                type="password"
                value={password}
                placeholder="Текущий пароль"
                onChange={(event) => onPasswordChange(event.target.value)}
                onKeyDown={(event) => {
                  if (event.key === 'Enter') {
                    event.preventDefault();
                    onReveal();
                  }
                }}
              />
              <Button type="button" onClick={onReveal} disabled={loading || !password}>
                {loading ? 'Проверка…' : 'Показать'}
              </Button>
            </div>
            {error ? <div className="mt-2 text-xs text-red-300">{typeof error === 'string' ? error : error?.primaryMessage || error?.userMessage || 'Не удалось раскрыть email'}</div> : null}
          </div>
        ) : null}
      </div>
    </div>
  );
});

export function InlineError({ value }) {
  if (!value) return null;
  return (
    <div className="rounded-2xl border border-red-400/40 bg-red-500/10 px-3 py-2 text-sm text-red-300">
      {typeof value === 'string' ? value : value?.primaryMessage || value?.userMessage || 'Не удалось выполнить действие'}
    </div>
  );
}

export const MiniProfilePreview = React.memo(function MiniProfilePreview({ profile, extra, compact = false }) {
  const name = displayName(profile);
  const avatarUrl = profile?.profilePictureUrl || profile?.avatarUrl || '';
  const skills = String(extra?.skillsText || '').split(',').map((item) => item.trim()).filter(Boolean).slice(0, compact ? 3 : 8);

  return (
    <Card className={compact ? 'p-3 space-y-3' : 'p-5 space-y-5'}>
      <div className="flex items-center gap-3">
        <div className={compact
          ? 'h-12 w-12 shrink-0 overflow-hidden rounded-2xl grid place-items-center bg-[rgba(var(--accent)/0.18)] text-[rgb(var(--accent))] font-semibold'
          : 'h-20 w-20 shrink-0 overflow-hidden rounded-3xl grid place-items-center bg-[rgba(var(--accent)/0.18)] text-[rgb(var(--accent))] text-2xl font-semibold'}>
          {avatarUrl ? <img src={avatarUrl} alt={name} className="h-full w-full object-cover" /> : <span>{initials(profile)}</span>}
        </div>
        <div className="min-w-0">
          <div className={compact ? 'truncate font-semibold' : 'truncate text-xl font-semibold'}>{name}</div>
          <div className="mt-1 truncate text-xs text-neutral-500 dark:text-neutral-400">@{profile?.login || 'login'}</div>
          <div className="mt-2 inline-flex rounded-full border border-[rgba(var(--border)/0.65)] px-2 py-1 text-xs text-neutral-500 dark:text-neutral-400">{profileRole(profile)}</div>
        </div>
      </div>
      {!compact ? (
        <>
          {extra?.bio ? <p className="whitespace-pre-line text-sm leading-relaxed text-neutral-700 dark:text-neutral-300">{extra.bio}</p> : <p className="text-sm text-neutral-500 dark:text-neutral-400">Добавь короткое описание профиля.</p>}
          {skills.length ? <div className="flex flex-wrap gap-2">{skills.map((skill) => <Badge key={skill}>{skill}</Badge>)}</div> : null}
        </>
      ) : null}
    </Card>
  );
});
