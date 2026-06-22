import React, { useEffect, useMemo, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { Eye, EyeOff, ExternalLink, LockKeyhole } from "lucide-react";
import Layout from "../components/Layout";
import { Card, Button, Input, Textarea, Badge } from "../components/ui";
import {
  getProfile,
  updateProfile,
  changeEmail,
  changePassword,
  revealEmail,
} from "../api/profile";
import {
  getTelegramStatus,
  generateTelegramCode,
  unlinkTelegram,
} from "../api/telegramLink";
import {
  getMinecraftStatus,
  requestMinecraftLink,
  confirmMinecraftLink,
  unlinkMinecraft,
} from "../api/minecraftLink";
import { getMyUiSettings, saveMyUiSettings } from "../api/uiSettings";
import { useAuth } from "../auth/AuthContext";
import { useNotify } from "../components/notify/NotifyProvider";
import { handleApiError } from "../utils/handleApiError";
import { parseProfileExtra, buildProfileExtra } from "../utils/profileExtra";
import PublicProfileCard from "../components/profile/PublicProfileCard";

const LS_KEY = "uiSettings";
const LOGIN_RE = /^[a-zA-Z0-9_.-]{3,64}$/;

const SECTIONS = [
  { key: "appearance", title: "Внешний вид" },
  { key: "solve", title: "Решение задач" },
  { key: "fx", title: "Фоновые эффекты" },
  { key: "profile", title: "Профиль" },
  { key: "preview", title: "Предпросмотр" },
  { key: "integrations", title: "Связи" },
  { key: "security", title: "Безопасность" },
];

const SECTION_KEYS = new Set(SECTIONS.map((x) => x.key));

function readLocal() {
  try {
    const raw = localStorage.getItem(LS_KEY);
    if (!raw) return null;
    return JSON.parse(raw);
  } catch {
    return null;
  }
}

function writeLocal(v) {
  localStorage.setItem(
    "colorTheme",
    v?.colorTheme || localStorage.getItem("colorTheme") || "pink",
  );
  localStorage.setItem(
    "mode",
    v?.mode || localStorage.getItem("mode") || "dark",
  );
  localStorage.setItem("bgFx", v?.bgFx ? "1" : "0");
  localStorage.setItem(
    "fxMode",
    v?.fxMode || localStorage.getItem("fxMode") || "random",
  );
  localStorage.setItem(
    "fxVariant",
    String(v?.fxVariant ?? localStorage.getItem("fxVariant") ?? "2"),
  );
  localStorage.setItem(
    "codeSolveLayout",
    v?.codeSolveLayout || localStorage.getItem("codeSolveLayout") || "split",
  );
  localStorage.setItem(
    "showSidebarToggle",
    v?.showSidebarToggle === false ? "0" : "1",
  );
  const persisted = { ...(v || {}) };
  delete persisted.sidebarCollapsed;
  localStorage.setItem(LS_KEY, JSON.stringify(persisted));
  window.dispatchEvent(new Event("tf-ui-settings-changed"));
}

function maskEmail(email) {
  const value = String(email || "").trim();
  if (!value || !value.includes("@")) return "Почта не указана";

  const [name, domain] = value.split("@");
  const nameMask =
    name.length <= 2 ? `${name.slice(0, 1)}***` : `${name.slice(0, 2)}***`;
  const parts = domain.split(".");
  const zone = parts.length > 1 ? parts.pop() : "";
  const domainName = parts.join(".") || domain;
  const domainMask =
    domainName.length <= 2
      ? `${domainName.slice(0, 1)}***`
      : `${domainName.slice(0, 2)}***`;

  return zone
    ? `${nameMask}@${domainMask}.${zone}`
    : `${nameMask}@${domainMask}`;
}

function formatDate(value) {
  if (!value) return "не указано";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "не указано";
  return date.toLocaleDateString("ru-RU");
}

function profileRole(profile) {
  const roles = Array.isArray(profile?.roles)
    ? profile.roles.filter(Boolean)
    : [];
  if (roles.length) return roles.join(", ");
  return profile?.role || "User";
}

function displayName(profile) {
  const first = String(profile?.firstName || "").trim();
  const last = String(profile?.lastName || "").trim();
  const full = [last, first].filter(Boolean).join(" ");
  if (full) return full;
  const login = String(profile?.login || "").trim();
  if (login) return login;
  const email = String(profile?.email || "").trim();
  return email ? maskEmail(email) : "Пользователь";
}

function initials(profile) {
  const first = String(profile?.firstName || "").trim();
  const last = String(profile?.lastName || "").trim();
  const value = `${last ? last[0] : ""}${first ? first[0] : ""}`.trim();
  if (value) return value.toUpperCase();
  const login = String(profile?.login || "").trim();
  if (login) return login[0].toUpperCase();
  const email = String(profile?.email || "").trim();
  return email ? email[0].toUpperCase() : "TF";
}

function ChoiceButton({ active, title, desc, onClick, disabled = false }) {
  return (
    <button
      type="button"
      disabled={disabled}
      onClick={disabled ? undefined : onClick}
      className={
        `rounded-2xl border px-4 py-3 text-left transition ` +
        (disabled
          ? "cursor-not-allowed border-[rgba(var(--border)/0.42)] bg-[rgba(var(--card)/0.32)] opacity-50"
          : active
            ? "border-[rgb(var(--accent))] bg-[rgba(var(--accent)/0.12)] shadow-[0_0_0_1px_rgba(var(--accent)/0.18)]"
            : "border-[rgba(var(--border)/0.72)] bg-[rgba(var(--card)/0.58)] hover:bg-[rgba(var(--card)/0.86)]")
      }
    >
      <div className="font-semibold leading-snug">{title}</div>
      {desc ? (
        <div className="mt-1 text-sm leading-snug text-neutral-500 dark:text-neutral-400">
          {desc}
        </div>
      ) : null}
    </button>
  );
}

function ReadOnlyValue({ label, value, hint }) {
  return (
    <div>
      <label className="text-sm text-neutral-500 dark:text-neutral-400">
        {label}
      </label>
      <div className="mt-1 rounded-xl border border-[rgba(var(--border)/0.65)] bg-neutral-100/60 px-3 py-2 text-neutral-500 dark:bg-neutral-950/35 dark:text-neutral-400">
        {value}
      </div>
      {hint ? (
        <div className="mt-1 text-xs text-neutral-500 dark:text-neutral-400">
          {hint}
        </div>
      ) : null}
    </div>
  );
}

function EmailRevealControl({
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
      <label className="text-sm text-neutral-500 dark:text-neutral-400">
        Почта
      </label>
      <div className="mt-1 rounded-xl border border-[rgba(var(--border)/0.65)] bg-neutral-100/60 px-3 py-2 dark:bg-neutral-950/35">
        <div className="flex items-center gap-2">
          <span className="min-w-0 flex-1 truncate text-neutral-600 dark:text-neutral-300">
            {revealedEmail || maskedEmail}
          </span>
          <button
            type="button"
            className="inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-xl border border-[rgba(var(--border)/0.65)] hover:bg-[rgba(var(--border)/0.18)]"
            onClick={revealedEmail || open ? onClose : onOpen}
            title={revealedEmail || open ? "Скрыть email" : "Показать email"}
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
                onChange={(e) => onPasswordChange(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === "Enter") {
                    e.preventDefault();
                    onReveal();
                  }
                }}
              />
              <Button
                type="button"
                onClick={onReveal}
                disabled={loading || !password}
              >
                {loading ? "Проверка…" : "Показать"}
              </Button>
            </div>
            {error ? (
              <div className="mt-2 text-xs text-red-300">
                {typeof error === "string"
                  ? error
                  : error?.primaryMessage ||
                    error?.userMessage ||
                    "Не удалось раскрыть email"}
              </div>
            ) : null}
          </div>
        ) : null}
      </div>

    </div>
  );
}

function InlineError({ value }) {
  if (!value) return null;
  return (
    <div className="rounded-2xl border border-red-400/40 bg-red-500/10 px-3 py-2 text-sm text-red-300">
      {typeof value === "string"
        ? value
        : value?.primaryMessage ||
          value?.userMessage ||
          "Не удалось выполнить действие"}
    </div>
  );
}

function MiniProfilePreview({ profile, extra, compact = false }) {
  const name = displayName(profile);
  const avatarUrl = profile?.profilePictureUrl || profile?.avatarUrl || "";
  const skills = String(extra?.skillsText || "")
    .split(",")
    .map((x) => x.trim())
    .filter(Boolean)
    .slice(0, compact ? 3 : 8);

  return (
    <Card className={compact ? "p-3 space-y-3" : "p-5 space-y-5"}>
      <div className="flex items-center gap-3">
        <div
          className={
            compact
              ? "h-12 w-12 shrink-0 overflow-hidden rounded-2xl grid place-items-center bg-[rgba(var(--accent)/0.18)] text-[rgb(var(--accent))] font-semibold"
              : "h-20 w-20 shrink-0 overflow-hidden rounded-3xl grid place-items-center bg-[rgba(var(--accent)/0.18)] text-[rgb(var(--accent))] text-2xl font-semibold"
          }
        >
          {avatarUrl ? (
            <img
              src={avatarUrl}
              alt={name}
              className="h-full w-full object-cover"
            />
          ) : (
            <span>{initials(profile)}</span>
          )}
        </div>
        <div className="min-w-0">
          <div
            className={
              compact
                ? "truncate font-semibold"
                : "truncate text-xl font-semibold"
            }
          >
            {name}
          </div>
          <div className="mt-1 truncate text-xs text-neutral-500 dark:text-neutral-400">
            @{profile?.login || "login"}
          </div>
          <div className="mt-2 inline-flex rounded-full border border-[rgba(var(--border)/0.65)] px-2 py-1 text-xs text-neutral-500 dark:text-neutral-400">
            {profileRole(profile)}
          </div>
        </div>
      </div>

      {!compact && (
        <>
          {extra?.bio ? (
            <p className="whitespace-pre-line text-sm leading-relaxed text-neutral-700 dark:text-neutral-300">
              {extra.bio}
            </p>
          ) : (
            <p className="text-sm text-neutral-500 dark:text-neutral-400">
              Описание ещё не заполнено.
            </p>
          )}

          <div className="grid gap-3 text-sm sm:grid-cols-2">
            <div className="rounded-2xl border border-[rgba(var(--border)/0.55)] px-3 py-2">
              <div className="text-xs text-neutral-500 dark:text-neutral-400">
                Место
              </div>
              <div className="mt-1 truncate">
                {extra?.location || "не указано"}
              </div>
            </div>
            <div className="rounded-2xl border border-[rgba(var(--border)/0.55)] px-3 py-2">
              <div className="text-xs text-neutral-500 dark:text-neutral-400">
                Образование
              </div>
              <div className="mt-1 truncate">
                {extra?.education || "не указано"}
              </div>
            </div>
            <div className="rounded-2xl border border-[rgba(var(--border)/0.55)] px-3 py-2">
              <div className="text-xs text-neutral-500 dark:text-neutral-400">
                Рейтинг
              </div>
              <div className="mt-1 font-semibold">
                {profile?.score ?? profile?.rating ?? 0}
              </div>
            </div>
            <div className="rounded-2xl border border-[rgba(var(--border)/0.55)] px-3 py-2">
              <div className="text-xs text-neutral-500 dark:text-neutral-400">
                Решено задач
              </div>
              <div className="mt-1 font-semibold">
                {profile?.solvedAssignments ?? 0}
              </div>
            </div>
          </div>
        </>
      )}

      {skills.length > 0 ? (
        <div className="flex flex-wrap gap-2">
          {skills.map((skill) => (
            <Badge key={skill}>{skill}</Badge>
          ))}
        </div>
      ) : null}
    </Card>
  );
}

export default function SettingsPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const notify = useNotify();
  const auth = useAuth();

  const initialSection = SECTION_KEYS.has(searchParams.get("section"))
    ? searchParams.get("section")
    : "appearance";
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [activeSection, setActiveSection] = useState(initialSection);
  const [profile, setProfile] = useState(null);
  const [extra, setExtra] = useState(() => parseProfileExtra(null));
  const [profileDirty, setProfileDirty] = useState(false);
  const [uiDirty, setUiDirty] = useState(false);

  const [emailForm, setEmailForm] = useState({ newEmail: "", password: "" });
  const [savingEmail, setSavingEmail] = useState(false);
  const [emailError, setEmailError] = useState(null);
  const [emailRevealOpen, setEmailRevealOpen] = useState(false);
  const [emailRevealPassword, setEmailRevealPassword] = useState("");
  const [revealedEmail, setRevealedEmail] = useState("");
  const [revealingEmail, setRevealingEmail] = useState(false);
  const [emailRevealError, setEmailRevealError] = useState(null);

  const [passwordForm, setPasswordForm] = useState({
    currentPassword: "",
    newPassword: "",
    confirmNewPassword: "",
  });
  const [savingPassword, setSavingPassword] = useState(false);
  const [passwordError, setPasswordError] = useState(null);

  const [tgStatus, setTgStatus] = useState(null);
  const [tgCode, setTgCode] = useState(null);
  const [tgExpires, setTgExpires] = useState(null);
  const [tgLoading, setTgLoading] = useState(false);
  const [tgError, setTgError] = useState(null);

  const [mcStatus, setMcStatus] = useState(null);
  const [mcNick, setMcNick] = useState("");
  const [mcGeneratedCode, setMcGeneratedCode] = useState("");
  const [mcInputCode, setMcInputCode] = useState("");
  const [mcExpires, setMcExpires] = useState(null);
  const [mcDelivery, setMcDelivery] = useState(null);
  const [mcLoading, setMcLoading] = useState(false);
  const [mcError, setMcError] = useState(null);

  const [form, setForm] = useState(
    () =>
      readLocal() || {
        colorTheme: localStorage.getItem("colorTheme") || "blue",
        mode: localStorage.getItem("mode") || "light",
        bgFx: localStorage.getItem("bgFx") === "1",
        fxMode: localStorage.getItem("fxMode") || "random",
        fxVariant: localStorage.getItem("fxVariant") || "2",
        codeSolveLayout: localStorage.getItem("codeSolveLayout") || "split",
        showSidebarToggle: localStorage.getItem("showSidebarToggle") !== "0",
      },
  );

  const profileId = profile?.id ?? profile?.userId ?? null;
  const profileLogin = String(profile?.login || "").trim();
  const profileLoginLooksOk = LOGIN_RE.test(profileLogin);
  const hasInvalidProfileLogin = !!profile && !profileLoginLooksOk;
  const hasChanges = uiDirty || profileDirty;
  const canSave = hasChanges && !hasInvalidProfileLogin;

  const fxOptions = useMemo(
    () => [
      {
        key: "random",
        title: "Случайный эффект при включении",
        desc: "Каждый раз выбирается новый фон.",
      },
      { key: "0", title: "Туман", desc: "Мягкий туман и блёстки." },
      {
        key: "1",
        title: "Пыль + кометы",
        desc: "Пыль, искры и редкие кометы.",
      },
      { key: "2", title: "Нейронные связи", desc: "Движущиеся точки и линии." },
      { key: "3", title: "Аврора", desc: "Большие мягкие световые блики." },
      { key: "4", title: "Сердечки", desc: "Плавающие сердечки на фоне." },
      { key: "5", title: "Matrix", desc: "Падающие символы как в Матрице." },
      {
        key: "6",
        title: "Соты (мёд)",
        desc: "Живые соты с мягкими волнами и искрами.",
      },
      {
        key: "7",
        title: "Дым (вихри)",
        desc: "Интерактивный дым с вихревыми завихрениями.",
      },
    ],
    [],
  );

  useEffect(() => {
    const next = searchParams.get("section");
    if (SECTION_KEYS.has(next) && next !== activeSection)
      setActiveSection(next);
  }, [searchParams, activeSection]);

  useEffect(() => {
    let alive = true;
    (async () => {
      try {
        setLoading(true);
        try {
          const p = await getProfile();
          if (alive) {
            setProfile(p || null);
            setExtra(parseProfileExtra(p?.additionalDataJson));
            setProfileDirty(false);
          }
        } catch (e) {
          handleApiError(e, notify, "Не удалось загрузить профиль");
        }

        try {
          const s = await getMyUiSettings();
          if (alive && s && typeof s === "object") {
            setForm((prev) => {
              const merged = {
                colorTheme: s.colorTheme || prev.colorTheme,
                mode: s.mode || prev.mode,
                bgFx: !!s.bgFx,
                fxMode: s.fxMode || prev.fxMode,
                fxVariant: String(s.fxVariant ?? prev.fxVariant),
                codeSolveLayout:
                  s.codeSolveLayout || prev.codeSolveLayout || "split",
                showSidebarToggle: s.showSidebarToggle !== false,
              };
              writeLocal(merged);
              return merged;
            });
            setUiDirty(false);
          }
        } catch (e) {
          handleApiError(
            e,
            notify,
            "Не удалось загрузить настройки интерфейса",
          );
        }

        try {
          const st = await getTelegramStatus();
          if (alive) setTgStatus(st);
        } catch {
          if (alive) setTgStatus(null);
        }

        try {
          const st = await getMinecraftStatus();
          if (alive) {
            setMcStatus(st);
            if (st?.nick) setMcNick(st.nick);
          }
        } catch {
          if (alive) setMcStatus(null);
        }
      } finally {
        if (alive) setLoading(false);
      }
    })();
    return () => {
      alive = false;
    };
  }, [notify]);

  useEffect(() => {
    window.scrollTo({ top: 0, left: 0, behavior: "auto" });
  }, [activeSection]);

  const openSection = (key) => {
    setActiveSection(key);
    setSearchParams({ section: key }, { replace: true });
  };

  const setField = (k, v) => {
    setForm((p) => {
      const next = { ...p, [k]: v };
      writeLocal(next);
      return next;
    });
    setUiDirty(true);
  };

  const setProfileField = (k, v) => {
    setProfile((p) => (p ? { ...p, [k]: v } : p));
    setProfileDirty(true);
  };

  const setExtraField = (k, v) => {
    setExtra((p) => ({ ...p, [k]: v }));
    setProfileDirty(true);
  };

  const handleRevealEmail = async () => {
    if (!emailRevealPassword) {
      setEmailRevealError("Введи текущий пароль.");
      return;
    }
    try {
      setRevealingEmail(true);
      setEmailRevealError(null);
      const data = await revealEmail(emailRevealPassword);
      setRevealedEmail(data?.email || "");
      setEmailRevealPassword("");
      notify.success("Email раскрыт");
    } catch (err) {
      const parsed = handleApiError(err, notify, "Не удалось раскрыть email");
      setEmailRevealError(parsed);
    } finally {
      setRevealingEmail(false);
    }
  };

  const closeEmailReveal = () => {
    setEmailRevealOpen(false);
    setEmailRevealPassword("");
    setRevealedEmail("");
    setEmailRevealError(null);
  };

  const buildPublicPreviewProfile = () => {
    const skills = String(extra?.skillsText || "")
      .split(",")
      .map((x) => x.trim())
      .filter(Boolean);
    return {
      id: profileId,
      firstName: profile?.firstName,
      lastName: profile?.lastName,
      displayName: displayName(profile),
      avatarUrl: profile?.profilePictureUrl || profile?.avatarUrl || "",
      profilePictureUrl: profile?.profilePictureUrl || profile?.avatarUrl || "",
      bio: extra?.bio || "",
      location: extra?.location || "",
      education: extra?.education || "",
      github: extra?.github || "",
      telegram: extra?.telegram || "",
      website: extra?.website || "",
      skills,
      solvedAssignments: profile?.solvedAssignments ?? 0,
      totalAttempts: profile?.totalAttempts ?? 0,
      rank: typeof profile?.rank === "number" ? profile.rank : undefined,
    };
  };

  const save = async () => {
    if (!hasChanges || saving) return;
    if (hasInvalidProfileLogin) {
      notify.error("Логин должен быть от 3 до 64 символов: латинские буквы, цифры, точка, дефис или подчёркивание.");
      return;
    }
    setSaving(true);
    try {
      if (uiDirty) {
        const payload = {
          colorTheme: form.colorTheme,
          mode: form.mode,
          bgFx: !!form.bgFx,
          fxMode: form.fxMode,
          fxVariant: Number(form.fxVariant),
          codeSolveLayout: form.codeSolveLayout,
          showSidebarToggle: form.showSidebarToggle !== false,
        };
        await saveMyUiSettings(payload);
        setUiDirty(false);
      }

      if (profileDirty && profile) {
        const updated = await updateProfile({
          login: profileLogin,
          firstName: profile.firstName || "",
          lastName: profile.lastName || "",
          phoneNumber: profile.phoneNumber || "",
          profilePictureUrl: profile.profilePictureUrl || "",
          additionalDataJson: buildProfileExtra(extra),
        });
        setProfile(updated || profile);
        setExtra(
          parseProfileExtra(
            (updated || profile)?.additionalDataJson ||
              buildProfileExtra(extra),
          ),
        );
        setProfileDirty(false);
        try {
          await auth?.refresh?.();
        } catch {}
      }

      notify.success(
        uiDirty && profileDirty
          ? "Настройки и профиль сохранены"
          : profileDirty
            ? "Профиль сохранён"
            : "Настройки сохранены",
      );
    } catch (e) {
      handleApiError(e, notify, "Не удалось сохранить изменения");
    } finally {
      setSaving(false);
    }
  };

  const refreshTgStatus = async () => {
    try {
      setTgError(null);
      const st = await getTelegramStatus();
      setTgStatus(st);
    } catch (e) {
      const parsed = handleApiError(e, notify, "Не удалось обновить Telegram");
      setTgError(parsed);
    }
  };

  const handleGenerateTgCode = async () => {
    try {
      setTgLoading(true);
      setTgError(null);
      const dto = await generateTelegramCode();
      setTgCode(dto.code);
      setTgExpires(dto.expiresAtUtc);
      setTgStatus(dto.status || dto);
      notify.success("Код Telegram создан");
    } catch (e) {
      const parsed = handleApiError(
        e,
        notify,
        "Не удалось сгенерировать код Telegram",
      );
      setTgError(parsed);
    } finally {
      setTgLoading(false);
    }
  };

  const handleCopyTgCode = async () => {
    if (!tgCode) return;
    try {
      await navigator.clipboard.writeText(tgCode);
      notify.success("Код скопирован");
    } catch {
      notify.warn("Не удалось скопировать код");
    }
  };

  const handleUnlinkTg = async () => {
    if (!window.confirm("Отвязать Telegram от аккаунта?")) return;
    try {
      setTgLoading(true);
      setTgError(null);
      await unlinkTelegram();
      setTgCode(null);
      setTgExpires(null);
      await refreshTgStatus();
      notify.success("Telegram отвязан");
    } catch (e) {
      const parsed = handleApiError(e, notify, "Не удалось отвязать Telegram");
      setTgError(parsed);
    } finally {
      setTgLoading(false);
    }
  };

  const refreshMcStatus = async () => {
    try {
      setMcError(null);
      const st = await getMinecraftStatus();
      setMcStatus(st);
      if (st?.nick) setMcNick(st.nick);
    } catch (e) {
      const parsed = handleApiError(e, notify, "Не удалось обновить Minecraft");
      setMcError(parsed);
    }
  };

  const handleRequestMc = async () => {
    const nick = (mcNick || "").trim();
    if (!nick) {
      setMcError("Введи ник на сервере Minecraft.");
      return;
    }
    try {
      setMcLoading(true);
      setMcError(null);
      const dto = await requestMinecraftLink(nick);
      setMcInputCode("");
      setMcExpires(dto.expiresAtUtc);
      setMcGeneratedCode(dto.code || "");
      setMcDelivery(dto.delivery || null);
      setMcStatus(dto.status || dto);
      notify.success("Код Minecraft создан");
    } catch (e) {
      const parsed = handleApiError(
        e,
        notify,
        "Не удалось отправить код привязки Minecraft",
      );
      setMcError(parsed);
    } finally {
      setMcLoading(false);
    }
  };

  const handleConfirmMc = async () => {
    const code = (mcInputCode || "").trim();
    if (!code) {
      setMcError("Введи код, который пришёл тебе в игре.");
      return;
    }
    try {
      setMcLoading(true);
      setMcError(null);
      const st = await confirmMinecraftLink(code);
      setMcStatus(st);
      setMcInputCode("");
      setMcExpires(null);
      setMcDelivery(null);
      try {
        await auth?.refresh?.();
      } catch {}
      notify.success("Minecraft привязан");
    } catch (e) {
      const parsed = handleApiError(
        e,
        notify,
        "Не удалось подтвердить код Minecraft",
      );
      setMcError(parsed);
    } finally {
      setMcLoading(false);
    }
  };

  const handleUnlinkMc = async () => {
    if (!window.confirm("Отвязать Minecraft от аккаунта?")) return;
    try {
      setMcLoading(true);
      setMcError(null);
      await unlinkMinecraft();
      setMcGeneratedCode("");
      setMcInputCode("");
      setMcExpires(null);
      setMcDelivery(null);
      await refreshMcStatus();
      try {
        await auth?.refresh?.();
      } catch {}
      notify.success("Minecraft отвязан");
    } catch (e) {
      const parsed = handleApiError(e, notify, "Не удалось отвязать Minecraft");
      setMcError(parsed);
    } finally {
      setMcLoading(false);
    }
  };

  const handleCopyMcCode = async () => {
    if (!mcGeneratedCode) return;
    try {
      await navigator.clipboard.writeText(mcGeneratedCode);
      notify.success("Код скопирован");
    } catch {
      notify.warn("Не удалось скопировать код");
    }
  };

  const handleEmailSubmit = async (e) => {
    e.preventDefault();
    const newEmail = emailForm.newEmail.trim();
    if (!newEmail || !emailForm.password) {
      setEmailError("Укажи новый email и текущий пароль.");
      return;
    }
    try {
      setSavingEmail(true);
      setEmailError(null);
      await changeEmail(newEmail, emailForm.password);
      setProfile((p) => (p ? { ...p, email: newEmail } : p));
      setEmailForm({ newEmail: "", password: "" });
      try {
        await auth?.refresh?.();
      } catch {}
      notify.success("Email обновлён");
    } catch (err) {
      const parsed = handleApiError(err, notify, "Не удалось обновить email");
      setEmailError(parsed);
    } finally {
      setSavingEmail(false);
    }
  };

  const handlePasswordSubmit = async (e) => {
    e.preventDefault();
    if (!passwordForm.currentPassword || !passwordForm.newPassword) {
      setPasswordError("Заполни текущий и новый пароль.");
      return;
    }
    if (passwordForm.newPassword !== passwordForm.confirmNewPassword) {
      setPasswordError("Новый пароль и повтор не совпадают.");
      return;
    }
    try {
      setSavingPassword(true);
      setPasswordError(null);
      await changePassword(
        passwordForm.currentPassword,
        passwordForm.newPassword,
      );
      setPasswordForm({
        currentPassword: "",
        newPassword: "",
        confirmNewPassword: "",
      });
      notify.success("Пароль изменён");
    } catch (err) {
      const parsed = handleApiError(err, notify, "Не удалось изменить пароль");
      setPasswordError(parsed);
    } finally {
      setSavingPassword(false);
    }
  };

  const renderAppearance = () => (
    <div className="space-y-4">
      <Card className="p-4 space-y-4">
        <div>
          <div className="font-semibold">Режим</div>
          <div className="text-sm text-neutral-500 dark:text-neutral-400">
            Светлая или тёмная тема.
          </div>
        </div>
        <div className="flex flex-wrap gap-2">
          <Button
            variant={form.mode === "light" ? "primary" : "outline"}
            onClick={() => setField("mode", "light")}
          >
            Светлая
          </Button>
          <Button
            variant={form.mode === "dark" ? "primary" : "outline"}
            onClick={() => setField("mode", "dark")}
          >
            Тёмная
          </Button>
        </div>
      </Card>

      <Card className="p-4 space-y-4">
        <div>
          <div className="font-semibold">Палитра</div>
          <div className="text-sm text-neutral-500 dark:text-neutral-400">
            Цвет акцентов и кнопок.
          </div>
        </div>
        <div className="flex flex-wrap gap-2">
          <Button
            variant={form.colorTheme === "blue" ? "primary" : "outline"}
            onClick={() => setField("colorTheme", "blue")}
          >
            Синяя
          </Button>
          <Button
            variant={form.colorTheme === "pink" ? "primary" : "outline"}
            onClick={() => setField("colorTheme", "pink")}
          >
            Розовая
          </Button>
          <Button
            variant={form.colorTheme === "apple" ? "primary" : "outline"}
            onClick={() => setField("colorTheme", "apple")}
          >
            Яблоко
          </Button>
          <Button
            variant={form.colorTheme === "red" ? "primary" : "outline"}
            onClick={() => setField("colorTheme", "red")}
          >
            Красная
          </Button>
          <Button
            variant={form.colorTheme === "honey" ? "primary" : "outline"}
            onClick={() => setField("colorTheme", "honey")}
          >
            Мёд
          </Button>
        </div>
      </Card>

      <Card className="p-4 space-y-4">
        <div>
          <div className="font-semibold">Левое меню</div>
          <div className="text-sm text-neutral-500 dark:text-neutral-400">
            Основную навигацию можно свернуть до иконок через стрелку рядом с
            TaskForge.
          </div>
        </div>
        <div className="flex flex-wrap gap-2">
          <Button
            variant={form.showSidebarToggle !== false ? "primary" : "outline"}
            onClick={() => setField("showSidebarToggle", true)}
          >
            Показывать стрелку
          </Button>
          <Button
            variant={form.showSidebarToggle === false ? "primary" : "outline"}
            onClick={() => setField("showSidebarToggle", false)}
          >
            Скрыть стрелку
          </Button>
        </div>
      </Card>
    </div>
  );

  const renderSolve = () => (
    <Card className="p-4 space-y-4">
      <div>
        <div className="font-semibold">Страница решения задач</div>
        <div className="text-sm text-neutral-500 dark:text-neutral-400">
          Выберите, как будет выглядеть страница решения задач с кодом.
        </div>
      </div>
      <div className="grid gap-3 md:grid-cols-2">
        <ChoiceButton
          active={form.codeSolveLayout === "split"}
          title="Классический split"
          desc="Условие слева, редактор справа. Удобно, когда надо постоянно видеть текст задания."
          onClick={() => setField("codeSolveLayout", "split")}
        />
        <ChoiceButton
          active={form.codeSolveLayout === "editorTop"}
          title="Редактор сверху"
          desc="Поле кода на всю ширину, условие и публичные тесты ниже."
          onClick={() => setField("codeSolveLayout", "editorTop")}
        />
      </div>
    </Card>
  );

  const renderFx = () => (
    <div className="space-y-3">
      <Card className="p-4 flex items-center justify-between gap-4">
        <div>
          <div className="font-semibold">Фоновые эффекты</div>
          <div className="text-sm text-neutral-500 dark:text-neutral-400">
            Включает или выключает живой фон. Выбор варианта доступен только после включения эффектов.
          </div>
        </div>
        <Button
          variant={form.bgFx ? "primary" : "outline"}
          onClick={() => setField("bgFx", !form.bgFx)}
        >
          {form.bgFx ? "Включено" : "Выключено"}
        </Button>
      </Card>

      {!form.bgFx ? (
        <Card className="p-3 text-sm text-neutral-500 dark:text-neutral-400">
          Сначала включи фоновые эффекты. После этого можно будет выбрать случайный режим или конкретный вариант.
        </Card>
      ) : null}

      <div className="grid gap-3 md:grid-cols-2">
        {fxOptions.map((o) => {
          const isRandom = o.key === "random";
          const selected = isRandom
            ? form.fxMode === "random"
            : form.fxMode === "fixed" && String(form.fxVariant) === o.key;
          return (
            <ChoiceButton
              key={o.key}
              active={selected}
              disabled={!form.bgFx}
              title={o.title}
              desc={o.desc}
              onClick={() => {
                if (isRandom) setField("fxMode", "random");
                else {
                  setField("fxMode", "fixed");
                  setField("fxVariant", o.key);
                }
              }}
            />
          );
        })}
      </div>
    </div>
  );

  const renderProfile = () => {
    if (loading && !profile)
      return (
        <Card className="p-4 text-sm text-neutral-500 dark:text-neutral-400">
          Загрузка профиля…
        </Card>
      );
    if (!profile)
      return (
        <Card className="p-4 text-sm text-neutral-500 dark:text-neutral-400">
          Профиль не загрузился.
        </Card>
      );

    return (
      <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_320px]">
        <div className="space-y-4">
          <Card className="p-4 space-y-5">
            <div className="font-semibold">Основные данные</div>

            <div className="grid gap-4 md:grid-cols-2">
              <div className="md:col-span-2">
                <label className="text-sm text-neutral-500 dark:text-neutral-400">
                  Логин
                </label>
                <Input
                  value={profile.login || ""}
                  onChange={(e) => setProfileField("login", e.target.value)}
                  autoComplete="username"
                  placeholder="krytoichel"
                />
                {!profileLoginLooksOk && (
                  <div className="mt-1 text-xs text-red-500">
                    От 3 до 64 символов: латинские буквы, цифры, точка, дефис или подчёркивание.
                  </div>
                )}
              </div>

              <div>
                <label className="text-sm text-neutral-500 dark:text-neutral-400">
                  Имя
                </label>
                <Input
                  value={profile.firstName || ""}
                  onChange={(e) => setProfileField("firstName", e.target.value)}
                />
              </div>
              <div>
                <label className="text-sm text-neutral-500 dark:text-neutral-400">
                  Фамилия
                </label>
                <Input
                  value={profile.lastName || ""}
                  onChange={(e) => setProfileField("lastName", e.target.value)}
                />
              </div>
              <EmailRevealControl
                maskedEmail={profile.maskedEmail || maskEmail(profile.email)}
                revealedEmail={revealedEmail}
                password={emailRevealPassword}
                open={emailRevealOpen}
                loading={revealingEmail}
                error={emailRevealError}
                onOpen={() => setEmailRevealOpen(true)}
                onClose={closeEmailReveal}
                onPasswordChange={setEmailRevealPassword}
                onReveal={handleRevealEmail}
              />
              <div>
                <label className="text-sm text-neutral-500 dark:text-neutral-400">
                  Телефон
                </label>
                <Input
                  placeholder="+375 (__) ___-__-__"
                  value={profile.phoneNumber || ""}
                  onChange={(e) =>
                    setProfileField("phoneNumber", e.target.value)
                  }
                />
              </div>
              <div className="md:col-span-2">
                <label className="text-sm text-neutral-500 dark:text-neutral-400">
                  Ссылка на аватар
                </label>
                <Input
                  placeholder="https://example.com/avatar.jpg"
                  value={profile.profilePictureUrl || ""}
                  onChange={(e) =>
                    setProfileField("profilePictureUrl", e.target.value)
                  }
                />
              </div>
              <ReadOnlyValue
                label="Доступ"
                value={profileRole(profile)}
              />
            </div>
          </Card>

          <Card className="p-4 space-y-4">
            <div className="font-semibold">Публичный профиль</div>
            <div>
              <label className="text-sm text-neutral-500 dark:text-neutral-400">
                О себе
              </label>
              <Textarea
                rows={4}
                placeholder="Например: студент ИТ, люблю C#, делаю проекты на TaskForge…"
                value={extra.bio}
                onChange={(e) => setExtraField("bio", e.target.value)}
              />
            </div>
            <div className="grid gap-4 md:grid-cols-2">
              <div>
                <label className="text-sm text-neutral-500 dark:text-neutral-400">
                  Город / место учёбы
                </label>
                <Input
                  placeholder="Минск, БГУИР, ITD-21"
                  value={extra.location}
                  onChange={(e) => setExtraField("location", e.target.value)}
                />
              </div>
              <div>
                <label className="text-sm text-neutral-500 dark:text-neutral-400">
                  Образование / группа
                </label>
                <Input
                  placeholder="Факультет АИС, ITD-21"
                  value={extra.education}
                  onChange={(e) => setExtraField("education", e.target.value)}
                />
              </div>
            </div>
            <div>
              <label className="text-sm text-neutral-500 dark:text-neutral-400">
                Навыки
              </label>
              <Input
                placeholder="C#, C++, SQL, React"
                value={extra.skillsText}
                onChange={(e) => setExtraField("skillsText", e.target.value)}
              />

            </div>
          </Card>

          <Card className="p-4 space-y-4">
            <div className="font-semibold">Ссылки</div>
            <div className="grid gap-4 md:grid-cols-2">
              <div>
                <label className="text-sm text-neutral-500 dark:text-neutral-400">
                  GitHub
                </label>
                <Input
                  placeholder="https://github.com/..."
                  value={extra.github}
                  onChange={(e) => setExtraField("github", e.target.value)}
                />
              </div>
              <div>
                <label className="text-sm text-neutral-500 dark:text-neutral-400">
                  Telegram
                </label>
                <Input
                  placeholder="@username или https://t.me/username"
                  value={extra.telegram}
                  onChange={(e) => setExtraField("telegram", e.target.value)}
                />
              </div>
              <div className="md:col-span-2">
                <label className="text-sm text-neutral-500 dark:text-neutral-400">
                  Личный сайт / портфолио
                </label>
                <Input
                  placeholder="https://..."
                  value={extra.website}
                  onChange={(e) => setExtraField("website", e.target.value)}
                />
              </div>
            </div>
          </Card>

          <Card className="p-4 flex items-center justify-between gap-4">
            <div>
              <div className="font-semibold">Участие в рейтинге</div>
            </div>
            <Button
              variant={extra.showInLeaderboard ? "primary" : "outline"}
              onClick={() =>
                setExtraField("showInLeaderboard", !extra.showInLeaderboard)
              }
            >
              {extra.showInLeaderboard ? "Включено" : "Выключено"}
            </Button>
          </Card>
        </div>

        <aside className="space-y-4 xl:sticky xl:top-24 xl:self-start">
          <MiniProfilePreview profile={profile} extra={extra} />
          <Card className="p-4 space-y-2 text-sm text-neutral-500 dark:text-neutral-400">
            <div className="font-semibold text-neutral-900 dark:text-neutral-100">
              Аккаунт
            </div>
            <div>Логин: @{profile.login || "—"}</div>
            <div>Создан: {formatDate(profile.createdAt)}</div>
            <div>Последний вход: {formatDate(profile.lastLoginAt)}</div>
            {profileDirty ? (
              <div className="text-[rgb(var(--accent))]">
                Есть несохранённые изменения
              </div>
            ) : null}
            <div className="pt-2">
              <Button
                variant="outline"
                disabled={!profileId}
                onClick={() => openSection("preview")}
              >
                Предпросмотр
              </Button>
            </div>
          </Card>
        </aside>
      </div>
    );
  };

  const renderPreview = () => {
    if (loading && !profile)
      return (
        <Card className="p-4 text-sm text-neutral-500 dark:text-neutral-400">
          Загрузка публичной страницы…
        </Card>
      );
    if (!profile)
      return (
        <Card className="p-4 text-sm text-neutral-500 dark:text-neutral-400">
          Профиль не загрузился.
        </Card>
      );

    return (
      <div className="space-y-4">
        <Card className="p-4 flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <div>
            <div className="font-semibold">Предпросмотр публичной страницы</div>

          </div>
          <Button
            type="button"
            variant="outline"
            disabled={!profileId}
            onClick={() =>
              profileId &&
              window.open(
                `/users/${profileId}`,
                "_blank",
                "noopener,noreferrer",
              )
            }
          >
            <ExternalLink size={16} />
            <span>Открыть отдельно</span>
          </Button>
        </Card>
        <PublicProfileCard profile={buildPublicPreviewProfile()} embedded />
      </div>
    );
  };

  const renderIntegrations = () => (
    <div className="space-y-4">
      <Card className="p-4 space-y-3">
        <div className="flex items-center justify-between gap-3">
          <div>
            <div className="font-semibold">Telegram</div>
            <div className="text-sm text-neutral-500 dark:text-neutral-400">
              Привязка для уведомлений и бота.
            </div>
          </div>
          {tgStatus ? (
            <div className="text-xs text-neutral-500 dark:text-neutral-400">
              Защита: одноразовый временный код
            </div>
          ) : null}
        </div>
        <InlineError value={tgError} />
        {tgStatus?.linked ? (
          <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
            <div className="text-sm">
              Привязан: <b>{tgStatus.username || "Telegram"}</b>
            </div>
            <div className="flex flex-wrap gap-2">
              <Button
                variant="outline"
                onClick={refreshTgStatus}
                disabled={tgLoading}
              >
                Обновить
              </Button>
              <Button
                variant="outline"
                onClick={handleUnlinkTg}
                disabled={tgLoading}
              >
                Удалить привязку
              </Button>
            </div>
          </div>
        ) : (
          <div className="space-y-3">
            <div className="text-sm text-neutral-500 dark:text-neutral-400">
              Сгенерируй код и отправь его боту{" "}
              {tgStatus?.botUsername ? <b>{tgStatus.botUsername}</b> : null} в
              личку.
            </div>
            {tgCode ? (
              <div className="rounded-2xl border border-[rgba(var(--border)/0.65)] p-3">
                <div className="text-xs text-neutral-500 dark:text-neutral-400">
                  Твой код
                </div>
                <div className="mt-1 font-mono text-lg tracking-wider">
                  {tgCode}
                </div>
                {tgExpires ? (
                  <div className="mt-1 text-xs text-neutral-500 dark:text-neutral-400">
                    Действует до: {new Date(tgExpires).toLocaleString()}
                  </div>
                ) : null}
              </div>
            ) : null}
            <div className="flex flex-wrap gap-2">
              <Button onClick={handleGenerateTgCode} disabled={tgLoading}>
                {tgLoading ? "Генерация…" : "Сгенерировать код"}
              </Button>
              <Button
                variant="outline"
                onClick={handleCopyTgCode}
                disabled={!tgCode}
              >
                Копировать
              </Button>
              <Button
                variant="outline"
                onClick={refreshTgStatus}
                disabled={tgLoading}
              >
                Обновить
              </Button>
            </div>
          </div>
        )}
      </Card>

      <Card className="p-4 space-y-3">
        <div className="flex items-center justify-between gap-3">
          <div>
            <div className="font-semibold">Minecraft</div>
            <div className="text-sm text-neutral-500 dark:text-neutral-400">
              Привязка ника к аккаунту TaskForge.
            </div>
          </div>
          {mcStatus ? (
            <div className="text-xs text-neutral-500 dark:text-neutral-400">
              Привязки: {mcStatus.linkCount ?? 0}/2
            </div>
          ) : null}
        </div>
        <InlineError value={mcError} />
        {mcStatus?.linked ? (
          <div className="space-y-3">
            <div className="text-sm">
              Привязан ник: <b>{mcStatus.nick}</b>
              {mcStatus.uuid ? (
                <span className="text-xs text-neutral-500 dark:text-neutral-400">
                  {" "}
                  ({mcStatus.uuid})
                </span>
              ) : null}
            </div>
            <div className="text-xs text-neutral-500 dark:text-neutral-400">
              Если у игрока не хватает рейтинга, сервер может выдавать дебафы.
            </div>
            <div className="flex flex-wrap gap-2">
              <Button
                variant="outline"
                onClick={refreshMcStatus}
                disabled={mcLoading}
              >
                Обновить
              </Button>
              <Button
                variant="outline"
                onClick={handleUnlinkMc}
                disabled={mcLoading}
              >
                Удалить привязку
              </Button>
            </div>
          </div>
        ) : (
          <div className="space-y-3">
            <div>
              <label className="text-sm text-neutral-500 dark:text-neutral-400">
                Ник на сервере
              </label>
              <Input
                placeholder="Player_123"
                value={mcNick}
                onChange={(e) => setMcNick(e.target.value)}
                disabled={mcLoading}
              />
            </div>
            <div className="flex flex-wrap gap-2">
              <Button onClick={handleRequestMc} disabled={mcLoading}>
                {mcLoading ? "Отправка…" : "Отправить код в игру"}
              </Button>
              <Button
                variant="outline"
                onClick={refreshMcStatus}
                disabled={mcLoading}
              >
                Обновить
              </Button>
            </div>
            {mcDelivery ? (
              <div
                className={`rounded-2xl px-3 py-2 text-xs ${mcDelivery.delivered ? "bg-emerald-500/10 text-emerald-300" : "bg-amber-500/10 text-amber-300"}`}
              >
                {mcDelivery.attempted
                  ? mcDelivery.delivered
                    ? "Код отправлен в игру."
                    : `Не удалось доставить код в игру: ${mcDelivery.message}`
                  : "Плагин Minecraft ещё не настроен. Код можно ввести вручную."}
              </div>
            ) : null}
            {mcGeneratedCode ? (
              <div className="rounded-2xl border border-[rgba(var(--border)/0.65)] p-3">
                <div className="text-xs text-neutral-500 dark:text-neutral-400">
                  Запасной код
                </div>
                <div className="mt-1 font-mono text-lg tracking-wider">
                  {mcGeneratedCode}
                </div>
                {mcExpires ? (
                  <div className="mt-1 text-xs text-neutral-500 dark:text-neutral-400">
                    Действует до: {new Date(mcExpires).toLocaleString()}
                  </div>
                ) : null}
              </div>
            ) : null}
            <div>
              <label className="text-sm text-neutral-500 dark:text-neutral-400">
                Код из игры
              </label>
              <Input
                placeholder="ABCD-EFGH"
                value={mcInputCode}
                onChange={(e) => setMcInputCode(e.target.value)}
                disabled={mcLoading}
              />
            </div>
            <div className="flex flex-wrap gap-2">
              <Button
                variant="outline"
                onClick={handleConfirmMc}
                disabled={mcLoading || !mcInputCode}
              >
                Подтвердить
              </Button>
              <Button
                variant="outline"
                onClick={handleCopyMcCode}
                disabled={!mcGeneratedCode}
              >
                Копировать запасной код
              </Button>
            </div>
          </div>
        )}
      </Card>
    </div>
  );

  const renderSecurity = () => (
    <div className="space-y-4">
      <Card className="p-4 space-y-4">
        <div>
          <div className="font-semibold">Смена email</div>
          <div className="text-sm text-neutral-500 dark:text-neutral-400">
            Текущий email скрыт:{" "}
            {profile?.maskedEmail || maskEmail(profile?.email)}
          </div>
        </div>
        <InlineError value={emailError} />
        <form onSubmit={handleEmailSubmit} className="space-y-4">
          <div>
            <label className="text-sm text-neutral-500 dark:text-neutral-400">
              Новый email
            </label>
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
            <label className="text-sm text-neutral-500 dark:text-neutral-400">
              Текущий пароль
            </label>
            <Input
              type="password"
              value={emailForm.password}
              onChange={(e) =>
                setEmailForm((f) => ({ ...f, password: e.target.value }))
              }
            />
          </div>
          <div className="flex justify-end">
            <Button
              type="submit"
              disabled={
                savingEmail || !emailForm.newEmail || !emailForm.password
              }
            >
              {savingEmail ? "Сохранение…" : "Сменить email"}
            </Button>
          </div>
        </form>
      </Card>

      <Card className="p-4 space-y-4">
        <div>
          <div className="font-semibold">Смена пароля</div>
          <div className="text-sm text-neutral-500 dark:text-neutral-400">
            После смены используй новый пароль при следующем входе.
          </div>
        </div>
        <InlineError value={passwordError} />
        <form onSubmit={handlePasswordSubmit} className="space-y-4">
          <div>
            <label className="text-sm text-neutral-500 dark:text-neutral-400">
              Текущий пароль
            </label>
            <Input
              type="password"
              value={passwordForm.currentPassword}
              onChange={(e) =>
                setPasswordForm((f) => ({
                  ...f,
                  currentPassword: e.target.value,
                }))
              }
            />
          </div>
          <div className="grid gap-4 md:grid-cols-2">
            <div>
              <label className="text-sm text-neutral-500 dark:text-neutral-400">
                Новый пароль
              </label>
              <Input
                type="password"
                value={passwordForm.newPassword}
                onChange={(e) =>
                  setPasswordForm((f) => ({
                    ...f,
                    newPassword: e.target.value,
                  }))
                }
              />
            </div>
            <div>
              <label className="text-sm text-neutral-500 dark:text-neutral-400">
                Повторите новый пароль
              </label>
              <Input
                type="password"
                value={passwordForm.confirmNewPassword}
                onChange={(e) =>
                  setPasswordForm((f) => ({
                    ...f,
                    confirmNewPassword: e.target.value,
                  }))
                }
              />
            </div>
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
              {savingPassword ? "Сохранение…" : "Сменить пароль"}
            </Button>
          </div>
        </form>
      </Card>
    </div>
  );

  const renderContent = () => {
    if (activeSection === "appearance") return renderAppearance();
    if (activeSection === "solve") return renderSolve();
    if (activeSection === "fx") return renderFx();
    if (activeSection === "profile") return renderProfile();
    if (activeSection === "preview") return renderPreview();
    if (activeSection === "integrations") return renderIntegrations();
    return renderSecurity();
  };

  return (
    <Layout>
      <div className="w-full max-w-[1320px]">
        <div className="grid items-start gap-4 lg:grid-cols-[244px_minmax(0,1040px)]">
          <aside className="self-start space-y-3">
            <Card className="p-2">
              <nav className="space-y-1">
                {SECTIONS.map((section) => (
                  <button
                    key={section.key}
                    type="button"
                    onClick={() => openSection(section.key)}
                    className={
                      "w-full rounded-2xl px-4 py-3 text-left font-semibold transition " +
                      (activeSection === section.key
                        ? "bg-[rgba(var(--accent)/0.16)] text-[rgb(var(--accent))]"
                        : "text-neutral-800 hover:bg-neutral-100 dark:text-neutral-100 dark:hover:bg-neutral-800/60")
                    }
                  >
                    {section.title}
                  </button>
                ))}
              </nav>
            </Card>
            {profile ? (
              <MiniProfilePreview profile={profile} extra={extra} compact />
            ) : null}
          </aside>

          <section className="min-w-0 max-w-[1040px]">
            {loading ? (
              <div className="mb-3 text-sm text-neutral-500 dark:text-neutral-400">
                Загрузка…
              </div>
            ) : null}

            {renderContent()}

            <div className="sticky bottom-4 z-10 mt-5 flex justify-end pointer-events-none">
              <Button
                onClick={save}
                disabled={saving || !canSave}
                className="pointer-events-auto shadow-lg disabled:cursor-not-allowed disabled:opacity-55"
              >
                {saving
                  ? "Сохранение…"
                  : hasChanges
                    ? "Сохранить"
                    : "Сохранено"}
              </Button>
            </div>
          </section>
        </div>
      </div>
    </Layout>
  );
}
