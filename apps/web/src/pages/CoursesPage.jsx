import React, { useEffect, useMemo, useRef, useState } from "react";
import Layout from "../components/Layout";
import { Card, Button, Input } from "../components/ui";
import { getCourses, createCourse, moveCoursePosition } from "../api/courses";
import { getCourseProgressByCourses } from "../api/assignments";
import { useNavigate } from "react-router-dom";
import { Plus } from "lucide-react";
import { useEditorMode } from "../contexts/EditorModeContext";
import { useNotify } from "../components/notify/NotifyProvider";
import { handleApiError } from "../utils/handleApiError";

const COURSE_PAGE_SIZE = 50;

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

function normalizeProgressRows(rows) {
  const map = new Map();
  for (const row of Array.isArray(rows) ? rows : []) {
    const id = String(row?.courseId || row?.CourseId || "");
    if (!id) continue;
    const total = Number(row?.total ?? row?.Total ?? 0) || 0;
    const solved = Number(row?.solved ?? row?.Solved ?? 0) || 0;
    const percent = total > 0 ? Math.round((solved / total) * 100) : 0;
    map.set(id, { total, solved, percent, isComplete: total > 0 && solved === total, loading: false });
  }
  return map;
}

function buildChildrenByParent(courses) {
  const map = new Map();
  for (const course of Array.isArray(courses) ? courses : []) {
    const parentKey = String(course?.parentCourseId || "");
    if (!map.has(parentKey)) map.set(parentKey, []);
    map.get(parentKey).push(course);
  }
  return map;
}

function collectCourseSubtreeIds(courseId, childrenByParent, seen = new Set()) {
  const id = String(courseId || "");
  if (!id || seen.has(id)) return [];
  seen.add(id);
  const ids = [id];
  for (const child of childrenByParent.get(id) || []) {
    ids.push(...collectCourseSubtreeIds(child.id, childrenByParent, seen));
  }
  return ids;
}

function sumCourseProgress(courseIds, directProgressByCourseId) {
  let total = 0;
  let solved = 0;
  for (const id of courseIds) {
    const row = directProgressByCourseId.get(String(id));
    total += Number(row?.total || 0);
    solved += Number(row?.solved || 0);
  }
  const percent = total > 0 ? Math.round((solved / total) * 100) : 0;
  return { total, solved, percent, isComplete: total > 0 && solved === total, loading: false };
}

function courseSortValue(course) {
  return Number.isFinite(Number(course?.sort)) ? Number(course.sort) : 0;
}

function compareCourses(a, b) {
  const bySort = courseSortValue(a) - courseSortValue(b);
  if (bySort !== 0) return bySort;
  return String(a?.title || "").localeCompare(String(b?.title || ""), "ru", { sensitivity: "base" });
}

function CourseCard({
  course,
  revealIndex = 0,
  editorTools,
  progress,
  isDragged,
  isDropTarget,
  dropMode,
  onNavigate,
  onDragStart,
  onDragEnter,
  onDragOver,
  onDragLeave,
  onDrop,
  onDragEnd,
}) {
  const progressText = progress?.loading ? "—/—" : `${progress?.solved ?? 0}/${progress?.total ?? 0}`;
  const unavailable = course.canAccess === false || course.isAccessible === false || course.isAvailable === false;
  const foreignInEditor = editorTools && course.canEdit === false;
  const canDrag = editorTools && course.canEdit !== false;
  const cardClass =
    "transition hover:shadow-lg cursor-pointer p-5 min-h-[190px] " +
    (progress?.isComplete || course.isCompletedForCurrentUser
      ? "border-emerald-400/40 bg-emerald-500/5 "
      : foreignInEditor || unavailable
        ? "border-neutral-300/60 bg-neutral-500/5 opacity-70 grayscale-[0.25] "
        : "border-[rgba(var(--accent)/0.25)] ") +
    (isDragged ? "opacity-60 scale-[0.99] " : "") +
    (isDropTarget && dropMode === "inside" ? "ring-2 ring-[rgb(var(--accent))] " : "") +
    (isDropTarget && dropMode === "before" ? "border-t-4 border-t-[rgb(var(--accent))] " : "") +
    (isDropTarget && dropMode === "after" ? "border-b-4 border-b-[rgb(var(--accent))] " : "") +
    (canDrag ? "cursor-move " : "cursor-pointer ");

  return (
    <div className="tf-reveal-item" style={{ "--tf-reveal-delay": `${(revealIndex % COURSE_PAGE_SIZE) * 35}ms` }}>
      <Card
        className={cardClass}
        onClick={onNavigate}
        role="button"
        tabIndex={0}
        draggable={canDrag}
        onDragStart={onDragStart}
        onDragEnter={onDragEnter}
        onDragOver={onDragOver}
        onDragLeave={onDragLeave}
        onDrop={onDrop}
        onDragEnd={onDragEnd}
        title={canDrag ? "Перетащи курс: между карточками — сортировка, на середину карточки — вложить внутрь" : "Открыть курс"}
      >
        <div className="flex h-full flex-col justify-between gap-4">
          <div className="min-w-0">
            <div className="flex min-w-0 items-start justify-between gap-2">
              <div className="min-w-0 text-lg font-semibold leading-7 truncate">{course.title}</div>
              <div className="shrink-0 rounded-full border border-[rgba(var(--border)/0.75)] px-3 py-1 text-xs text-neutral-500">курс</div>
            </div>

            {course.description ? (
              <p className="text-sm text-neutral-500 mt-2 line-clamp-3">{course.description}</p>
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
  const [draggedCourseId, setDraggedCourseId] = useState(null);
  const [dragOverCourseId, setDragOverCourseId] = useState(null);
  const [dragOverMode, setDragOverMode] = useState("before");
  const dragStartedRef = useRef(false);

  const nav = useNavigate();
  const notify = useNotify();
  const { canEdit, isEditorMode } = useEditorMode();

  const editorTools = canEdit && isEditorMode;
  const rootCourses = useMemo(() => {
    const query = q.trim().toLowerCase();
    return (items || [])
      .filter((course) => !course?.parentCourseId)
      .filter((course) => {
        if (!query) return true;
        return String(course?.title || "").toLowerCase().includes(query)
          || String(course?.description || "").toLowerCase().includes(query);
      })
      .sort(compareCourses);
  }, [items, q]);

  const loadCourses = async ({ reset = false } = {}) => {
    const nextPage = reset ? 1 : page + 1;
    try {
      if (reset) setLoading(true);
      else setLoadingMore(true);
      setLoadError("");
      const payload = await getCourses({ tree: true, page: nextPage, pageSize: COURSE_PAGE_SIZE });
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
    loadCourses({ reset: true });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

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
      for (const course of rootCourses) {
        if (!next[course.id]) next[course.id] = { loading: true, total: 0, solved: 0, percent: 0, isComplete: false };
      }
      return next;
    });

    getCourseProgressByCourses(visibleCourses.map((course) => course.id))
      .then((rows) => {
        if (cancelled) return;
        const directProgress = normalizeProgressRows(rows);
        const childrenByParent = buildChildrenByParent(visibleCourses);
        const next = {};
        for (const course of rootCourses) {
          const subtreeIds = collectCourseSubtreeIds(course.id, childrenByParent);
          next[course.id] = sumCourseProgress(subtreeIds, directProgress);
        }
        setProgressByCourseId(next);
      })
      .catch(() => {
        if (cancelled) return;
        const failed = {};
        for (const course of rootCourses) {
          failed[course.id] = { total: 0, solved: 0, percent: 0, isComplete: false, loading: false, failed: true };
        }
        setProgressByCourseId(failed);
      });

    return () => {
      cancelled = true;
    };
  }, [items, rootCourses]);

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

  const getDropMode = (event, targetCourse) => {
    if (!draggedCourseId || String(draggedCourseId) === String(targetCourse.id)) return "before";
    const rect = event.currentTarget.getBoundingClientRect();
    const y = event.clientY - rect.top;
    if (y < rect.height * 0.25) return "before";
    if (y > rect.height * 0.75) return "after";
    return "inside";
  };

  const resetDrag = () => {
    setDraggedCourseId(null);
    setDragOverCourseId(null);
    setDragOverMode("before");
    setTimeout(() => {
      dragStartedRef.current = false;
    }, 0);
  };

  const moveRootCourse = async (sourceCourse, targetCourse, mode) => {
    const nextRoot = rootCourses.filter((x) => String(x.id) !== String(sourceCourse.id));
    const targetIndex = nextRoot.findIndex((x) => String(x.id) === String(targetCourse.id));
    if (targetIndex < 0) return;
    const insertIndex = mode === "after" ? targetIndex + 1 : targetIndex;
    nextRoot.splice(insertIndex, 0, sourceCourse);
    const sortById = new Map(nextRoot.map((x, index) => [String(x.id), index]));

    setItems((prev) => prev.map((x) => {
      const nextSort = sortById.get(String(x.id));
      return Number.isFinite(nextSort) ? { ...x, parentCourseId: null, sort: nextSort } : x;
    }));

    await moveCoursePosition(sourceCourse.id, null, insertIndex + 1);
  };

  const moveCourseInside = async (sourceCourse, targetCourse) => {
    const childCount = (items || []).filter((x) => String(x?.parentCourseId || "") === String(targetCourse.id)).length;
    setItems((prev) => prev.map((x) => (
      String(x.id) === String(sourceCourse.id)
        ? { ...x, parentCourseId: targetCourse.id, sort: childCount }
        : x
    )));
    await moveCoursePosition(sourceCourse.id, targetCourse.id, childCount + 1);
  };

  const handleDropCourse = async (targetCourse, modeFromEvent) => {
    const sourceId = draggedCourseId;
    const mode = modeFromEvent || dragOverMode;
    resetDrag();
    if (!editorTools || !sourceId || String(sourceId) === String(targetCourse.id)) return;

    const sourceCourse = items.find((x) => String(x.id) === String(sourceId));
    if (!sourceCourse || sourceCourse.canEdit === false) {
      notify.warn("Вы не владелец курса — менять порядок нельзя");
      return;
    }
    if (mode === "inside" && targetCourse.canEdit === false) {
      notify.warn("Нельзя вложить курс в чужой курс");
      return;
    }

    try {
      if (mode === "inside") {
        await moveCourseInside(sourceCourse, targetCourse);
        notify.success("Курс вложен");
      } else {
        await moveRootCourse(sourceCourse, targetCourse, mode);
        notify.success("Порядок курсов обновлён");
      }
    } catch (e) {
      handleApiError(e, notify, "Не удалось переместить курс");
      await loadCourses({ reset: true });
    }
  };

  const navigateCourse = (course) => {
    if (dragStartedRef.current) {
      dragStartedRef.current = false;
      return;
    }
    nav(`/course/${course.id}`);
  };

  return (
    <Layout>
      <div className="page-hero-card mb-6 rounded-[28px] p-5 sm:p-6">
        <div className="flex flex-col gap-4 xl:flex-row xl:items-start xl:justify-between">
          <div className="max-w-3xl">
            <div className="text-xs font-semibold uppercase tracking-[0.24em] text-neutral-400">Каталог</div>
            <h1 className="mt-2 text-3xl font-semibold tracking-tight sm:text-4xl">Курсы</h1>
            <p className="mt-3 max-w-2xl text-sm leading-6 text-neutral-500">
              В каталоге показываются только курсы верхнего уровня. В режиме редактора порядок и вложенность меняются перетаскиванием карточек.
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
            <CourseCard
              key={course.id}
              course={course}
              revealIndex={index}
              editorTools={editorTools}
              progress={progressByCourseId[course.id]}
              isDragged={String(draggedCourseId || "") === String(course.id)}
              isDropTarget={String(dragOverCourseId || "") === String(course.id)}
              dropMode={dragOverMode}
              onNavigate={() => navigateCourse(course)}
              onDragStart={(e) => {
                if (!editorTools || course.canEdit === false) {
                  e.preventDefault();
                  return;
                }
                dragStartedRef.current = true;
                e.dataTransfer.effectAllowed = "move";
                e.dataTransfer.setData("text/plain", String(course.id));
                setDraggedCourseId(course.id);
              }}
              onDragEnter={(e) => {
                if (!draggedCourseId || String(draggedCourseId) === String(course.id)) return;
                e.preventDefault();
                setDragOverCourseId(course.id);
                setDragOverMode(getDropMode(e, course));
              }}
              onDragOver={(e) => {
                if (!draggedCourseId || String(draggedCourseId) === String(course.id)) return;
                e.preventDefault();
                e.dataTransfer.dropEffect = "move";
                setDragOverCourseId(course.id);
                setDragOverMode(getDropMode(e, course));
              }}
              onDragLeave={() => {
                if (String(dragOverCourseId || "") === String(course.id)) {
                  setDragOverCourseId(null);
                  setDragOverMode("before");
                }
              }}
              onDrop={(e) => {
                e.preventDefault();
                const mode = getDropMode(e, course);
                handleDropCourse(course, mode);
              }}
              onDragEnd={resetDrag}
            />
          ))}
        </div>
      )}

      {!loading && rootCourses.length === 0 && (
        <div className="card-muted p-8 mt-6 text-center text-neutral-500">Курсы пока не найдены.</div>
      )}

      {!loading && items.length > 0 && (
        <div className="mt-6 flex flex-col items-center gap-2">
          <div className="text-xs text-neutral-500">Показано {rootCourses.length}{total ? ` из ${total}` : ''}</div>
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
