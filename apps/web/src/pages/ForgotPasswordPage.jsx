import React, { useEffect, useMemo, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import { AlertTriangle, ArrowLeft, CheckCircle2, KeyRound, Send, ShieldCheck } from "lucide-react";
import { AuthApi } from "../api/auth";
import { getApiErrorMessage } from "../api/http";
import { Button, Card, Field, Input } from "../components/ui";

const formatTime = (seconds) => {
  const safe = Math.max(0, Number(seconds) || 0);
  const minutes = Math.floor(safe / 60);
  const rest = safe % 60;
  return `${minutes}:${String(rest).padStart(2, "0")}`;
};

export default function ForgotPasswordPage() {
  const [step, setStep] = useState("identity");
  const [identity, setIdentity] = useState("");
  const [verificationCode, setVerificationCode] = useState("");
  const [newPassword, setNewPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [message, setMessage] = useState("");
  const [botUsername, setBotUsername] = useState("");
  const [secondsLeft, setSecondsLeft] = useState(0);
  const [completedLogin, setCompletedLogin] = useState("");
  const [initializing, setInitializing] = useState(true);
  const navigate = useNavigate();

  useEffect(() => {
    let active = true;
    AuthApi.getPasswordRecoveryStatus()
      .then((data) => {
        if (!active) return;
        if (data?.stage === "password") {
          setIdentity(data?.identity || "");
          setStep("password");
          return;
        }
        if (data?.stage === "code") {
          setIdentity(data?.identity || "");
          setStep("code");
          setSecondsLeft(Number(data?.expiresInSeconds) || 600);
          setBotUsername(data?.botUsername || "");
          setMessage("Код восстановления уже отправлен в привязанный Telegram.");
        }
      })
      .catch(() => {})
      .finally(() => {
        if (active) setInitializing(false);
      });
    return () => {
      active = false;
    };
  }, []);

  useEffect(() => {
    if (step !== "code" || secondsLeft <= 0) return undefined;
    const timer = window.setInterval(() => {
      setSecondsLeft((value) => Math.max(0, value - 1));
    }, 1000);
    return () => window.clearInterval(timer);
  }, [step, secondsLeft]);

  const codeExpired = step === "code" && secondsLeft <= 0;
  const telegramLabel = useMemo(() => {
    if (!botUsername) return "Telegram";
    return botUsername.startsWith("@") ? botUsername : `@${botUsername}`;
  }, [botUsername]);

  const requestCode = async (event) => {
    event?.preventDefault();
    setBusy(true);
    setError("");
    setMessage("");
    try {
      const data = await AuthApi.requestPasswordRecovery(identity.trim());
      if (!data?.available) {
        setMessage(data?.message || "Извините, автоматическое восстановление невозможно.");
        setStep("unavailable");
        return;
      }

      setBotUsername(data?.botUsername || "");
      setSecondsLeft(Number(data?.expiresInSeconds) || 600);
      setVerificationCode("");
      setMessage(data?.message || "Код отправлен в Telegram.");
      setStep("code");
    } catch (err) {
      setError(getApiErrorMessage(err, "Не удалось начать восстановление. Попробуйте позже."));
    } finally {
      setBusy(false);
    }
  };

  const verifyCode = async (event) => {
    event.preventDefault();
    setBusy(true);
    setError("");
    try {
      await AuthApi.verifyPasswordRecovery(verificationCode);
      setNewPassword("");
      setConfirmPassword("");
      setStep("password");
    } catch (err) {
      setError(getApiErrorMessage(err, "Не удалось подтвердить код."));
    } finally {
      setBusy(false);
    }
  };

  const resetPassword = async (event) => {
    event.preventDefault();
    setError("");
    if (newPassword !== confirmPassword) {
      setError("Пароли не совпадают.");
      return;
    }

    setBusy(true);
    try {
      const data = await AuthApi.resetPasswordRecovery(newPassword);
      setCompletedLogin(data?.login || identity.trim());
      setNewPassword("");
      setConfirmPassword("");
      setStep("done");
    } catch (err) {
      setError(getApiErrorMessage(err, "Не удалось изменить пароль."));
    } finally {
      setBusy(false);
    }
  };

  const restart = async () => {
    try {
      await AuthApi.cancelPasswordRecovery();
    } catch {
    }
    setStep("identity");
    setVerificationCode("");
    setNewPassword("");
    setConfirmPassword("");
    setError("");
    setMessage("");
    setSecondsLeft(0);
  };

  return (
    <div className="max-w-md mx-auto">
      <Card>
        <div className="flex items-center gap-3 mb-5">
          <div className="w-11 h-11 rounded-xl border border-brand-500/30 bg-brand-500/10 flex items-center justify-center text-brand-500">
            <KeyRound size={22} />
          </div>
          <div>
            <h1 className="text-2xl font-semibold">Восстановление пароля</h1>
            <p className="text-sm text-neutral-500 mt-1">Подтверждение выполняется через привязанный Telegram.</p>
          </div>
        </div>

        {error && (
          <div role="alert" aria-live="polite" className="mb-4 rounded-xl border border-red-500/30 bg-red-500/10 px-4 py-3 text-sm text-red-500">
            {error}
          </div>
        )}

        {initializing && (
          <div className="py-10 text-center text-sm text-neutral-500">Проверяем незавершённое восстановление…</div>
        )}

        {!initializing && step === "identity" && (
          <form onSubmit={requestCode} className="space-y-4">
            <Field label="Логин или email" hint="Укажите данные аккаунта, пароль от которого нужно восстановить.">
              <Input
                value={identity}
                onChange={(event) => setIdentity(event.target.value)}
                autoComplete="username"
                placeholder="krytoichel или krytoichel@example.com"
                required
                autoFocus
              />
            </Field>

            <div className="rounded-xl border border-neutral-500/20 bg-neutral-500/5 px-4 py-3 text-sm text-neutral-500 leading-5">
              Код придёт только в Telegram, который уже привязан к этому аккаунту. Без привязанного Telegram автоматическое восстановление невозможно.
            </div>

            <Button type="submit" disabled={busy || !identity.trim()} className="w-full justify-center">
              <Send size={16} />
              <span className="ml-2">{busy ? "Отправляем…" : "Отправить код в Telegram"}</span>
            </Button>
          </form>
        )}

        {!initializing && step === "code" && (
          <form onSubmit={verifyCode} className="space-y-4">
            <div className="rounded-xl border border-emerald-500/30 bg-emerald-500/10 px-4 py-3 text-sm text-emerald-500 leading-5">
              {message} Откройте чат с {telegramLabel} и введите восьмизначный код ниже.
            </div>

            <Field label="Код из Telegram" hint={codeExpired ? "Срок действия кода истёк." : `Код действует ещё ${formatTime(secondsLeft)}.`}>
              <Input
                value={verificationCode}
                onChange={(event) => setVerificationCode(event.target.value.replace(/\D/g, "").slice(0, 8))}
                inputMode="numeric"
                autoComplete="one-time-code"
                placeholder="00000000"
                className="text-center tracking-[0.35em] text-lg"
                required
                autoFocus
                disabled={codeExpired}
              />
            </Field>

            {!codeExpired ? (
              <Button type="submit" disabled={busy || verificationCode.length !== 8} className="w-full justify-center">
                <ShieldCheck size={16} />
                <span className="ml-2">{busy ? "Проверяем…" : "Подтвердить код"}</span>
              </Button>
            ) : (
              <Button type="button" onClick={requestCode} disabled={busy} className="w-full justify-center">
                <Send size={16} />
                <span className="ml-2">{busy ? "Отправляем…" : "Отправить новый код"}</span>
              </Button>
            )}

            <button type="button" onClick={restart} className="w-full text-sm text-neutral-500 hover:text-neutral-200 transition">
              Указать другой аккаунт
            </button>
          </form>
        )}

        {!initializing && step === "password" && (
          <form onSubmit={resetPassword} className="space-y-4">
            <div className="rounded-xl border border-emerald-500/30 bg-emerald-500/10 px-4 py-3 text-sm text-emerald-500 flex items-start gap-2">
              <ShieldCheck size={18} className="mt-0.5 shrink-0" />
              <span>Telegram подтверждён. Задайте новый пароль для аккаунта.</span>
            </div>

            <Field label="Новый пароль" hint="Минимум 8 символов.">
              <Input
                type="password"
                value={newPassword}
                onChange={(event) => setNewPassword(event.target.value)}
                autoComplete="new-password"
                required
                minLength={8}
                autoFocus
              />
            </Field>

            <Field label="Повторите новый пароль">
              <Input
                type="password"
                value={confirmPassword}
                onChange={(event) => setConfirmPassword(event.target.value)}
                autoComplete="new-password"
                required
                minLength={8}
              />
            </Field>

            <Button type="submit" disabled={busy || newPassword.length < 8 || confirmPassword.length < 8} className="w-full justify-center">
              <KeyRound size={16} />
              <span className="ml-2">{busy ? "Сохраняем…" : "Изменить пароль"}</span>
            </Button>
          </form>
        )}

        {!initializing && step === "unavailable" && (
          <div className="space-y-4">
            <div className="rounded-xl border border-amber-500/30 bg-amber-500/10 px-4 py-4 text-sm text-amber-500 leading-5 flex items-start gap-3">
              <AlertTriangle size={20} className="mt-0.5 shrink-0" />
              <span>{message || "Извините, автоматическое восстановление невозможно."}</span>
            </div>
            <Button type="button" variant="outline" onClick={restart} className="w-full justify-center">
              Попробовать другой аккаунт
            </Button>
          </div>
        )}

        {!initializing && step === "done" && (
          <div className="space-y-4 text-center">
            <CheckCircle2 size={46} className="mx-auto text-emerald-500" />
            <div>
              <h2 className="text-xl font-semibold">Пароль восстановлен</h2>
              <p className="text-sm text-neutral-500 mt-2">Теперь можно войти с новым паролем.</p>
              {completedLogin && <p className="text-sm mt-2">Логин: <strong>{completedLogin}</strong></p>}
            </div>
            <Button
              type="button"
              onClick={() => navigate("/login", { replace: true, state: { login: completedLogin } })}
              className="w-full justify-center"
            >
              Перейти ко входу
            </Button>
          </div>
        )}
      </Card>

      <div className="mt-4 text-sm text-neutral-500 flex items-center justify-between gap-4">
        <Link to="/login" className="inline-flex items-center gap-1 text-brand-600 hover:underline">
          <ArrowLeft size={15} /> Вернуться ко входу
        </Link>
        <Link to="/register" className="text-brand-600 hover:underline">Регистрация</Link>
      </div>
    </div>
  );
}
