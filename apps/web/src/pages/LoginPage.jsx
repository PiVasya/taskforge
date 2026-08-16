import React, { useState, useEffect } from "react";
import { Field, Input, Button, Card } from "../components/ui";
import { useAuth } from "../auth/AuthContext";
import { useLocation, useNavigate, Link } from "react-router-dom";
import { KeyRound, LogIn } from "lucide-react";
import { getApiErrorMessage } from "../api/http";
import { locationToInternalPath, safeInternalPath } from "../auth/authRedirect";

export default function LoginPage() {
  const { login, access } = useAuth();      
  const [loginName, setLoginName] = useState("");
  const [password, setPassword] = useState("");
  const [err, setErr] = useState("");
  const [busy, setBusy] = useState(false);
  const nav = useNavigate();
  const loc = useLocation();
  const requestedNext = new URLSearchParams(loc.search).get("next");
  const stateNext = locationToInternalPath(loc.state?.from);
  const from = safeInternalPath(requestedNext || stateNext, "/courses");

  useEffect(() => {
    if (loc.state?.login) setLoginName(loc.state.login);
  }, [loc.state?.login]);

  const handleSubmit = async (e) => {
    e.preventDefault();
    setErr("");

    const normalizedLogin = loginName.trim();
    if (!normalizedLogin && !password) {
      setErr("Введите логин или email и пароль.");
      return;
    }
    if (!normalizedLogin) {
      setErr("Введите логин или email.");
      return;
    }
    if (!password) {
      setErr("Введите пароль.");
      return;
    }

    setBusy(true);

    try {
      await login(normalizedLogin, password);
    } catch (e) {
      setErr(getApiErrorMessage(e, "Неверный логин/email или пароль. Проверьте данные или зарегистрируйтесь."));
    } finally {
      setBusy(false);
    }
  };

  
  useEffect(() => {
    if (access) {
      nav(from, { replace: true });
    }
  }, [access, nav, from]);
  
  return (
    <>
      <div className="max-w-md mx-auto">
        <Card>
          <h1 className="text-2xl font-semibold mb-4">Вход</h1>

          {err && (
            <div role="alert" aria-live="polite" className="text-red-600 mb-3 text-sm leading-5">
              {err}
            </div>
          )}

          <form onSubmit={handleSubmit} noValidate className="space-y-4">
            <Field label="Логин или email">
              <Input
                type="text"
                value={loginName}
                onChange={(e) => setLoginName(e.target.value)}
                required
                aria-invalid={Boolean(err && !loginName.trim())}
                autoComplete="username"
                placeholder="krytoichel или krytoichel@example.com"
                data-taskforge-automation-id="login-identity"
                data-taskforge-agent-role="login-identity"
                data-taskforge-agent-action="fill"
              />
            </Field>

            <Field label="Пароль">
              <Input
                type="password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                required
                aria-invalid={Boolean(err && !password)}
                autoComplete="current-password"
                data-taskforge-automation-id="login-password"
                data-taskforge-agent-role="login-password"
                data-taskforge-agent-action="fill"
              />
            </Field>

            <div className="flex justify-end -mt-2">
              <Link to="/forgot-password" className="inline-flex items-center gap-1 text-sm text-brand-600 hover:underline">
                <KeyRound size={14} /> Забыли пароль?
              </Link>
            </div>

            <Button
              disabled={busy}
              className="w-full"
              data-taskforge-automation-id="login-submit"
              data-taskforge-agent-role="login-submit"
              data-taskforge-agent-action="login"
            >
              {busy ? "Входим…" : (
                <>
                  <LogIn size={16} /> <span className="ml-1">Войти</span>
                </>
              )}
            </Button>
          </form>
        </Card>

        <div className="mt-4 text-sm text-neutral-500">
          Нет аккаунта?{" "}
          <Link to="/register" className="text-brand-600 hover:underline">
            Зарегистрироваться
          </Link>
        </div>
      </div>
    </>
  );
}
