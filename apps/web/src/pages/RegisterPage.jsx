import React, { useEffect, useMemo, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import Layout from "../components/Layout";
import { Card, Field, Input, Button, Textarea } from "../components/ui";
import { useAuth } from "../auth/AuthContext";
import { registerUser } from "../api/auth";
import { UserPlus, LogIn, ChevronDown } from "lucide-react";
import { getApiErrorMessage } from "../api/http";

const LOGIN_RE = /^[a-zA-Z0-9_.-]{3,64}$/;

function buildAdditionalDataJson(fields) {
    const skills = (fields.skillsText || "")
        .split(",")
        .map((x) => x.trim())
        .filter(Boolean);

    const obj = {
        bio: fields.bio.trim(),
        location: fields.studyPlace.trim(),
        education: fields.education.trim(),
        links: {
            github: null,
            telegram: null,
            website: null,
        },
        skills,
        showInLeaderboard: true,
    };

    return JSON.stringify(obj);
}

export default function RegisterPage() {
    const nav = useNavigate();
    const { login: signIn, access } = useAuth();

    const [login, setLogin] = useState("");
    const [password, setPassword] = useState("");
    const [password2, setPassword2] = useState("");
    const [firstName, setFirstName] = useState("");
    const [lastName, setLastName] = useState("");

    const [showExtra, setShowExtra] = useState(false);
    const [email, setEmail] = useState("");
    const [phoneNumber, setPhoneNumber] = useState("");
    const [studyPlace, setStudyPlace] = useState("");
    const [education, setEducation] = useState("");
    const [skillsText, setSkillsText] = useState("");
    const [bio, setBio] = useState("");

    const [acceptedPolicy, setAcceptedPolicy] = useState(false);
    const [err, setErr] = useState("");
    const [busy, setBusy] = useState(false);

    const normalizedLogin = login.trim();
    const loginLooksOk = LOGIN_RE.test(normalizedLogin);

    const canSubmit =
        loginLooksOk &&
        password.length >= 8 &&
        password === password2 &&
        firstName.trim() &&
        acceptedPolicy;

    const passwordHint = useMemo(() => {
        if (!password) return "Минимум 8 символов.";
        if (password.length < 8) return "Сейчас меньше 8 символов.";
        if (password2 && password !== password2) return "Пароли не совпадают.";
        return "Пароль подходит.";
    }, [password, password2]);

    const onSubmit = async (e) => {
        e.preventDefault();
        if (!canSubmit) return;

        setBusy(true);
        setErr("");
        try {
            await registerUser({
                login: normalizedLogin,
                email: email.trim() || null,
                password,
                firstName: firstName.trim(),
                lastName: lastName.trim(),
                phoneNumber: phoneNumber.trim() || null,
                additionalDataJson: buildAdditionalDataJson({ studyPlace, education, skillsText, bio }),
            });

            try {
                await signIn(normalizedLogin, password);
            } catch (eLogin) {
                nav("/login", { replace: true });
            }
        } catch (e2) {
            setErr(getApiErrorMessage(e2, "Не удалось зарегистрироваться. Проверьте данные и попробуйте ещё раз."));
        } finally {
            setBusy(false);
        }
    };

    useEffect(() => {
        if (access) {
            nav("/courses", { replace: true });
        }
    }, [access, nav]);

    return (
        <Layout>
            <div className="max-w-2xl mx-auto">
                <form onSubmit={onSubmit} className="space-y-4">
                    <Card className="p-6">
                        <h1 className="text-2xl font-semibold mb-4 flex items-center gap-2">
                            <UserPlus size={20} /> Регистрация
                        </h1>

                        {err && <div className="text-red-500 mb-3">{err}</div>}

                        <div className="grid gap-4">
                            <Field
                                label="Логин"
                                hint="Он используется для входа. Почта больше не обязательна. Разрешены латинские буквы, цифры, точка, дефис и подчёркивание."
                            >
                                <Input
                                    value={login}
                                    onChange={(e) => setLogin(e.target.value)}
                                    autoComplete="username"
                                    placeholder="pivasya"
                                />
                                {normalizedLogin && !loginLooksOk && (
                                    <div className="mt-1 text-xs text-red-500">
                                        Логин должен быть от 3 до 64 символов: a-z, 0-9, точка, дефис или подчёркивание.
                                    </div>
                                )}
                            </Field>

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

                            <div className="grid sm:grid-cols-2 gap-4">
                                <Field label="Пароль" hint={passwordHint}>
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
                        </div>
                    </Card>

                    <Card className="p-0 overflow-hidden">
                        <button
                            type="button"
                            className="w-full flex items-center justify-between gap-3 px-6 py-4 text-left hover:bg-neutral-50 dark:hover:bg-neutral-900/60"
                            onClick={() => setShowExtra((v) => !v)}
                        >
                            <div>
                                <div className="font-semibold">Дополнительная информация</div>
                                <div className="text-sm text-neutral-500 dark:text-neutral-400">
                                    Не обязательно. Можно сразу указать почту, место учёбы, группу и навыки.
                                </div>
                            </div>
                            <ChevronDown
                                size={18}
                                className={`shrink-0 transition-transform ${showExtra ? "rotate-180" : ""}`}
                            />
                        </button>

                        {showExtra && (
                            <div className="border-t border-neutral-200 dark:border-neutral-800 px-6 py-5 grid gap-4">
                                <div className="grid sm:grid-cols-2 gap-4">
                                    <Field label="Email (не обязательно)">
                                        <Input
                                            type="email"
                                            value={email}
                                            onChange={(e) => setEmail(e.target.value)}
                                            autoComplete="email"
                                            placeholder="you@example.com"
                                        />
                                    </Field>
                                    <Field label="Телефон (не обязательно)">
                                        <Input
                                            value={phoneNumber}
                                            onChange={(e) => setPhoneNumber(e.target.value)}
                                            autoComplete="tel"
                                            placeholder="+375..."
                                        />
                                    </Field>
                                </div>

                                <div className="grid sm:grid-cols-2 gap-4">
                                    <Field label="Город / место учёбы">
                                        <Input
                                            value={studyPlace}
                                            onChange={(e) => setStudyPlace(e.target.value)}
                                            placeholder="Минск, БГУИР"
                                        />
                                    </Field>
                                    <Field label="Образование / группа">
                                        <Input
                                            value={education}
                                            onChange={(e) => setEducation(e.target.value)}
                                            placeholder="Факультет АИС, ITD-21"
                                        />
                                    </Field>
                                </div>

                                <Field label="Навыки">
                                    <Input
                                        value={skillsText}
                                        onChange={(e) => setSkillsText(e.target.value)}
                                        placeholder="C#, C++, SQL, React"
                                    />
                                </Field>

                                <Field label="О себе">
                                    <Textarea
                                        rows={3}
                                        value={bio}
                                        onChange={(e) => setBio(e.target.value)}
                                        placeholder="Коротко о себе, если нужно показать это в профиле."
                                    />
                                </Field>
                            </div>
                        )}
                    </Card>

                    <Card className="p-6">
                        <div className="text-sm text-neutral-600 dark:text-neutral-400 flex items-start gap-2">
                            <input
                                id="policyAgreement"
                                type="checkbox"
                                className="mt-1"
                                checked={acceptedPolicy}
                                onChange={(e) => setAcceptedPolicy(e.target.checked)}
                            />
                            <label htmlFor="policyAgreement" className="leading-5">
                                Я соглашаюсь с{' '}
                                <Link to="/privacy" className="text-brand-600 hover:underline">
                                    политикой конфиденциальности
                                </Link>
                            </label>
                        </div>

                        <Button type="submit" disabled={!canSubmit || busy} className="mt-4 w-full">
                            {busy ? "Создаю аккаунт…" : "Зарегистрироваться"}
                        </Button>

                        <div className="mt-4 text-sm text-neutral-500">
                            Уже есть аккаунт?{" "}
                            <Link to="/login" className="text-brand-600 hover:underline">
                                <LogIn className="inline -mt-1 mr-1" size={16} />
                                Войти
                            </Link>
                        </div>
                    </Card>
                </form>
            </div>
        </Layout>
    );
}
