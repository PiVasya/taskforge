import React, { useEffect, useMemo, useState } from "react";
import Layout from "../components/Layout";
import { Card, Button, Input } from "../components/ui";
import { getCourses, createCourse, moveCoursePosition } from "../api/courses";
import { getAssignmentsByCourse } from "../api/assignments";
import { useNavigate } from "react-router-dom";
import { ArrowDown, ArrowUp, ChevronDown, ChevronRight, Plus } from "lucide-react";
import { useEditorMode } from "../contexts/EditorModeContext";
import { useNotify } from "../components/notify/NotifyProvider";
import { handleApiError } from "../utils/handleApiError";

const COURSE_PAGE_SIZE = 50;
const ROOT_PARENT_KEY = "__root__";

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

function courseSortValue(course) {
  return Number.isFinite(Number(course?.sort)) ? Number(course.sort) : 0;
}

function compareCourses(a, b) {
  const bySort = courseSortValue(a) - courseSortValue(b);
  if (bySort !== 0) return bySort;
  return String(a?.title || "").localeCompare(String(b?.title || ""), "ru", { sensitivity: "base" });
}

function parentKey(parentCourseId) {
  return parentCourseId ? String(parentCourseId) : ROOT_PARENT_KEY;
}

function CourseNode({
  course,
  depth = 0,
  revealIndex = 0,
  editorTools,
  progressByCourseId,
  openCourseIds,
  forceOpen,
  onToggleOpen,
  onNavigateCourse,
  onSwapCourse,
}) {
  const progress = progressByCourseId[course.id];
  const progressText = progress?.loading ? "—/—" : `${progress?.solved ?? 0}/${progress?.total ?? 0}`;
  const unavailable = course.canAccess === false || course.isAccessible === false || course.isAvailable === false;
  const foreignInEditor = editorTools && course.canEdit === false;
  const hasChildren = Array.isArray(course.children) && course.children.length > 0;
  const isOpen = forceOpen || openCourseIds.has(String(course.id));

  const href = editorTools && course.canEdit ? `/courses/${course.id}/edit` : `/course/${course.id}`;
  const cardClass =
    "transition hover:shadow-lg cursor-pointer " +
    (depth > 0 ? "p-4 min-h-[150px] " : "p-5 min-h-[190px] ") +
    (progress?.isComplete || course.isCompletedForCurrentUser
      ? "border-emerald-400/40 bg-emerald-500/5 "
      : foreignInEditor || unavailable
        ? "border-neutral-300/60 bg-neutral-500/5 opacity-70 grayscale-[0.25] "
        : "border-[rgba(var(--accent)/0.25)] ");

  return (
    <div
      className={(depth === 0 ? "tf-reveal-item" : "mt-3 border-l border-[rgba(var(--border)/0.8)] pl-3")}
      style={depth === 0 ? { "--tf-reveal-delay": `${(revealIndex % COURSE_PAGE_SIZE) * 35}ms` } : undefined}
    >
      <Card className={cardClass} onClick={() => onNavigateCourse(href)} role="button" tabIndex={0}>
        <div className="flex h-full flex-col justify-between gap-4">
          <div className="min-w-0">
            <div className="flex min-w-0 items-start justify-between gap-2">
              <div className="min-w-0">
                <div className={(depth > 0 ? "text-base" : "text-lg") + " font-semibold leading-7 truncate"}>{course.title}</div>
                {editorTools ? (
                  <div className="mt-1 text-[11px] text-neutral-400">sort: {courseSortValue(course)}</div>
                ) : null}
              </div>

              <div className="flex shrink-0 items-center gap-1" onClick={(e) => e.stopPropagation()}>
                {editorTools && course.canEdit ? (
                  <>
                    <button
                      type="button"
                      className="rounded-lg border border-[rgba(var(--border)/0.8)] px-2 py-1 text-xs hover:bg-[rgba(var(--muted)/0.35)]"
                      title="Поднять курс выше"
                      onClick={() => onSwapCourse(course, -1)}
                    >
                      <ArrowUp size={14} />
                    </button>
                    <button
                      type="button"
                      className="rounded-lg border border-[rgba(var(--border)/0.8)] px-2 py-1 text-xs hover:bg-[rgba(var(--muted)/0.35)]"
                      title="Опустить курс ниже"
                      onClick={() => onSwapCourse(course, 1)}
                    >
                      <ArrowDown size={14} />
                    </button>
                  </>
                ) : null}

                {hasChildren ? (
                  <button
                    type="button"
                    className="rounded-lg border border-[rgba(var(--border)/0.8)] px-2 py-1 text-xs hover:bg-[rgba(var(--muted)/0.35)]"
                    title={isOpen ? "Скрыть вложенные курсы" : "Показать вложенные курсы"}
                    onClick={() => onToggleOpen(course.id)}
                  >
                    {isOpen ? <ChevronDown size={14} /> : <ChevronRight size={14} />}
                  </button>
                ) : null}
              </div>
            </div>

            {course.description ? (
              <p className="text-sm text-neutral-500 mt-2 line-clamp-3">{course.description}</p>
            ) : (
              <p className="text-sm text-neutral-400 mt-2">Описание пока не добавлено.</p>
            )}

            {hasChildren ? (
              <div className="mt-3 text-xs font-medium text-neutral-500">
                Вложенные курсы: {course.children.length}
              </div>
            ) : null}
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

      {hasChildren && isOpen ? (
        <div className="mt-3">
          {course.children.map((child, index) => (
            <CourseNode
              key={child.id}
              course={child}
              depth={depth + 1}
              revealIndex={index}
              editorTools={editorTools}
              progressByCourseId={progressByCourseId}
              openCourseIds={openCourseIds}
              forceOpen={forceOpen}
              onToggleOpen={onToggleOpen}
              onNavigateCourse={onNavigateCourse}
              onSwapCourse={onSwapCourse}
            />
          ))}
        </div>
      ) : null}
    </div>
  );
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
  const [openCourseIds, setOpenCourseIds] = useState(() => new Set());

  const nav = useNavigate();
  const notify = useNotify();
  const { canEdit, isEditorMode } = useEditorMode();

  const editorTools = canEdit && isEditorMode;
  const rootCourses = useMemo(() => (items || []).filter((course) => !course?.parentCourseId).sort(compareCourses), [items]);
  const forceOpenTree = false;

  const siblingGroups = useMemo(() => {
    const groups = new Map();
    for (const course of items || []) {
      const key = parentKey(course?.parentCourseId || null);
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key).push(course);
    }
    for (const group of groups.values()) group.sort(compareCourses);
    return groups;
  }, [items]);

  const loadCourses = async ({ reset = false, query = q } = {}) => {
    const nextPage = reset ? 1 : page + 1;
    try {
      if (reset) setLoading(true);
      else setLoadingMore(true);
      setLoadError("");
      const payload = await getCourses({ tree: true, page: nextPage, pageSize: COURSE_PAGE_SIZE, q: query.trim() || undefined });
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
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [q]);

  useEffect(() => {
    let cancelled = false;
    const visibleCourses = (rootCourses || []).filter((course) => course?.id);
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
  }, [rootCourses]);

  const handleCreate = async () => {
    try {
      const { id } = await createCourse({
        title: "Новый курс",
        description: "Описание курса",
        isPublic: false,
        visibleGroupIds: [],
        ownerIds: [],
        parentCourseId: null,
        sort: rootCourses.length,
      });
      notify.success("Курс создан");
      nav(`/courses/${id}/edit`);
    } catch (e) {
      handleApiError(e, notify, "Не удалось создать курс");
    }
  };

  const toggleOpen = (courseId) => {
    setOpenCourseIds((prev) => {
      const next = new Set(prev);
      const id = String(courseId);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  };

  const swapCourse = async (course, dir) => {
    if (!editorTools) return;
    const key = parentKey(course?.parentCourseId || null);
    const siblings = siblingGroups.get(key) || [];
    const currentIndex = siblings.findIndex((x) => String(x.id) === String(course.id));
    const targetIndex = currentIndex + dir;
    if (currentIndex < 0 || targetIndex < 0 || targetIndex >= siblings.length) return;

    const a = siblings[currentIndex];
    const b = siblings[targetIndex];
    if (a.canEdit === false || b.canEdit === false) {
      notify.warn("Вы не владелец курса — менять порядок нельзя");
      return;
    }

    const nextSiblings = [...siblings];
    const [moved] = nextSiblings.splice(currentIndex, 1);
    nextSiblings.splice(targetIndex, 0, moved);
    const nextSortById = new Map(nextSiblings.map((x, index) => [String(x.id), index]));

    setItems((prev) => prev.map((x) => {
      const nextSort = nextSortById.get(String(x.id));
      return Number.isFinite(nextSort) ? { ...x, sort: nextSort } : x;
    }));

    try {
      await moveCoursePosition(a.id, a.parentCourseId || null, targetIndex + 1);
    } catch (e) {
      handleApiError(e, notify, "Не удалось изменить порядок курсов");
      await loadCourses({ reset: true, query: q });
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
              Выберите курс и переходите к заданиям. В каталоге показываются только курсы верхнего уровня; вложенные курсы открываются уже внутри родителя.
            </p>
          </div>

          {editorTools && (
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

      {!loading && (
        <div className="auto-fill-grid">
          {rootCourses.map((course, index) => (
            <CourseNode
              key={course.id}
              course={course}
              revealIndex={index}
              editorTools={editorTools}
              progressByCourseId={progressByCourseId}
              openCourseIds={openCourseIds}
              forceOpen={forceOpenTree}
              onToggleOpen={toggleOpen}
              onNavigateCourse={nav}
              onSwapCourse={swapCourse}
            />
          ))}
        </div>
      )}

      {!loading && rootCourses.length === 0 && (
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
