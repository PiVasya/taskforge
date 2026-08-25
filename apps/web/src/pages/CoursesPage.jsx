import React, { useEffect, useMemo, useRef, useState } from "react";
import { Badge, Card, Button, Input } from "../components/ui";
import { getCourses, createCourse, moveCoursePosition } from "../api/courses";
import { getCourseProgressByCourses } from "../api/assignments";
import { useNavigate } from "react-router-dom";
import { Copy, ExternalLink, EyeOff, FolderPlus, Pencil, Plus } from "lucide-react";
import { useEditorMode } from "../contexts/EditorModeContext";
import { useNotify } from "../components/notify/NotifyProvider";
import { handleApiError } from "../utils/handleApiError";
import { resolveCardDropIntent, resolveGridGapDropIntent, isPointerInsideDndItem } from "../utils/gridDragDrop";
import { ContextMenu, ContextMenuItem, ContextMenuLabel, ContextMenuSeparator, claimContextMenuEvent } from '../components/ui/ContextMenu';
import { useQueryClient } from '../data/QueryClientProvider';
import { useAuth } from '../auth/AuthContext';
import {
  COURSE_PROGRESSION_CHANGED_EVENT,
  COURSE_PROGRESSION_STORAGE_KEY,
  getCourseProgressionRevision,
} from '../features/course-assignments/courseProgressionFreshness';

const COURSE_PAGE_SIZE = 50;
const COURSES_PAGE_STATE_KEY = ['page-state', 'courses'];
const COURSES_CACHE_STALE_MS = 60_000;

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
  dropEdge,
  onNavigate,
  onContextMenu,
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
  const hiddenFromStudents = editorTools && Boolean(course.isHiddenFromStudents);
  const groupRestricted = editorTools && !hiddenFromStudents && course.isPublic === false;
  const canDrag = editorTools && course.canEdit !== false;
  const cardClass =
    "transition hover:shadow-lg cursor-pointer p-5 min-h-[190px] " +
    (progress?.isComplete || course.isCompletedForCurrentUser
      ? "border-emerald-400/40 bg-emerald-500/5 "
      : foreignInEditor || unavailable
        ? "border-neutral-300/60 bg-neutral-500/5 opacity-70 grayscale-[0.25] "
        : "border-[rgba(var(--accent)/0.25)] ") +
    (hiddenFromStudents ? "ring-2 ring-[rgba(var(--accent)/0.55)] ring-offset-2 ring-offset-[rgb(var(--bg))] border-dashed " : "") +
    (isDragged ? "opacity-60 scale-[0.99] " : "") +
    (isDropTarget && dropMode !== "inside" ? "dnd-insert-target " : "") +
    (isDropTarget && dropMode === "inside" ? "dnd-nest-target " : "") +
    (isDropTarget && dropMode !== "inside" && dropEdge ? `dnd-insert-${dropEdge} ` : "") +
    (canDrag ? "cursor-move " : "cursor-pointer ");

  return (
    <div className="tf-reveal-item" style={{ "--tf-reveal-delay": `${(revealIndex % COURSE_PAGE_SIZE) * 35}ms` }}>
      <Card
        className={cardClass}
        onClick={onNavigate}
        onContextMenuCapture={onContextMenu}
        role="button"
        tabIndex={0}
        draggable={canDrag}
        data-dnd-course-id={course.id}
        onDragStart={onDragStart}
        onDragEnter={onDragEnter}
        onDragOver={onDragOver}
        onDragLeave={onDragLeave}
        onDrop={onDrop}
        onDragEnd={onDragEnd}
        title={canDrag ? "Край карточки — изменить порядок. Центр другой карточки — вложить курс внутрь." : "Открыть курс"}
        data-taskforge-automation-id={`course-${course.id}`}
        data-taskforge-agent-role="course-card"
        data-taskforge-agent-action="open-course"
        data-taskforge-agent-state={(progress?.isComplete || course.isCompletedForCurrentUser) ? "completed" : "incomplete"}
      >
        <div className="flex h-full flex-col justify-between gap-4">
          <div className="min-w-0">
            <div className="flex min-w-0 items-center gap-2">
              <div className="min-w-0 flex-1 text-lg font-semibold leading-7 truncate">{course.title}</div>
              {hiddenFromStudents ? <Badge intent="outline"><EyeOff size={13} className="mr-1 inline" />Скрыт</Badge> : null}
              {groupRestricted ? <Badge intent="outline">По группам</Badge> : null}
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
        {isDropTarget && dropMode === "inside" ? <div className="dnd-nest-hint">Вложить курс сюда</div> : null}
      </Card>
    </div>
  );
}

export default function CoursesPage() {
  const queryClient = useQueryClient();
  const { user } = useAuth();
  const currentUserId = String(user?.id || user?.userId || user?.uuid || '');
  const cachedStateRef = useRef(queryClient.getQueryData(COURSES_PAGE_STATE_KEY));
  const cachedState = cachedStateRef.current || {};
  const [items, setItems] = useState(() => Array.isArray(cachedState.items) ? cachedState.items : []);
  const [q, setQ] = useState(() => String(cachedState.q || ""));
  const [dataLoadedAt, setDataLoadedAt] = useState(() => Number(cachedState.dataLoadedAt || 0));
  const [loading, setLoading] = useState(() => !cachedState.dataLoadedAt);
  const [loadingMore, setLoadingMore] = useState(false);
  const [loadError, setLoadError] = useState("");
  const [page, setPage] = useState(() => Number(cachedState.page || 1));
  const [hasMore, setHasMore] = useState(() => Boolean(cachedState.hasMore));
  const [total, setTotal] = useState(() => Number(cachedState.total || 0));
  const [directProgressByCourseId, setDirectProgressByCourseId] = useState(() => cachedState.directProgressByCourseId instanceof Map ? cachedState.directProgressByCourseId : new Map());
  const [progressLoadedAt, setProgressLoadedAt] = useState(() => Number(cachedState.progressLoadedAt || 0));
  const [progressIdsKey, setProgressIdsKey] = useState(() => String(cachedState.progressIdsKey || ''));
  const [progressionRevisionSeen, setProgressionRevisionSeen] = useState(() => Number(cachedState.progressionRevisionSeen || 0));
  const [progressionRevision, setProgressionRevision] = useState(() => getCourseProgressionRevision(currentUserId));
  const [progressLoading, setProgressLoading] = useState(false);
  const [draggedCourseId, setDraggedCourseId] = useState(null);
  const [dragOverCourseId, setDragOverCourseId] = useState(null);
  const [dragOverMode, setDragOverMode] = useState("before");
  const [dragOverEdge, setDragOverEdge] = useState("top");
  const [contextMenu, setContextMenu] = useState({ open: false, x: 0, y: 0, course: null });
  const dragStartedRef = useRef(false);
  const progressMountRefreshRef = useRef(true);

  const nav = useNavigate();
  const notify = useNotify();
  const { canEdit, isEditorMode } = useEditorMode();

  const editorTools = canEdit && isEditorMode;
  const closeContextMenu = () => setContextMenu((current) => current.open ? { ...current, open: false } : current);
  const openContextMenu = (event, selectedCourse = null) => {
    if (!selectedCourse && !editorTools) {
      claimContextMenuEvent(event);
      closeContextMenu();
      return;
    }
    if (!claimContextMenuEvent(event)) return;
    setContextMenu({ open: true, x: event.clientX, y: event.clientY, course: selectedCourse });
  };

  const orderedRootCourses = useMemo(
    () => (items || []).filter((course) => !course?.parentCourseId).sort(compareCourses),
    [items],
  );
  const rootCourses = useMemo(() => {
    const query = q.trim().toLowerCase();
    if (!query) return orderedRootCourses;
    return orderedRootCourses.filter((course) => (
      String(course?.title || "").toLowerCase().includes(query)
      || String(course?.description || "").toLowerCase().includes(query)
    ));
  }, [orderedRootCourses, q]);


  const visibleCourses = useMemo(
    () => (items || []).filter((course) => course?.id),
    [items],
  );
  const visibleCourseIdsKey = useMemo(
    () => visibleCourses.map((course) => String(course.id)).sort().join(','),
    [visibleCourses],
  );

  const progressByCourseId = useMemo(() => {
    const childrenByParent = buildChildrenByParent(visibleCourses);
    const next = {};
    for (const course of rootCourses) {
      const subtreeIds = collectCourseSubtreeIds(course.id, childrenByParent);
      const progress = sumCourseProgress(subtreeIds, directProgressByCourseId);
      next[course.id] = progressLoading
        ? { ...progress, loading: true }
        : progress;
    }
    return next;
  }, [directProgressByCourseId, progressLoading, rootCourses, visibleCourses]);

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
      setDataLoadedAt(Date.now());
    } catch (e) {
      const parsed = handleApiError(e, notify, "Не удалось загрузить курсы");
      setLoadError(parsed?.userMessage || "Не удалось загрузить курсы");
    } finally {
      setLoading(false);
      setLoadingMore(false);
    }
  };

  useEffect(() => {
    const cacheFresh = dataLoadedAt > 0 && Date.now() - dataLoadedAt < COURSES_CACHE_STALE_MS;
    if (cacheFresh) {
      setLoading(false);
      return;
    }
    loadCourses({ reset: true });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    queryClient.setQueryData(COURSES_PAGE_STATE_KEY, {
      items,
      q,
      page,
      hasMore,
      total,
      dataLoadedAt,
      directProgressByCourseId,
      progressLoadedAt,
      progressIdsKey,
      progressionRevisionSeen,
    });
  }, [dataLoadedAt, directProgressByCourseId, hasMore, items, page, progressIdsKey, progressLoadedAt, progressionRevisionSeen, q, queryClient, total]);

  useEffect(() => {
    const syncProgressionRevision = () => {
      setProgressionRevision(getCourseProgressionRevision(currentUserId));
    };
    syncProgressionRevision();
    const onStorage = (event) => {
      if (event?.key === COURSE_PROGRESSION_STORAGE_KEY) syncProgressionRevision();
    };
    window.addEventListener(COURSE_PROGRESSION_CHANGED_EVENT, syncProgressionRevision);
    window.addEventListener('storage', onStorage);
    return () => {
      window.removeEventListener(COURSE_PROGRESSION_CHANGED_EVENT, syncProgressionRevision);
      window.removeEventListener('storage', onStorage);
    };
  }, [currentUserId]);

  useEffect(() => {
    let cancelled = false;
    if (!visibleCourseIdsKey) {
      if (directProgressByCourseId.size > 0) setDirectProgressByCourseId(new Map());
      if (progressIdsKey) setProgressIdsKey('');
      if (progressLoadedAt) setProgressLoadedAt(0);
      setProgressLoading(false);
      return () => {
        cancelled = true;
      };
    }

    const cachedProgressFresh = progressIdsKey === visibleCourseIdsKey
      && progressLoadedAt > 0
      && progressionRevisionSeen >= progressionRevision
      && Date.now() - progressLoadedAt < COURSES_CACHE_STALE_MS;
    if (cachedProgressFresh && !progressMountRefreshRef.current) {
      setProgressLoading(false);
      return () => {
        cancelled = true;
      };
    }

    progressMountRefreshRef.current = false;
    setProgressLoading(true);
    getCourseProgressByCourses(visibleCourseIdsKey.split(','))
      .then((rows) => {
        if (cancelled) return;
        setDirectProgressByCourseId(normalizeProgressRows(rows));
        setProgressIdsKey(visibleCourseIdsKey);
        setProgressLoadedAt(Date.now());
        setProgressionRevisionSeen(progressionRevision);
      })
      .catch(() => {
        if (cancelled) return;
        setDirectProgressByCourseId(new Map());
      })
      .finally(() => {
        if (!cancelled) setProgressLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [directProgressByCourseId.size, progressionRevision, progressionRevisionSeen, progressIdsKey, progressLoadedAt, visibleCourseIdsKey]);


  const handleCreate = async () => {
    try {
      const { id } = await createCourse({
        title: "Новый курс",
        description: "Описание курса",
        isPublic: false,
        visibleGroupIds: [],
        ownerIds: [],
        parentCourseId: null,
        sort: orderedRootCourses.length,
      });
      notify.success("Курс создан");
      nav(`/courses/${id}/edit`);
    } catch (e) {
      handleApiError(e, notify, "Не удалось создать курс");
    }
  };

  const handleCreateNested = async (parentCourse) => {
    if (!editorTools || !parentCourse || parentCourse.canEdit === false) return;
    try {
      const { id } = await createCourse({
        title: "Новый вложенный курс",
        description: "Описание курса",
        isPublic: false,
        visibleGroupIds: [],
        ownerIds: [],
        parentCourseId: parentCourse.id,
        sort: 0,
      });
      notify.success("Вложенный курс создан");
      if (id) nav(`/courses/${id}/edit?returnTo=${encodeURIComponent(`/course/${parentCourse.id}`)}`);
    } catch (e) {
      handleApiError(e, notify, "Не удалось создать вложенный курс");
    }
  };

  const copyCourseLink = async (selectedCourse) => {
    try {
      await navigator.clipboard.writeText(`${window.location.origin}/course/${selectedCourse.id}`);
      notify.success("Ссылка на курс скопирована");
    } catch {
      notify.warn("Не удалось скопировать ссылку");
    }
  };

  const getDropIntent = (event, targetCourse) => resolveCardDropIntent(event, { allowInside: targetCourse?.canEdit !== false });

  const resetDrag = () => {
    setDraggedCourseId(null);
    setDragOverCourseId(null);
    setDragOverMode("before");
    setDragOverEdge("top");
    setTimeout(() => {
      dragStartedRef.current = false;
    }, 0);
  };

  const moveRootCourse = async (sourceCourse, targetCourse, mode) => {
    const nextRoot = orderedRootCourses.filter((x) => String(x.id) !== String(sourceCourse.id));
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


  const handleDropCourse = async (targetCourse, modeFromEvent) => {
    const sourceId = draggedCourseId;
    const mode = modeFromEvent === "inside" ? "inside" : modeFromEvent === "after" ? "after" : "before";
    resetDrag();
    if (!editorTools || !sourceId || String(sourceId) === String(targetCourse.id)) return;

    const sourceCourse = items.find((x) => String(x.id) === String(sourceId));
    if (!sourceCourse || sourceCourse.canEdit === false) {
      notify.warn("Вы не владелец курса — менять порядок нельзя");
      return;
    }

    if (mode === "inside" && targetCourse.canEdit === false) {
      notify.warn("Нельзя вкладывать курс в курс без прав на целевой курс");
      return;
    }

    try {
      if (mode === "inside") {
        setItems((prev) => prev.map((item) => String(item.id) === String(sourceCourse.id)
          ? { ...item, parentCourseId: targetCourse.id }
          : item));
        await moveCoursePosition(sourceCourse.id, targetCourse.id, null);
        notify.success(`Курс вложен в «${targetCourse.title || 'курс'}»`);
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
    if (editorTools && course.canEdit !== false) {
      nav(`/courses/${course.id}/edit`);
      return;
    }
    nav(`/course/${course.id}`);
  };

  return (
    <>
      <div className="page-hero-card mb-6 rounded-[28px] p-5 sm:p-6">
        <div className="flex flex-col gap-4 xl:flex-row xl:items-start xl:justify-between">
          <div className="max-w-3xl">
            <div className="text-xs font-semibold uppercase tracking-[0.24em] text-neutral-400">Каталог</div>
            <h1 className="mt-2 text-3xl font-semibold tracking-tight sm:text-4xl">Курсы</h1>
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
        <div
          className="auto-fill-grid"
          onContextMenuCapture={(event) => { if (event.target === event.currentTarget) openContextMenu(event, null); }}
          onDragOver={(event) => {
            if (!draggedCourseId || !editorTools) return;
            if (isPointerInsideDndItem(event, '[data-dnd-course-id]')) return;
            const intent = resolveGridGapDropIntent(
              event.currentTarget,
              event.clientX,
              event.clientY,
              '[data-dnd-course-id]',
              String(draggedCourseId),
              (element) => String(element.dataset.dndCourseId || ''),
            );
            if (!intent) return;
            event.preventDefault();
            event.dataTransfer.dropEffect = 'move';
            setDragOverCourseId(intent.key);
            setDragOverMode(intent.mode);
            setDragOverEdge(intent.edge);
          }}
          onDrop={(event) => {
            if (!draggedCourseId || !editorTools) return;
            if (isPointerInsideDndItem(event, '[data-dnd-course-id]')) return;
            const intent = resolveGridGapDropIntent(
              event.currentTarget,
              event.clientX,
              event.clientY,
              '[data-dnd-course-id]',
              String(draggedCourseId),
              (element) => String(element.dataset.dndCourseId || ''),
            );
            if (!intent) return;
            event.preventDefault();
            const targetCourse = orderedRootCourses.find((item) => String(item.id) === String(intent.key));
            if (targetCourse) handleDropCourse(targetCourse, intent.mode);
          }}
        >
          {rootCourses.map((course, index) => (
            <CourseCard
              key={course.id}
              course={course}
              revealIndex={index}
              editorTools={editorTools}
              progress={progressByCourseId[course.id]}
              isDragged={String(draggedCourseId || "") === String(course.id)}
              isDropTarget={String(dragOverCourseId || "") === String(course.id)}
              dropMode={String(dragOverCourseId || "") === String(course.id) ? dragOverMode : ""}
              dropEdge={String(dragOverCourseId || "") === String(course.id) && dragOverMode !== "inside" ? dragOverEdge : ""}
              onNavigate={() => navigateCourse(course)}
              onContextMenu={(event) => openContextMenu(event, course)}
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
                const intent = getDropIntent(e, course);
                setDragOverCourseId(course.id);
                setDragOverMode(intent.mode);
                setDragOverEdge(intent.edge);
              }}
              onDragOver={(e) => {
                if (!draggedCourseId || String(draggedCourseId) === String(course.id)) return;
                e.preventDefault();
                e.dataTransfer.dropEffect = "move";
                const intent = getDropIntent(e, course);
                setDragOverCourseId(course.id);
                setDragOverMode(intent.mode);
                setDragOverEdge(intent.edge);
              }}
              onDragLeave={(e) => {
                if (e.currentTarget?.contains?.(e.relatedTarget)) return;
                if (String(dragOverCourseId || "") === String(course.id)) {
                  setDragOverCourseId(null);
                  setDragOverMode("before");
                  setDragOverEdge("top");
                }
              }}
              onDrop={(e) => {
                e.preventDefault();
                e.stopPropagation();
                const intent = getDropIntent(e, course);
                handleDropCourse(course, intent.mode);
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

      <ContextMenu
        open={contextMenu.open}
        x={contextMenu.x}
        y={contextMenu.y}
        onClose={closeContextMenu}
        ariaLabel="Действия курса"
      >
        {contextMenu.course ? (
          <>
            <ContextMenuLabel>Курс</ContextMenuLabel>
            <ContextMenuItem icon={ExternalLink} onClick={() => { const selected = contextMenu.course; closeContextMenu(); nav(`/course/${selected.id}`); }}>Открыть курс</ContextMenuItem>
            {editorTools && contextMenu.course.canEdit !== false ? (
              <ContextMenuItem icon={Pencil} onClick={() => { const selected = contextMenu.course; closeContextMenu(); nav(`/courses/${selected.id}/edit`); }}>Редактировать</ContextMenuItem>
            ) : null}
            <ContextMenuItem icon={Copy} onClick={() => { const selected = contextMenu.course; closeContextMenu(); void copyCourseLink(selected); }}>Скопировать ссылку</ContextMenuItem>
            {editorTools && contextMenu.course.canEdit !== false ? (
              <>
                <ContextMenuSeparator />
                <ContextMenuItem icon={FolderPlus} onClick={() => { const selected = contextMenu.course; closeContextMenu(); void handleCreateNested(selected); }}>Создать вложенный курс</ContextMenuItem>
              </>
            ) : null}
          </>
        ) : editorTools ? (
          <>
            <ContextMenuLabel>Каталог</ContextMenuLabel>
            <ContextMenuItem icon={Plus} onClick={() => { closeContextMenu(); void handleCreate(); }}>Создать курс</ContextMenuItem>
          </>
        ) : null}
      </ContextMenu>
    </>
  );
}
