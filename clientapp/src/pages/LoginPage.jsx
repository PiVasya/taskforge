import React, { useState } from "react";
import Layout from "../components/Layout";
import { Field, Input, Button, Card } from "../components/ui";
import { useAuth } from "../auth/AuthContext";
import { useLocation, useNavigate } from "react-router-dom";
import { LogIn } from "lucide-react";

export default function LoginPage() {
    const { login } = useAuth();
    const [email, setEmail] = useState("");
    const [password, setPassword] = useState("");
    const [err, setErr] = useState("");
    const [busy, setBusy] = useState(false);
    const nav = useNavigate();
    const loc = useLocation();
    const from = loc.state?.from?.pathname || "/courses";

    const handleSubmit = async (e) => {
        e.preventDefault();
        setBusy(true); setErr("");
        try { await login(email, password); nav(from, { replace: true }); }
        catch (e) { setErr(e?.response?.data || e.message || "Ошибка входа"); }
        finally { setBusy(false); }
    };

    return (
        <Layout>
            <div className="max-w-md mx-auto">
                <Card>
                    <h1 className="text-2xl font-semibold mb-4">Вход</h1>
                    {err && <div className="text-red-500 mb-3">{String(err)}</div>}
                    <form onSubmit={handleSubmit} className="space-y-4">
                        <Field label="Email"><Input type="email" value={email} onChange={e => setEmail(e.target.value)} required /></Field>
                        <Field label="Пароль"><Input type="password" value={password} onChange={e => setPassword(e.target.value)} required /></Field>
                        <Button disabled={busy} className="w-full">{busy ? "Входим…" : (<><LogIn size={16} /> Войти</>)}</Button>
                    </form>
                </Card>
            </div>
        </Layout>
    );
}
