import React, { useState, useEffect } from "react";
import Layout from "../components/Layout";
import { Field, Input, Button, Card } from "../components/ui";
import { useAuth } from "../auth/AuthContext";
import { useLocation, useNavigate, Link } from "react-router-dom";
import { LogIn } from "lucide-react";
import { getApiErrorMessage } from "../api/http";

export default function LoginPage() {
  const { login, access } = useAuth();      
  const [loginName, setLoginName] = useState("");
  const [password, setPassword] = useState("");
  const [err, setErr] = useState("");
  const [busy, setBusy] = useState(false);
  const nav = useNavigate();
  const loc = useLocation();
  const from = loc.state?.from?.pathname || "/courses";

  const handleSubmit = async (e) => {
    e.preventDefault();
    setBusy(true);
    setErr("");

    try {
      
      await login(loginName.trim(), password);
      
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
    <Layout>
      <div className="max-w-md mx-auto">
        <Card>
          <h1 className="text-2xl font-semibold mb-4">Вход</h1>

          {err && (
            <div className="text-red-600 mb-3 text-sm leading-5">
              {err}
            </div>
          )}

          <form onSubmit={handleSubmit} className="space-y-4">
            <Field label="Логин или email">
              <Input
                type="text"
                value={loginName}
                onChange={(e) => setLoginName(e.target.value)}
                required
                autoComplete="username"
                placeholder="pivasya или pivasya@example.com"
              />
            </Field>

            <Field label="Пароль">
              <Input
                type="password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                required
                autoComplete="current-password"
              />
            </Field>

            <Button disabled={busy} className="w-full">
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
    </Layout>
  );
}
