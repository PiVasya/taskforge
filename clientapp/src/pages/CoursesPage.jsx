import React, { useEffect, useMemo, useState } from "react";
import Layout from "../components/Layout";
import { Card, Button, Input, Badge } from "../components/ui";
import { getCourses, createCourse } from "../api/courses";
import { Link, useNavigate } from "react-router-dom";
import { Plus } from "lucide-react";
import { useEditorMode } from "../contexts/EditorModeContext";

export default function CoursesPage() {
  const [items, setItems] = useState([]);
  const [q, setQ] = useState("");
  const [loading, setLoading] = useState(true);
  const [err, setErr] = useState("");

  const nav = useNavigate();
  const { canEdit, isEditorMode } = useEditorMode();

  useEffect(() => {
    (async () => {
      try {
        setLoading(true);
        setErr("");
        const list = await getCourses();
        setItems(Array.isArray(list) ? list : []);
      } catch (e) {
        setErr(e?.userMessage || e?.message || "Не удалось загрузить курсы");
      } finally {
        setLoading(false);
      }
    })();
  }, []);

  const filtered = useMemo(() => {
    const qq = q.toLowerCase();
    return (items || []).filter((c) =>
      ((c.title || "") + " " + (c.description || "")).toLowerCase().includes(qq)
    );
  }, [items, q]);

  const handleCreate = async () => {
    try {
      const { id } = await createCourse({
        title: "Новый курс",
        description: "Описание курса",
        isPublic: false,
        visibleGroupIds: [],
        ownerIds: [],
      });
      nav(`/courses/${id}/edit`);
    } catch (e) {
      setErr(e?.userMessage || e?.message || "Не удалось создать курс");
    }
  };

  return (
    <Layout>
      <div className="flex items-center justify-between mb-6">
        <h1 className="text-2xl font-semibold">Курсы</h1>

        {canEdit && isEditorMode && (
          <Button onClick={handleCreate} title="Создать курс">
            <Plus size={16} />
            <span className="ml-1">Создать курс</span>
          </Button>
        )}
      </div>

      <Card className="mb-6">
        <Input
          placeholder="Поиск по названию/описанию…"
          value={q}
          onChange={(e) => setQ(e.target.value)}
        />
      </Card>

      {err && <div className="text-red-500 mb-4">{err}</div>}
      {loading && <div className="text-slate-500">Загрузка…</div>}

      <div className="grid md:grid-cols-2 xl:grid-cols-3 gap-5">
        {filtered.map((c) => {
          const href = canEdit && isEditorMode && c.canEdit ? `/courses/${c.id}/edit` : `/course/${c.id}`;
          const showOwnerBadge = canEdit && isEditorMode;
          const ownerBadge = showOwnerBadge ? (
            c.canEdit ? (
              <Badge intent="success">мой</Badge>
            ) : (
              <Badge intent="danger">чужой</Badge>
            )
          ) : null;

          return (
            <Link
              key={c.id}
              to={href}
              className="block group focus:outline-none focus:ring-2 focus:ring-[rgb(var(--accent))] rounded-2xl"
            >
              <Card
                className={
                  "p-5 transition hover:shadow-lg cursor-pointer " +
                  (c.isCompletedForCurrentUser ? "border-emerald-400/40 bg-emerald-500/5" : "") +
                  (showOwnerBadge ? (c.canEdit ? " border-emerald-400/20" : " border-red-400/20") : "")
                }
              >
                <div className="flex items-start justify-between gap-3">
                  <div>
                    <div className="text-lg font-semibold">{c.title}</div>
                    {c.description && (
                      <p className="text-sm text-slate-500 mt-1 line-clamp-2">{c.description}</p>
                    )}

                    <div className="mt-3 flex flex-wrap items-center gap-2 text-sm text-slate-500">
                      <Badge>Заданий: {c.assignmentCount ?? "—"}</Badge>
                      <Badge>Тестов: {c.testCount ?? "—"}</Badge>
                      {typeof c.solvedCountForCurrentUser === "number" && (
                        <Badge>Код решено: {c.solvedCountForCurrentUser}</Badge>
                      )}
                      {typeof c.solvedTestsCountForCurrentUser === "number" && (
                        <Badge>Тесты решено: {c.solvedTestsCountForCurrentUser}</Badge>
                      )}
                      {c.isCompletedForCurrentUser ? <Badge intent="success">Курс пройден</Badge> : null}
                      {ownerBadge}
                    </div>
                  </div>
                </div>
              </Card>
            </Link>
          );
        })}
      </div>

      {!loading && filtered.length === 0 && (
        <div className="card-muted p-8 mt-6 text-center text-slate-500">Пусто</div>
      )}
    </Layout>
  );
}
