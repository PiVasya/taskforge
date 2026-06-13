import React, { useEffect, useState } from "react";
import Layout from "../components/Layout";
import { Card, Button, Input, Badge } from "../components/ui";
import { getCourses, createCourse } from "../api/courses";
import { Link, useNavigate } from "react-router-dom";
import { Plus } from "lucide-react";
import { useEditorMode } from "../contexts/EditorModeContext";
import { useNotify } from "../components/notify/NotifyProvider";
import { handleApiError } from "../utils/handleApiError";
import { CourseSkeletonGrid } from "../components/LoadingStates";
import { useProgressiveList } from "../hooks/useProgressiveList";
const COURSE_PAGE_SIZE = 12;


function normalizePagedCourses(payload) {
  if (Array.isArray(payload)) return { items: payload, page: 1, hasMore: false, total: payload.length };
  const items = Array.isArray(payload?.items) ? payload.items : [];
  return {
    items,
    page: Number(payload?.page || 1),
    hasMore: Boolean(payload?.hasMore),
    total: Number(payload?.total || items.length),
  };
}

export default function CoursesPage() {
  const [items, setItems] = useState([]);
  const [q, setQ] = useState("");
  const [loading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [loadError, setLoadError] = useState("");
  const [page, setPage] = useState(1);
  const [hasMore, setHasMore] = useState(false);
  const [total, setTotal] = useState(0);
  const [listVersion, setListVersion] = useState(0);

  const nav = useNavigate();
  const notify = useNotify();
  const { canEdit, isEditorMode } = useEditorMode();
  const { visibleItems, isRevealing } = useProgressiveList(items, {
    initialCount: 6,
    step: 3,
    intervalMs: 70,
    resetKey: listVersion,
  });

  const loadCourses = async ({ reset = false, query = q } = {}) => {
    const nextPage = reset ? 1 : page + 1;
    try {
      if (reset) setLoading(true);
      else setLoadingMore(true);
      setLoadError("");
      const payload = await getCourses({ page: nextPage, pageSize: COURSE_PAGE_SIZE, q: query.trim() || undefined });
      const parsed = normalizePagedCourses(payload);
      setItems((prev) => reset ? parsed.items : [...prev, ...parsed.items]);
      if (reset) setListVersion((v) => v + 1);
      setPage(parsed.page);
      setHasMore(parsed.hasMore);
      setTotal(parsed.total);
    } catch (e) {
      const parsed = handleApiError(e, notify, "Не удалось загрузить курсы");
      setLoadError(parsed?.userMessage || "Не удалось загрузить курсы");
    } finally {
      setLoading(false);
      setLoadingMore(false);
    }
  };

  useEffect(() => {
    const timer = setTimeout(() => {
      loadCourses({ reset: true, query: q });
    }, 250);
    return () => clearTimeout(timer);
  }, [q]);

  const handleCreate = async () => {
    try {
      const { id } = await createCourse({
        title: "Новый курс",
        description: "Описание курса",
        isPublic: false,
        visibleGroupIds: [],
        ownerIds: [],
      });
      notify.success("Курс создан");
      nav(`/courses/${id}/edit`);
    } catch (e) {
      handleApiError(e, notify, "Не удалось создать курс");
    }
  };

  return (
    <Layout>
      <div className="page-hero-card mb-6 rounded-[28px] p-5 sm:p-6">
        <div className="flex flex-col gap-4 xl:flex-row xl:items-start xl:justify-between">
          <div className="max-w-3xl">
            <div className="text-xs font-semibold uppercase tracking-[0.24em] text-neutral-400">Каталог</div>
            <h1 className="mt-2 text-3xl font-semibold tracking-tight sm:text-4xl">Курсы</h1>
            <p className="mt-3 max-w-2xl text-sm leading-6 text-neutral-500">
              Выберите курс и переходите к заданиям. В обычном режиме карточки показывают только нужное ученику.
            </p>
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

      {loadError && (
        <div className="rounded-2xl border border-rose-300 bg-rose-500/10 p-4 text-sm text-rose-700 dark:text-rose-300 mb-4">
          {loadError}
        </div>
      )}
      {loading && <CourseSkeletonGrid count={6} />}

      {!loading && (
      <div className="auto-fill-grid">
        {visibleItems.map((c, index) => {
          const editorTools = canEdit && isEditorMode;
          const href = editorTools && c.canEdit ? `/courses/${c.id}/edit` : `/course/${c.id}`;
          const unavailable = c.canAccess === false || c.isAccessible === false || c.isAvailable === false;
          const foreignInEditor = editorTools && c.canEdit === false;

          return (
            <Link
              key={c.id}
              to={href}
              className="tf-reveal-item block group focus:outline-none focus:ring-2 focus:ring-[rgb(var(--accent))] rounded-2xl"
              style={{ animationDelay: `${Math.min(index, 8) * 28}ms` }}
            >
              <Card
                className={
                  "p-5 transition hover:shadow-lg cursor-pointer min-h-[150px] " +
                  (c.isCompletedForCurrentUser
                    ? "border-emerald-400/40 bg-emerald-500/5 "
                    : foreignInEditor || unavailable
                      ? "border-neutral-300/60 bg-neutral-500/5 opacity-70 grayscale-[0.25] "
                      : "border-[rgba(var(--accent)/0.25)] ")
                }
              >
                <div className="flex h-full flex-col justify-between gap-4">
                  <div className="min-w-0">
                    <div className="flex items-start justify-between gap-3">
                      <div className="text-lg font-semibold leading-7 truncate">{c.title}</div>
                      {c.isCompletedForCurrentUser ? <Badge intent="success">Пройден</Badge> : null}
                    </div>
                    {c.description ? (
                      <p className="text-sm text-neutral-500 mt-2 line-clamp-3">{c.description}</p>
                    ) : (
                      <p className="text-sm text-neutral-400 mt-2">Описание пока не добавлено.</p>
                    )}
                  </div>
                </div>
              </Card>
            </Link>
          );
        })}
      </div>
      )}

      {!loading && items.length === 0 && (
        <div className="card-muted p-8 mt-6 text-center text-neutral-500">Курсы пока не найдены.</div>
      )}

      {!loading && items.length > 0 && (
        <div className="mt-6 flex flex-col items-center gap-2">
          <div className="text-xs text-neutral-500">Показано {visibleItems.length}{total ? ` из ${total}` : ''}</div>
          {isRevealing ? <div className="text-xs text-neutral-400">Раскладываем карточки…</div> : null}
          {hasMore && !isRevealing && (
            <Button variant="outline" onClick={() => loadCourses({ reset: false })} disabled={loadingMore}>
              {loadingMore ? 'Загружаем ещё…' : 'Показать ещё'}
            </Button>
          )}
        </div>
      )}
    </Layout>
  );
}
