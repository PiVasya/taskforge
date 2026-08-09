import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { Button, Card } from '../../components/ui';
import { useNotify } from '../../components/notify/NotifyProvider';
import { useAuth } from '../../auth/AuthContext';
import { useUiSettingsActions } from '../../contexts/UiSettingsContext';
import { getProfile, updateProfile, changeEmail, changePassword, revealEmail } from '../../api/profile';
import { getTelegramStatus, generateTelegramCode, unlinkTelegram } from '../../api/telegramLink';
import { getMinecraftStatus, requestMinecraftLink, confirmMinecraftLink, unlinkMinecraft } from '../../api/minecraftLink';
import { getMyUiSettings, saveMyUiSettings } from '../../api/uiSettings';
import { handleApiError } from '../../utils/handleApiError';
import { parseProfileExtra, buildProfileExtra } from '../../utils/profileExtra';
import useQuery from '../../hooks/useQuery';
import { useQueryClient } from '../../data/QueryClientProvider';
import {
  defaultUiSettings,
  FX_OPTIONS,
  LOGIN_RE,
  SETTINGS_SECTIONS,
  SETTINGS_SECTION_KEYS,
} from './settingsModel';
import { displayName, MiniProfilePreview } from './components/SettingsPrimitives';
import AppearanceSettingsSection from './components/AppearanceSettingsSection';
import SolveSettingsSection from './components/SolveSettingsSection';
import BackgroundSettingsSection from './components/BackgroundSettingsSection';
import ProfileSettingsSection from './components/ProfileSettingsSection';
import ProfilePreviewSettingsSection from './components/ProfilePreviewSettingsSection';
import IntegrationsSettingsSection from './components/IntegrationsSettingsSection';
import SecuritySettingsSection from './components/SecuritySettingsSection';

const PROFILE_QUERY_KEY = ['profile', 'me'];
const UI_SETTINGS_QUERY_KEY = ['ui-settings', 'me'];
const TELEGRAM_QUERY_KEY = ['integrations', 'telegram'];
const MINECRAFT_QUERY_KEY = ['integrations', 'minecraft'];

const loadProfile = () => getProfile();
const loadUiSettings = () => getMyUiSettings();
const loadTelegram = () => getTelegramStatus();
const loadMinecraft = () => getMinecraftStatus();

function normalizeRemoteUiSettings(remote, previous) {
  if (!remote || typeof remote !== 'object') return previous;
  return {
    colorTheme: remote.colorTheme || previous.colorTheme,
    mode: remote.mode || previous.mode,
    uiStyle: remote.uiStyle === 'neobrutal' ? 'neobrutal' : (previous.uiStyle || 'default'),
    bgFx: Boolean(remote.bgFx),
    fxMode: remote.fxMode || previous.fxMode,
    fxVariant: String(remote.fxVariant ?? previous.fxVariant),
    codeSolveLayout: remote.codeSolveLayout || previous.codeSolveLayout || 'split',
    codeEditorStyle: remote.codeEditorStyle === 'mono' ? 'mono' : 'color',
    showSidebarToggle: remote.showSidebarToggle !== false,
  };
}

function SettingsNavigation({ activeSection, onOpen, profile, extra }) {
  return (
    <aside className="self-start space-y-3">
      <Card className="p-2">
        <nav className="space-y-1" aria-label="Разделы настроек">
          {SETTINGS_SECTIONS.map((section) => (
            <button
              key={section.key}
              type="button"
              onClick={() => onOpen(section.key)}
              className={`w-full rounded-2xl px-4 py-3 text-left font-semibold transition ${activeSection === section.key
                ? 'bg-[rgba(var(--accent)/0.16)] text-[rgb(var(--accent))]'
                : 'text-neutral-800 hover:bg-neutral-100 dark:text-neutral-100 dark:hover:bg-neutral-800/60'}`}
            >
              {section.title}
            </button>
          ))}
        </nav>
      </Card>
      {profile ? <MiniProfilePreview profile={profile} extra={extra} compact /> : null}
    </aside>
  );
}

const MemoSettingsNavigation = React.memo(SettingsNavigation);

export default function SettingsFeature() {
  const [searchParams, setSearchParams] = useSearchParams();
  const notify = useNotify();
  const auth = useAuth();
  const queryClient = useQueryClient();
  const { applyUiSettings } = useUiSettingsActions();

  const requestedSection = searchParams.get('section');
  const initialSection = SETTINGS_SECTION_KEYS.has(requestedSection) ? requestedSection : 'appearance';
  const [activeSection, setActiveSection] = useState(initialSection);
  const [saving, setSaving] = useState(false);
  const [profile, setProfile] = useState(null);
  const [extra, setExtra] = useState(() => parseProfileExtra(null));
  const [profileDirty, setProfileDirty] = useState(false);
  const [uiDirty, setUiDirty] = useState(false);
  const [form, setForm] = useState(defaultUiSettings);

  const profileQuery = useQuery({ queryKey: PROFILE_QUERY_KEY, queryFn: loadProfile, staleTime: 60_000, refetchOnWindowFocus: true });
  const uiSettingsQuery = useQuery({ queryKey: UI_SETTINGS_QUERY_KEY, queryFn: loadUiSettings, staleTime: 60_000 });
  const telegramQuery = useQuery({ queryKey: TELEGRAM_QUERY_KEY, queryFn: loadTelegram, staleTime: 20_000 });
  const minecraftQuery = useQuery({ queryKey: MINECRAFT_QUERY_KEY, queryFn: loadMinecraft, staleTime: 20_000 });
  const refetchTelegramStatus = telegramQuery.refetch;
  const refetchMinecraftStatus = minecraftQuery.refetch;

  const hydratedProfileRef = useRef(null);
  const hydratedUiRef = useRef(null);
  useEffect(() => {
    if (!profileQuery.data || hydratedProfileRef.current === profileQuery.data) return;
    hydratedProfileRef.current = profileQuery.data;
    if (!profileDirty) {
      setProfile(profileQuery.data);
      setExtra(parseProfileExtra(profileQuery.data?.additionalDataJson));
    }
  }, [profileDirty, profileQuery.data]);

  useEffect(() => {
    if (!uiSettingsQuery.data || hydratedUiRef.current === uiSettingsQuery.data) return;
    hydratedUiRef.current = uiSettingsQuery.data;
    if (!uiDirty) setForm((previous) => normalizeRemoteUiSettings(uiSettingsQuery.data, previous));
  }, [uiDirty, uiSettingsQuery.data]);

  useEffect(() => {
    applyUiSettings(form);
  }, [applyUiSettings, form]);

  useEffect(() => {
    const next = searchParams.get('section');
    if (SETTINGS_SECTION_KEYS.has(next) && next !== activeSection) setActiveSection(next);
  }, [activeSection, searchParams]);

  useEffect(() => {
    window.scrollTo({ top: 0, left: 0, behavior: 'auto' });
  }, [activeSection]);

  const [emailForm, setEmailForm] = useState({ newEmail: '', password: '' });
  const [savingEmail, setSavingEmail] = useState(false);
  const [emailError, setEmailError] = useState(null);
  const [emailRevealOpen, setEmailRevealOpen] = useState(false);
  const [emailRevealPassword, setEmailRevealPassword] = useState('');
  const [revealedEmail, setRevealedEmail] = useState('');
  const [revealingEmail, setRevealingEmail] = useState(false);
  const [emailRevealError, setEmailRevealError] = useState(null);

  const [passwordForm, setPasswordForm] = useState({ currentPassword: '', newPassword: '', confirmNewPassword: '' });
  const [savingPassword, setSavingPassword] = useState(false);
  const [passwordError, setPasswordError] = useState(null);

  const [tgCode, setTgCode] = useState(null);
  const [tgExpires, setTgExpires] = useState(null);
  const [tgLoading, setTgLoading] = useState(false);
  const [tgError, setTgError] = useState(null);

  const [mcNick, setMcNick] = useState('');
  const [mcInputCode, setMcInputCode] = useState('');
  const [mcExpires, setMcExpires] = useState(null);
  const [mcDelivery, setMcDelivery] = useState(null);
  const [mcLoading, setMcLoading] = useState(false);
  const [mcError, setMcError] = useState(null);
  const minecraftStatus = minecraftQuery.data || null;
  const telegramStatus = telegramQuery.data || null;

  useEffect(() => {
    if (!mcNick && minecraftStatus?.nick) setMcNick(minecraftStatus.nick);
  }, [mcNick, minecraftStatus?.nick]);

  const profileId = profile?.id ?? profile?.userId ?? null;
  const profileLogin = String(profile?.login || '').trim();
  const profileLoginLooksOk = LOGIN_RE.test(profileLogin);
  const hasChanges = uiDirty || profileDirty;
  const canSave = hasChanges && (!profile || profileLoginLooksOk);
  const loading = profileQuery.isLoading || uiSettingsQuery.isLoading;

  const openSection = useCallback((key) => {
    setActiveSection(key);
    setSearchParams({ section: key }, { replace: true });
  }, [setSearchParams]);

  const setField = useCallback((key, value) => {
    setForm((previous) => ({ ...previous, [key]: value }));
    setUiDirty(true);
  }, []);

  const setProfileField = useCallback((key, value) => {
    setProfile((previous) => (previous ? { ...previous, [key]: value } : previous));
    setProfileDirty(true);
  }, []);

  const setExtraField = useCallback((key, value) => {
    setExtra((previous) => ({ ...previous, [key]: value }));
    setProfileDirty(true);
  }, []);

  const closeEmailReveal = useCallback(() => {
    setEmailRevealOpen(false);
    setEmailRevealPassword('');
    setRevealedEmail('');
    setEmailRevealError(null);
  }, []);

  const handleRevealEmail = useCallback(async () => {
    if (!emailRevealPassword) {
      setEmailRevealError('Введи текущий пароль.');
      return;
    }
    try {
      setRevealingEmail(true);
      setEmailRevealError(null);
      const data = await revealEmail(emailRevealPassword);
      setRevealedEmail(data?.email || '');
      setEmailRevealPassword('');
      notify.success('Email раскрыт');
    } catch (error) {
      setEmailRevealError(handleApiError(error, notify, 'Не удалось раскрыть email'));
    } finally {
      setRevealingEmail(false);
    }
  }, [emailRevealPassword, notify]);

  const previewProfile = useMemo(() => {
    const skills = String(extra?.skillsText || '').split(',').map((item) => item.trim()).filter(Boolean);
    return {
      id: profileId,
      firstName: profile?.firstName,
      lastName: profile?.lastName,
      displayName: displayName(profile),
      avatarUrl: profile?.profilePictureUrl || profile?.avatarUrl || '',
      profilePictureUrl: profile?.profilePictureUrl || profile?.avatarUrl || '',
      bio: extra?.bio || '',
      location: extra?.location || '',
      education: extra?.education || '',
      github: extra?.github || '',
      telegram: extra?.telegram || '',
      website: extra?.website || '',
      skills,
      solvedAssignments: profile?.solvedAssignments ?? 0,
      totalAttempts: profile?.totalAttempts ?? 0,
      rank: typeof profile?.rank === 'number' ? profile.rank : undefined,
    };
  }, [extra, profile, profileId]);

  const save = useCallback(async () => {
    if (!hasChanges || saving) return;
    if (profile && !profileLoginLooksOk) {
      notify.error('Логин должен быть от 3 до 64 символов: латинские буквы, цифры, точка, дефис или подчёркивание.');
      return;
    }
    const uiWasDirty = uiDirty;
    const profileWasDirty = profileDirty;
    setSaving(true);
    try {
      if (uiWasDirty) {
        const payload = {
          colorTheme: form.colorTheme,
          mode: form.mode,
          uiStyle: form.uiStyle === 'neobrutal' ? 'neobrutal' : 'default',
          bgFx: Boolean(form.bgFx),
          fxMode: form.fxMode,
          fxVariant: Number(form.fxVariant),
          codeSolveLayout: form.codeSolveLayout,
          codeEditorStyle: form.codeEditorStyle === 'mono' ? 'mono' : 'color',
          showSidebarToggle: form.showSidebarToggle !== false,
        };
        const saved = await saveMyUiSettings(payload);
        queryClient.setQueryData(UI_SETTINGS_QUERY_KEY, saved || payload);
        setUiDirty(false);
      }
      if (profileWasDirty && profile) {
        const updated = await updateProfile({
          login: profileLogin,
          firstName: profile.firstName || '',
          lastName: profile.lastName || '',
          phoneNumber: profile.phoneNumber || '',
          profilePictureUrl: profile.profilePictureUrl || '',
          additionalDataJson: buildProfileExtra(extra),
        });
        const nextProfile = updated || profile;
        queryClient.setQueryData(PROFILE_QUERY_KEY, nextProfile);
        setProfile(nextProfile);
        setExtra(parseProfileExtra(nextProfile.additionalDataJson || buildProfileExtra(extra)));
        setProfileDirty(false);
        await auth?.refresh?.().catch(() => {});
      }
      notify.success(uiWasDirty && profileWasDirty ? 'Настройки и профиль сохранены' : profileWasDirty ? 'Профиль сохранён' : 'Настройки сохранены');
    } catch (error) {
      handleApiError(error, notify, 'Не удалось сохранить изменения');
    } finally {
      setSaving(false);
    }
  }, [auth, extra, form, hasChanges, notify, profile, profileDirty, profileLogin, profileLoginLooksOk, queryClient, saving, uiDirty]);

  const refreshTelegram = useCallback(async () => {
    setTgError(null);
    try { return await refetchTelegramStatus(); } catch (error) { setTgError(handleApiError(error, notify, 'Не удалось обновить Telegram')); return null; }
  }, [notify, refetchTelegramStatus]);

  const generateTelegram = useCallback(async () => {
    try {
      setTgLoading(true); setTgError(null);
      const dto = await generateTelegramCode();
      setTgCode(dto.code); setTgExpires(dto.expiresAtUtc);
      queryClient.setQueryData(TELEGRAM_QUERY_KEY, dto.status || dto);
      notify.success('Код Telegram создан');
    } catch (error) { setTgError(handleApiError(error, notify, 'Не удалось сгенерировать код Telegram')); }
    finally { setTgLoading(false); }
  }, [notify, queryClient]);

  const copyTelegram = useCallback(async () => {
    if (!tgCode) return;
    try { await navigator.clipboard.writeText(tgCode); notify.success('Код скопирован'); }
    catch { notify.warn('Не удалось скопировать код'); }
  }, [notify, tgCode]);

  const unlinkTelegramAccount = useCallback(async () => {
    if (!window.confirm('Отвязать Telegram от аккаунта?')) return;
    try {
      setTgLoading(true); setTgError(null);
      await unlinkTelegram(); setTgCode(null); setTgExpires(null);
      await refreshTelegram(); notify.success('Telegram отвязан');
    } catch (error) { setTgError(handleApiError(error, notify, 'Не удалось отвязать Telegram')); }
    finally { setTgLoading(false); }
  }, [notify, refreshTelegram]);

  const refreshMinecraft = useCallback(async () => {
    setMcError(null);
    try { return await refetchMinecraftStatus(); } catch (error) { setMcError(handleApiError(error, notify, 'Не удалось обновить Minecraft')); return null; }
  }, [notify, refetchMinecraftStatus]);

  const requestMinecraft = useCallback(async () => {
    const nick = mcNick.trim();
    if (!nick) { setMcError('Введи ник на сервере Minecraft.'); return; }
    try {
      setMcLoading(true); setMcError(null); setMcDelivery(null); setMcExpires(null);
      const dto = await requestMinecraftLink(nick);
      setMcInputCode(''); setMcExpires(dto.expiresAtUtc); setMcDelivery(dto.delivery || null);
      queryClient.setQueryData(MINECRAFT_QUERY_KEY, dto.status || dto);
      notify.success('Код Minecraft создан');
    } catch (error) { setMcError(handleApiError(error, notify, 'Не удалось отправить код привязки Minecraft')); }
    finally { setMcLoading(false); }
  }, [mcNick, notify, queryClient]);

  const confirmMinecraft = useCallback(async () => {
    const code = mcInputCode.trim();
    if (!code) { setMcError('Введи код, который пришёл тебе в игре.'); return; }
    try {
      setMcLoading(true); setMcError(null);
      const status = await confirmMinecraftLink(code);
      queryClient.setQueryData(MINECRAFT_QUERY_KEY, status);
      setMcNick(''); setMcInputCode(''); setMcExpires(null); setMcDelivery(null);
      await auth?.refresh?.().catch(() => {});
      notify.success('Minecraft привязан');
    } catch (error) { setMcError(handleApiError(error, notify, 'Не удалось подтвердить код Minecraft')); }
    finally { setMcLoading(false); }
  }, [auth, mcInputCode, notify, queryClient]);

  const unlinkMinecraftAccount = useCallback(async (linkId, nick) => {
    if (!linkId || !window.confirm(`Удалить привязку ${nick || 'Minecraft'}? Баланс сохранится.`)) return;
    try {
      setMcLoading(true); setMcError(null);
      const status = await unlinkMinecraft(linkId);
      queryClient.setQueryData(MINECRAFT_QUERY_KEY, status);
      setMcInputCode(''); setMcExpires(null); setMcDelivery(null);
      await auth?.refresh?.().catch(() => {});
      notify.success('Привязка Minecraft удалена. Баланс сохранён.');
    } catch (error) { setMcError(handleApiError(error, notify, 'Не удалось удалить привязку Minecraft')); }
    finally { setMcLoading(false); }
  }, [auth, notify, queryClient]);

  const handleEmailSubmit = useCallback(async (event) => {
    event.preventDefault();
    const newEmail = emailForm.newEmail.trim();
    if (!newEmail || !emailForm.password) { setEmailError('Укажи новый email и текущий пароль.'); return; }
    try {
      setSavingEmail(true); setEmailError(null);
      await changeEmail(newEmail, emailForm.password);
      setProfile((previous) => (previous ? { ...previous, email: newEmail } : previous));
      queryClient.setQueryData(PROFILE_QUERY_KEY, (previous) => (previous ? { ...previous, email: newEmail } : previous));
      setEmailForm({ newEmail: '', password: '' });
      await auth?.refresh?.().catch(() => {});
      notify.success('Email обновлён');
    } catch (error) { setEmailError(handleApiError(error, notify, 'Не удалось обновить email')); }
    finally { setSavingEmail(false); }
  }, [auth, emailForm, notify, queryClient]);

  const handlePasswordSubmit = useCallback(async (event) => {
    event.preventDefault();
    if (!passwordForm.currentPassword || !passwordForm.newPassword) { setPasswordError('Заполни текущий и новый пароль.'); return; }
    if (passwordForm.newPassword !== passwordForm.confirmNewPassword) { setPasswordError('Новый пароль и повтор не совпадают.'); return; }
    try {
      setSavingPassword(true); setPasswordError(null);
      await changePassword(passwordForm.currentPassword, passwordForm.newPassword);
      setPasswordForm({ currentPassword: '', newPassword: '', confirmNewPassword: '' });
      notify.success('Пароль изменён');
    } catch (error) { setPasswordError(handleApiError(error, notify, 'Не удалось изменить пароль')); }
    finally { setSavingPassword(false); }
  }, [notify, passwordForm]);

  const activeContent = (() => {
    if (activeSection === 'appearance') return <AppearanceSettingsSection form={form} setField={setField} />;
    if (activeSection === 'solve') return <SolveSettingsSection form={form} setField={setField} />;
    if (activeSection === 'fx') return <BackgroundSettingsSection form={form} setField={setField} options={FX_OPTIONS} />;
    if (activeSection === 'profile') return (
      <ProfileSettingsSection
        loading={loading}
        profile={profile}
        extra={extra}
        profileId={profileId}
        profileDirty={profileDirty}
        profileLoginLooksOk={profileLoginLooksOk}
        setProfileField={setProfileField}
        setExtraField={setExtraField}
        openPreview={() => openSection('preview')}
        emailReveal={{
          open: emailRevealOpen,
          revealedEmail,
          password: emailRevealPassword,
          loading: revealingEmail,
          error: emailRevealError,
          onOpen: () => setEmailRevealOpen(true),
          onClose: closeEmailReveal,
          onPasswordChange: setEmailRevealPassword,
          onReveal: handleRevealEmail,
        }}
      />
    );
    if (activeSection === 'preview') return <ProfilePreviewSettingsSection loading={loading} profile={profile} profileId={profileId} previewProfile={previewProfile} />;
    if (activeSection === 'integrations') return (
      <IntegrationsSettingsSection
        telegram={{ status: telegramStatus, code: tgCode, expires: tgExpires, loading: tgLoading || telegramQuery.isFetching, error: tgError, refresh: refreshTelegram, generate: generateTelegram, copy: copyTelegram, unlink: unlinkTelegramAccount }}
        minecraft={{ status: minecraftStatus, nick: mcNick, setNick: setMcNick, inputCode: mcInputCode, setInputCode: setMcInputCode, expires: mcExpires, delivery: mcDelivery, loading: mcLoading || minecraftQuery.isFetching, error: mcError, refresh: refreshMinecraft, request: requestMinecraft, confirm: confirmMinecraft, unlink: unlinkMinecraftAccount }}
      />
    );
    return <SecuritySettingsSection profile={profile} email={{ form: emailForm, setForm: setEmailForm, saving: savingEmail, error: emailError, submit: handleEmailSubmit }} password={{ form: passwordForm, setForm: setPasswordForm, saving: savingPassword, error: passwordError, submit: handlePasswordSubmit }} />;
  })();

  return (
    <div className="w-full max-w-[1320px]">
      <div className="grid items-start gap-4 lg:grid-cols-[244px_minmax(0,1040px)]">
        <MemoSettingsNavigation activeSection={activeSection} onOpen={openSection} profile={profile} extra={extra} />
        <section className="min-w-0 max-w-[1040px]">
          {loading ? <div className="mb-3 text-sm text-neutral-500 dark:text-neutral-400">Загрузка…</div> : null}
          {profileQuery.error && !profile ? <Card className="mb-4 p-4 text-sm text-red-500">Не удалось загрузить профиль. <Button variant="outline" onClick={profileQuery.refetch}>Повторить</Button></Card> : null}
          {activeContent}
          <div className="sticky bottom-4 z-10 mt-5 flex justify-end pointer-events-none">
            <Button onClick={save} disabled={saving || !canSave} className="pointer-events-auto shadow-lg disabled:cursor-not-allowed disabled:opacity-55">
              {saving ? 'Сохранение…' : hasChanges ? 'Сохранить' : 'Сохранено'}
            </Button>
          </div>
        </section>
      </div>
    </div>
  );
}
