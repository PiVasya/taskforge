import React, { useEffect, useState } from "react";
import Layout from "../components/Layout";
import { Card, Button, Input, Badge } from "../components/ui";
import { getCourses, createCourse } from "../api/courses";
import { Link, useNavigate } from "react-router-dom";
import { Plus } from "lucide-react";

export default function CoursesPage() {
    const [items, setItems] = useState([]);
    const [q, setQ] = useState("");
    const [loading, setLoading] = useState(true);
    const [err, setErr] = useState("");
    const nav = useNavigate();

    useEffect(() => {
        (async () => {
            try { setLoading(true); setErr(""); setItems(await getCourses()); }
            catch (e) { setErr(e.message || "Не удалось загрузить курсы"); }
            finally { setLoading(false); }
        })()
    }, []);

    const filtered = items.filter(c =>
        (c.title || "").toLowerCase().includes(q.toLowerCase()) ||
        (c.description || "").toLowerCase().includes(q.toLowerCase())
    );

    const handleCreate = async () => {
        const { id } = await createCourse({ title: "Новый курс", description: "" });
        nav(`/courses/${id}/edit`);
    };

    return (
        <Layout>
            <div className="flex items-center justify-between mb-6">
                <h1 className="text-2xl font-semibold">Курсы</h1>
                <Button onClick={handleCreate}><Plus size={16} /> Создать курс</Button>
            </div>

            <Card className="mb-6">
                <Input placeholder="Поиск по курсам…" value={q} onChange={e => setQ(e.target.value)} />
            </Card>

            {err && <div className="text-red-500 mb-4">{err}</div>}
            {loading && <div className="text-slate-500">Загрузка…</div>}

            <div className="grid md:grid-cols-2 xl:grid-cols-3 gap-5">
                {filtered.map(c => (
                    <Card key={c.id} className="p-5 hover:shadow-lg transition">
                        <div className="flex items-start justify-between gap-3">
                            <div>
                                <Link to={`/courses/${c.id}/edit`} className="text-lg font-semibold hover:underline">{c.title}</Link>
                                {c.description && <p className="text-sm text-slate-500 mt-1 line-clamp-2">{c.description}</p>}
                                <div className="mt-3 flex items-center gap-2 text-sm text-slate-500">
                                    <Badge>Заданий: {c.assignmentCount ?? "—"}</Badge>
                                    {typeof c.solvedCountForCurrentUser === "number" && <Badge>Решено: {c.solvedCountForCurrentUser}</Badge>}
                                </div>
                            </div>
                        </div>
                    </Card>
                ))}
            </div>

            {!loading && filtered.length === 0 && <div className="card-muted p-8 mt-6 text-center text-slate-500">Пусто</div>}
        </Layout>
    );
}
