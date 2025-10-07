import React, { useEffect, useState } from "react";
import Layout from "../components/Layout";
import { Field, Input, Textarea, Button, Card } from "../components/ui";
import { getCourse, updateCourse, deleteCourse } from "../api/courses";
import { Link, useNavigate, useParams } from "react-router-dom";
import { Save, Trash2, ArrowLeft, Layers } from "lucide-react";

// helper: userId из JWT
function getCurrentUserIdFromToken() {
  try {
    const raw =
      localStorage.getItem("access_token") ||
      localStorage.getItem("token") ||
      sessionStorage.getItem("access_token") ||
      sessionStorage.getItem("token");
    if (!raw) return null;
    const parts = raw.split(".");
    if (parts.length < 2) return null;
    const payload = JSON.parse(
      atob(parts[1].replace(/-/g, "+").replace(/_/g, "/"))
    );
    return payload.sub || payload.nameid || payload.uid || payload.userId || null;
  } catch {
    return null;
  }
}

export default function CourseEditPage() {
  const { courseId } = useParams();
  const nav = useNavigate();

  const [title, setTitle] = useState("");
  const [description, setDescription] = useState("");
  const [isPublic, setIsPublic] = useState(false);

  const [err, setErr] = useState("");
  const [busy, setBusy] = useState(false);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    (async () => {
      try {
        setErr("");
        setLoading(true);
        const c = await getCourse(courseId);

        // гард: не владелец — не пускаем
        const myId = getCurrentUserIdFromToken();
        const isOwner =
          myId &&
          c?.ownerId &&
          String(c.ownerId).toLowerCase() === String(myId).toLowerCase();
        if (!isOwner) {
          setErr("Это чужой курс. Редактирование недоступно.");
          setTimeout(() => nav(`/course/${courseId}`), 600);
          return;
        }

        setTitle(c.title || "");
        setDescription(c.description || "");
        setIsPublic(!!c.isPublic);
      } catch (e) {
        setErr(e.message || "Ошибка загрузки курса");
      } finally {
        setLoading(false);
      }
    })();
  }, [courseId, nav]);

  const save = async () => {
    setBusy(true);
    setErr("");
    try {
      await updateCourse(courseId, { title, description, isPublic });
      nav(`/course/${courseId}`);
    } catch (e) {
      const msg =
        e && e.response && e.response.status === 403
          ? (e.response.data && e.response.data.message) || "Вы не владелец курса"
          : e.message || "Ошибка сохранения";
      setErr(msg);
    } finally {
      setBusy(false);
    }
  };

  const remove = async () => {
    if (!window.confirm("Удалить курс?")) return;
    try {
      await deleteCourse(courseId);
      nav("/courses");
    } catch (e) {
      const msg =
        e && e.response && e.response.status === 403
          ? (e.response.data && e.response.data.message) || "Вы не владелец курса"
          : e.message || "Ошибка удаления";
      setErr(msg);
    }
  };

  return (
    <Layout>
      <div className="flex items-center justify-between mb-6">
        <div className="flex items-center gap-2">
          <Layers size={20} />
          <h1 className="text-2xl font-semibold">Редактирование курса</h1>
        </div>
        <Link to={`/course/${courseId}`} className="text-brand-600 hover:underline">
          <ArrowLeft className="inline" size={16} /> к заданиям
        </Link>
      </div>

      {err && <div className="text-red-500 mb-4">{err}</div>}
      {loading && <div className="text-slate-500 mb-4">Загрузка…</div>}

      <div className="grid lg:grid-cols-3 gap-6">
        <div className="lg:col-span-2">
          <Card>
            <div className="grid sm:grid-cols-2 gap-4">
              <Field label="Название">
                <Input
                  value={title}
                  onChange={(e) => setTitle(e.target.value)}
                  placeholder="Например: Основы C++"
                />
              </Field>

              <div className="sm:col-span-2">
                <Field label="Описание">
                  <Textarea
                    rows={8}
                    value={description}
                    onChange={(e) => setDescription(e.target.value)}
                    placeholder="Кратко опишите, чему посвящён курс…"
                  />
                </Field>
              </div>

              <div className="sm:col-span-2">
                <label className="flex items-center gap-3 select-none">
                  <input
                    type="checkbox"
                    className="h-4 w-4 rounded border-slate-300"
                    checked={isPublic}
                    onChange={(e) => setIsPublic(e.target.checked)}
                  />
                  <span className="text-sm">
                    Публичный курс <span className="text-slate-500">(виден всем)</span>
                  </span>
                </label>
              </div>
            </div>
          </Card>
        </div>

        <div className="space-y-4">
          <Card>
            <div className="flex gap-2">
              <Button onClick={save} disabled={busy} className="flex-1">
                <Save size={16} /> {busy ? "Сохраняю…" : "Сохранить"}
              </Button>
              <Button
                variant="outline"
                onClick={remove}
                className="flex-1 text-red-600 border-red-300 hover:bg-red-50 dark:hover:bg-red-900/20"
              >
                <Trash2 size={16} /> Удалить
              </Button>
            </div>
          </Card>
        </div>
      </div>
    </Layout>
  );
}
