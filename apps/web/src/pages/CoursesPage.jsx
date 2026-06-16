import React, { useEffect, useState } from "react";
import Layout from "../components/Layout";
import { Card, Button, Input } from "../components/ui";
import { getCourses, createCourse } from "../api/courses";
import { getAssignmentsByCourse } from "../api/assignments";
import { Link, useNavigate } from "react-router-dom";
import { Plus } from "lucide-react";
import { useEditorMode } from "../contexts/EditorModeContext";
import { useNotify } from "../components/notify/NotifyProvider";
import { handleApiError } from "../utils/handleApiError";

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

function isAssignmentSolved(item) {
  return Boolean(item?.solvedByCurrentUser || item?.isSolved || item?.progressStatus === "solved");
}

function buildCourseProgress(assignments) {
  const list = Array.isArray(assignments) ? assignments : [];
  const total = list.length;
  const solved = list.filter(isAssignmentSolved).length;
  const percent = total > 0 ? Math.round((solved / total) * 100) : 0;
  return { total, solved, percent, isComplete: total > 0 && solved === total };
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
  const [progressByCourseId, setProgressByCourseId] = useState({});

  const nav = useNavigate();
  const notify = useNotify();
  const { canEdit, isEditorMode } = useEditorMode();

  const loadCourses = async ({ reset = false, query = q } = {}) => {
    const nextPage = reset ? 1 : page + 1;
    try {
      if (reset) setLoading(true);
      else setLoadingMore(true);
      setLoadError("");
      const payload = await getCourses({ page: nextPage, pageSize: COURSE_PAGE_SIZE, q: query.trim() || undefined });
      const parsed = normalizePagedCourses(payload);
      setItems((prev) => reset ? parsed.items : [...prev, ...parsed.items]);
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

  useEffect(() => {
    let cancelled = false;
    const visibleCourses = (items || []).filter((course) => course?.id);
    if (visibleCourses.length === 0) {
      setProgressByCourseId({});
      return () => {
        cancelled = true;
      };
    }

    setProgressByCourseId((prev) => {
      const next = { ...prev };
      for (const course of visibleCourses) {
        if (!next[course.id]) next[course.id] = { loading: true, total: 0, solved: 0, percent: 0, isComplete: false };
      }
      return next;
    });

    Promise.all(
      visibleCourses.map(async (course) => {
        try {
          const assignments = await getAssignmentsByCourse(course.id);
          return [course.id, { ...buildCourseProgress(assignments), loading: false }];
        } catch {
          return [course.id, { total: 0, solved: 0, percent: 0, isComplete: false, loading: false, failed: true }];
        }
      })
    ).then((entries) => {
      if (cancelled) return;
      setProgressByCourseId((prev) => {
        const next = { ...prev };
        for (const [id, progress] of entries) next[id] = progress;
        return next;
      });
    });

    return () => {
      cancelled = true;
    };
  }, [items]);

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
      {loading && (
        <div className="auto-fill-grid">
          {Array.from({ length: 6 }).map((_, index) => (
            <div key={index} className="tf-skeleton-card tf-reveal-item" style={{ "--tf-reveal-delay": `${index * 45}ms` }}>
              <div className="tf-skeleton tf-skeleton-line mb-4 w-2/3" />
              <div className="tf-skeleton tf-skeleton-line mb-2 w-full" />
              <div className="tf-skeleton tf-skeleton-line mb-2 w-5/6" />
              <div className="tf-skeleton tf-skeleton-line mt-6 w-1/3" />
            </div>
          ))}
        </div>
      )}

      <div className="auto-fill-grid">
        {items.map((c, index) => {
          const editorTools = canEdit && isEditorMode;
          const href = editorTools && c.canEdit ? `/courses/${c.id}/edit` : `/course/${c.id}`;
          const unavailable = c.canAccess === false || c.isAccessible === false || c.isAvailable === false;
          const foreignInEditor = editorTools && c.canEdit === false;
          const progress = progressByCourseId[c.id];
          const progressText = progress?.loading
            ? "—/—"
            : `${progress?.solved ?? 0}/${progress?.total ?? 0}`;

          return (
            <Link
              key={c.id}
              to={href}
              className="block group focus:outline-none focus:ring-2 focus:ring-[rgb(var(--accent))] rounded-2xl tf-reveal-item"
              style={{ "--tf-reveal-delay": `${(index % COURSE_PAGE_SIZE) * 35}ms` }}
            >
              <Card
                className={
                  "p-5 transition hover:shadow-lg cursor-pointer min-h-[190px] " +
                  (progress?.isComplete || c.isCompletedForCurrentUser
                    ? "border-emerald-400/40 bg-emerald-500/5 "
                    : foreignInEditor || unavailable
                      ? "border-neutral-300/60 bg-neutral-500/5 opacity-70 grayscale-[0.25] "
                      : "border-[rgba(var(--accent)/0.25)] ")
                }
              >
                <div className="flex h-full flex-col justify-between gap-4">
                  <div className="min-w-0">
                    <div className="text-lg font-semibold leading-7 truncate">{c.title}</div>
                    {c.description ? (
                      <p className="text-sm text-neutral-500 mt-2 line-clamp-3">{c.description}</p>
                    ) : (
                      <p className="text-sm text-neutral-400 mt-2">Описание пока не добавлено.</p>
                    )}
                  </div>

                  <div className="mt-auto space-y-2">
                    <div className="text-xs font-medium text-neutral-500">{progressText}</div>
                    <div className="h-2 overflow-hidden rounded-full bg-neutral-200/70 dark:bg-white/10">
                      <div
                        className="h-full rounded-full bg-[rgb(var(--accent))] transition-all duration-500"
                        style={{ width: `${progress?.total > 0 ? progress.percent : 0}%` }}
                      />
                    </div>
                  </div>
                </div>
              </Card>
            </Link>
          );
        })}
      </div>

      {!loading && items.length === 0 && (
        <div className="card-muted p-8 mt-6 text-center text-neutral-500">Курсы пока не найдены.</div>
      )}

      {!loading && items.length > 0 && (
        <div className="mt-6 flex flex-col items-center gap-2">
          <div className="text-xs text-neutral-500">Показано {items.length}{total ? ` из ${total}` : ''}</div>
          {hasMore && (
            <Button variant="outline" onClick={() => loadCourses({ reset: false })} disabled={loadingMore}>
              {loadingMore ? 'Загружаем ещё…' : 'Показать ещё'}
            </Button>
          )}
        </div>
      )}
    </Layout>
  );
}
