import React, { useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import Layout from "../components/Layout";
import { Card, Field, Input, Button } from "../components/ui";
import { useAuth } from "../auth/AuthContext";
import { registerUser } from "../api/auth";
import { UserPlus, LogIn } from "lucide-react";

export default function RegisterPage() {
    const nav = useNavigate();
    const { setTokens } = useAuth?.() || {}; // если контекст отдает сеттер токенов

    const [email, setEmail] = useState("");
    const [password, setPassword] = useState("");
    const [password2, setPassword2] = useState("");
    const [firstName, setFirstName] = useState("");
    const [lastName, setLastName] = useState("");

    const [err, setErr] = useState("");
    const [busy, setBusy] = useState(false);

    const canSubmit =
        email.trim() &&
        password.length >= 6 &&
        password === password2 &&
        firstName.trim();

    const onSubmit = async (e) => {
        e.preventDefault();
        if (!canSubmit) return;

        setBusy(true);
        setErr("");
        try {
            // ожидаем, что API вернет токены как и login (access/refresh),
            // если нет — просто редирект на /login
            const res = await registerUser({
                email: email.trim(),
                password,
                firstName: firstName.trim(),
                lastName: lastName.trim(),
            });

            if (res?.access && res?.refresh && setTokens) {
                setTokens(res.access, res.refresh);
                nav("/courses", { replace: true });
            } else {
                nav("/login", { replace: true });
            }
        } catch (e2) {
            setErr(
                e2?.response?.data?.title ||
                e2?.message ||
                "Не удалось зарегистрироваться"
            );
        } finally {
            setBusy(false);
        }
    };

    return (
        <Layout>
            <div className="max-w-lg mx-auto">
                <Card className="p-6">
                    <h1 className="text-2xl font-semibold mb-4 flex items-center gap-2">
                        <UserPlus size={20} /> Регистрация
                    </h1>

                    {err && <div className="text-red-500 mb-3">{err}</div>}

                    <form onSubmit={onSubmit} className="grid gap-4">
                        <div className="grid sm:grid-cols-2 gap-4">
                            <Field label="Имя">
                                <Input
                                    value={firstName}
                                    onChange={(e) => setFirstName(e.target.value)}
                                    autoComplete="given-name"
                                />
                            </Field>
                            <Field label="Фамилия">
                                <Input
                                    value={lastName}
                                    onChange={(e) => setLastName(e.target.value)}
                                    autoComplete="family-name"
                                />
                            </Field>
                        </div>

                        <Field label="Email">
                            <Input
                                type="email"
                                value={email}
                                onChange={(e) => setEmail(e.target.value)}
                                autoComplete="email"
                                placeholder="you@example.com"
                            />
                        </Field>

                        <div className="grid sm:grid-cols-2 gap-4">
                            <Field label="Пароль (минимум 6 символов)">
                                <Input
                                    type="password"
                                    value={password}
                                    onChange={(e) => setPassword(e.target.value)}
                                    autoComplete="new-password"
                                />
                            </Field>
                            <Field label="Повторите пароль">
                                <Input
                                    type="password"
                                    value={password2}
                                    onChange={(e) => setPassword2(e.target.value)}
                                    autoComplete="new-password"
                                />
                            </Field>
                        </div>

                        <Button type="submit" disabled={!canSubmit || busy}>
                            {busy ? "Создаю аккаунт…" : "Зарегистрироваться"}
                        </Button>
                    </form>

                    <div className="mt-4 text-sm text-slate-500">
                        Уже есть аккаунт?{" "}
                        <Link to="/login" className="text-brand-600 hover:underline">
                            <LogIn className="inline -mt-1 mr-1" size={16} />
                            Войти
                        </Link>
                    </div>
                </Card>
            </div>
        </Layout>
    );
}
