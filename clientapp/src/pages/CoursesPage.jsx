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
      <div className="page-hero-card mb-6 rounded-[28px] p-5 sm:p-6">
      <div className="flex flex-col gap-4 xl:flex-row xl:items-start xl:justify-between">
        <div className="max-w-3xl">
          <div className="text-xs font-semibold uppercase tracking-[0.24em] text-neutral-400">Каталог</div>
          <h1 className="mt-2 text-3xl font-semibold tracking-tight sm:text-4xl">Курсы</h1>
          <p className="mt-3 max-w-2xl text-sm leading-6 text-neutral-500">Сделали центральную часть шире: меньше пустых полей по краям, больше места для карточек и поиска, при этом левый и правый блоки остались на своих местах.</p>
        </div>

        {canEdit && isEditorMode && (
          <Button onClick={handleCreate} title="Создать курс">
            <Plus size={16} />
            <span className="ml-1">Создать курс</span>
          </Button>
        )}
      </div>
      </div>

      <Card className="page-search-card mb-6 rounded-[24px] p-3 sm:p-4">
        <Input
          placeholder="Поиск по названию/описанию…"
          value={q}
          onChange={(e) => setQ(e.target.value)}
        />
      </Card>

      {err && <div className="text-red-500 mb-4">{err}</div>}
      {loading && <div className="text-neutral-500">Загрузка…</div>}

      <div className="auto-fill-grid">
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
                  (c.isCompletedForCurrentUser 
                    ? "border-emerald-400/40 bg-emerald-500/5" 
                    : showOwnerBadge 
                      ? (c.canEdit ? "border-emerald-400/20" : "border-red-400/20")
                      : "border-[rgba(var(--accent)/0.25)]"
                  )
                }
              >
                <div className="flex h-full items-start justify-between gap-3">
                  <div className="min-w-0 flex-1">
                    <div className="text-lg font-semibold leading-7">{c.title}</div>
                    {c.description && (
                      <p className="text-sm text-neutral-500 mt-1 line-clamp-2">{c.description}</p>
                    )}

                    <div className="mt-3 flex flex-wrap items-center gap-2 text-sm text-neutral-500">
                      <Badge variant="info">Заданий: {c.assignmentCount ?? "—"}</Badge>
                      <Badge variant="info">Тестов: {c.testCount ?? "—"}</Badge>
                      {typeof c.solvedCountForCurrentUser === "number" && (
                        <Badge variant="primary">Код решено: {c.solvedCountForCurrentUser}</Badge>
                      )}
                      {typeof c.solvedTestsCountForCurrentUser === "number" && (
                        <Badge variant="primary">Тесты решено: {c.solvedTestsCountForCurrentUser}</Badge>
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
        <div className="card-muted p-8 mt-6 text-center text-neutral-500">Пусто</div>
      )}
    </Layout>
  );
}
