import React, { useEffect, useState } from "react";
import Layout from "../components/Layout";
import { Field, Input, Textarea, Button, Card } from "../components/ui";
import { getCourse, updateCourse, deleteCourse } from "../api/courses";
import { Link, useNavigate, useParams } from "react-router-dom";
import { Save, Trash2, ArrowLeft, Layers } from "lucide-react";

export default function CourseEditPage() {
    const { courseId } = useParams();
    const [title, setTitle] = useState("");
    const [description, setDescription] = useState("");
    const [err, setErr] = useState("");
    const [busy, setBusy] = useState(false);
    const nav = useNavigate();

    useEffect(() => {
        (async () => {
            try { setErr(""); const c = await getCourse(courseId); setTitle(c.title || ""); setDescription(c.description || ""); }
            catch (e) { setErr(e.message || "Ошибка загрузки курса"); }
        })()
    }, [courseId]);

    const save = async () => {
        setBusy(true); setErr("");
        try { await updateCourse(courseId, { title, description }); nav(`/course/${courseId}`); }
        catch (e) { setErr(e.message || "Ошибка сохранения"); }
        finally { setBusy(false); }
    };

    const remove = async () => {
        if (!window.confirm("Удалить курс?")) return;
        try { await deleteCourse(courseId); nav("/courses"); }
        catch (e) { setErr(e.message || "Ошибка удаления"); }
    };

    return (
        <Layout>
            <div className="flex items-center justify-between mb-6">
                <div className="flex items-center gap-2">
                    <Layers size={20} /> <h1 className="text-2xl font-semibold">Редактирование курса</h1>
                </div>
                <Link to={`/course/${courseId}`} className="text-brand-600 hover:underline"><ArrowLeft className="inline" size={16} /> к заданиям</Link>
            </div>

            {err && <div className="text-red-500 mb-4">{err}</div>}

            <div className="grid lg:grid-cols-3 gap-6">
                <div className="lg:col-span-2">
                    <Card>
                        <div className="grid sm:grid-cols-2 gap-4">
                            <Field label="Название"><Input value={title} onChange={e => setTitle(e.target.value)} /></Field>
                            <div className="sm:col-span-2"><Field label="Описание"><Textarea rows={8} value={description} onChange={e => setDescription(e.target.value)} /></Field></div>
                        </div>
                    </Card>
                </div>
                <div className="space-y-4">
                    <Card>
                        <div className="flex gap-2">
                            <Button onClick={save} disabled={busy} className="flex-1"><Save size={16} /> {busy ? 'Сохраняю…' : 'Сохранить'}</Button>
                            <Button variant="outline" onClick={remove} className="flex-1 text-red-600 border-red-300 hover:bg-red-50 dark:hover:bg-red-900/20"><Trash2 size={16} /> Удалить</Button>
                        </div>
                    </Card>
                </div>
            </div>
        </Layout>
    );
}
