import React, { useEffect, useMemo, useRef, useState } from "react";
import { useLocation, useNavigate, useParams, useSearchParams } from "react-router-dom";

import { Card, Button, Input, Textarea, Badge } from "../../components/ui";

import { getCourse, getCourses, createCourse, updateCourseSort, moveCoursePosition } from "../../api/courses";
import { getApiErrorMessage } from "../../api/http";

import {
  getAssignmentsByCourse,
  getCourseProgressByCourses,
  createAssignment,
  importAssignmentsFromJson,
  exportAssignmentsToJson,
  updateAssignmentSort,
} from "../../api/assignments";
import { Plus, Layers, FileJson, Upload, X, Copy, Sparkles, Download, GitCompare, AlertTriangle, ExternalLink, Pencil, FilePlus2, FolderPlus } from "lucide-react";
import IfEditor from "../../components/IfEditor";
import { useNotify } from "../../components/notify/NotifyProvider";
import { handleApiError } from "../../utils/handleApiError";
import { notifyOnce } from "../../utils/notifyOnce";
import { resolveCardDropIntent, resolveFlowNodeDropIntent } from "../../utils/gridDragDrop";
import useQuery from '../../hooks/useQuery';
import { useQueryClient } from '../../data/QueryClientProvider';
import { useEditorMode } from '../../contexts/EditorModeContext';
import { ContextMenu, ContextMenuItem, ContextMenuLabel, ContextMenuSeparator } from '../../components/ui/ContextMenu';

import {
  isAssignmentSolved,
  normalizeProgressRows,
  buildChildrenByParent,
  collectCourseSubtreeIds,
  sumCourseProgress,
  SORT_OPTIONS,
  compareCourses,
  contentKey,
  contentTitle,
  contentCreatedAt,
  compareContentItems,
  makeCourseContentItem,
  makeAssignmentContentItem,
  CREATE_OPTIONS,
  JSON_IMPORT_EXAMPLES,
  JSON_IMPORT_DOC_FIELDS,
  buildDefaultAssignmentPayload,
  summarizeImportPayload,
  shortImportValue,
  buildJsonImportDiff,
} from './courseAssignmentsModel';
import CourseContentGrid from './components/CourseContentGrid';
import CourseFlowEditor from './components/CourseFlowEditor';
import CourseLayoutToggle from './components/CourseLayoutToggle';
import { resolveRootCourseId } from './courseMapModel';
import { navigateToCourseEditor } from './courseMapNavigation';
const EMPTY_LIST = Object.freeze([]);
const EMPTY_COURSE_BUNDLE = Object.freeze({
  course: null,
  allCourses: EMPTY_LIST,
  childCourses: EMPTY_LIST,
  courseCanEdit: true,
});

export default function CourseAssignmentsPage() {
  const { courseId } = useParams();
  const nav = useNavigate();
  const location = useLocation();
  const [params, setParams] = useSearchParams();
  const notify = useNotify();
  const queryClient = useQueryClient();
  const { isEditorMode } = useEditorMode();
  const assignmentsKey = useMemo(() => ['course-assignments', courseId], [courseId]);
  const courseBundleKey = useMemo(() => ['course-bundle', courseId], [courseId]);

  const assignmentsQuery = useQuery({
    queryKey: assignmentsKey,
    queryFn: async () => {
      const data = await getAssignmentsByCourse(courseId);
      return (data || []).map((item, index) => ({
        ...item,
        sort: typeof item.sort === 'number' ? item.sort : index,
      }));
    },
    enabled: Boolean(courseId),
    staleTime: 20_000,
    keepPreviousData: true,
  });

  const courseBundleQuery = useQuery({
    queryKey: courseBundleKey,
    queryFn: async () => {
      const [loadedCourse, coursesPayload] = await Promise.all([
        getCourse(courseId),
        getCourses().catch(() => []),
      ]);
      const payloadItems = Array.isArray(coursesPayload?.items) ? coursesPayload.items : coursesPayload;
      const all = Array.isArray(payloadItems) ? payloadItems : [];
      const children = all
        .filter((item) => String(item?.parentCourseId || '') === String(courseId))
        .sort(compareCourses);
      return {
        course: loadedCourse || null,
        allCourses: all,
        childCourses: children,
        courseCanEdit: typeof loadedCourse?.canEdit === 'boolean' ? Boolean(loadedCourse.canEdit) : true,
      };
    },
    enabled: Boolean(courseId),
    staleTime: 30_000,
    keepPreviousData: true,
  });

  const items = assignmentsQuery.data || EMPTY_LIST;
  const courseBundle = courseBundleQuery.data || EMPTY_COURSE_BUNDLE;
  const course = courseBundle.course || null;
  const childCourses = courseBundle.childCourses || EMPTY_LIST;
  const allCourses = courseBundle.allCourses || EMPTY_LIST;
  const courseCanEdit = courseBundle.courseCanEdit !== false;
  const loading = assignmentsQuery.isLoading || courseBundleQuery.isLoading;
  const err = assignmentsQuery.error ? getApiErrorMessage(assignmentsQuery.error, 'Не удалось загрузить задания') : '';

  const setItems = React.useCallback((updater) => {
    queryClient.setQueryData(assignmentsKey, (previous = []) => (
      typeof updater === 'function' ? updater(previous || []) : updater
    ));
  }, [assignmentsKey, queryClient]);

  const setChildCourses = React.useCallback((updater) => {
    queryClient.setQueryData(courseBundleKey, (previous = {}) => {
      const currentChildren = previous.childCourses || [];
      const nextChildren = typeof updater === 'function' ? updater(currentChildren) : updater;
      const nextById = new Map((nextChildren || []).map((item) => [String(item.id), item]));
      const childIds = new Set(currentChildren.map((item) => String(item.id)));
      const retained = (previous.allCourses || []).filter((item) => !childIds.has(String(item.id)));
      return {
        ...previous,
        childCourses: nextChildren || [],
        allCourses: [...retained, ...Array.from(nextById.values())],
      };
    });
  }, [courseBundleKey, queryClient]);

  const [childProgressByCourseId, setChildProgressByCourseId] = useState({});
  const [courseProgressByCourseId, setCourseProgressByCourseId] = useState({});
  const [q, setQ] = useState('');
  const [contentLayout, setContentLayout] = useState(() => {
    if (typeof window === 'undefined') return 'flow';
    try {
      const saved = window.localStorage.getItem('taskforge-course-editor-layout');
      return saved === 'grid' ? 'grid' : 'flow';
    } catch {
      return 'flow';
    }
  });

  const [createDialogOpen, setCreateDialogOpen] = useState(false);
  const [createMode, setCreateMode] = useState('choice');
  const [jsonDocsOpen, setJsonDocsOpen] = useState(false);
  const [createBusyType, setCreateBusyType] = useState('');
  const [jsonImportText, setJsonImportText] = useState('');
  const [jsonImportBusy, setJsonImportBusy] = useState(false);
  const [jsonExportBusy, setJsonExportBusy] = useState(false);
  const [jsonImportPreview, setJsonImportPreview] = useState('пусто');
  const [jsonImportDiffOpen, setJsonImportDiffOpen] = useState(false);
  const [jsonImportDiff, setJsonImportDiff] = useState(null);
  const [jsonImportParsed, setJsonImportParsed] = useState(null);
  const [draggedContentKey, setDraggedContentKey] = useState(null);
  const [dragOverContentKey, setDragOverContentKey] = useState(null);
  const [dragOverContentMode, setDragOverContentMode] = useState('before');
  const [dragOverContentEdge, setDragOverContentEdge] = useState('top');
  const [extractDropActive, setExtractDropActive] = useState(false);
  const [contextMenu, setContextMenu] = useState({ open: false, x: 0, y: 0, entry: null, canEdit: false });
  const [createMenu, setCreateMenu] = useState({ open: false, x: 0, y: 0 });
  const dragStartedRef = useRef(false);

  const sortMode = params.get('sort') || 'default';
  const showFlowLayout = isEditorMode ? (courseCanEdit && contentLayout === 'flow') : true;

  useEffect(() => {
    if (typeof window === 'undefined') return;
    try {
      window.localStorage.setItem('taskforge-course-editor-layout', contentLayout);
    } catch {}
  }, [contentLayout]);

  useEffect(() => {
    if (!showFlowLayout || !course?.id || !course?.parentCourseId) return;
    const root = resolveRootCourseId(course.id, [...(allCourses || []), course]);
    if (!root || String(root) === String(course.id)) return;
    const next = new URLSearchParams();
    next.set('focusCourse', String(course.id));
    nav(`/course/${root}?${next.toString()}`, { replace: true });
  }, [allCourses, course, nav, showFlowLayout]);

  const closeContextMenu = React.useCallback(() => {
    setContextMenu((current) => current.open ? { ...current, open: false } : current);
  }, []);

  const closeCreateMenu = React.useCallback(() => {
    setCreateMenu((current) => current.open ? { ...current, open: false } : current);
  }, []);

  const openCreateMenu = React.useCallback((event) => {
    const rect = event.currentTarget.getBoundingClientRect();
    setCreateMenu({ open: true, x: rect.left, y: rect.bottom + 7 });
  }, []);

  const openContextMenu = React.useCallback((event, entry = null, canEditEntry = false) => {
    if (!isEditorMode || !courseCanEdit) return;
    event.preventDefault();
    event.stopPropagation();
    setContextMenu({
      open: true,
      x: event.clientX,
      y: event.clientY,
      entry,
      canEdit: Boolean(canEditEntry),
    });
  }, [courseCanEdit, isEditorMode]);

  const openCreateDialog = React.useCallback((mode = 'json') => {
    closeContextMenu();
    closeCreateMenu();
    setCreateMode(mode);
    setJsonDocsOpen(false);
    setCreateDialogOpen(true);
  }, [closeContextMenu, closeCreateMenu]);

  useEffect(() => {
    if (!jsonImportDiffOpen) return undefined;

    const previousBodyOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';

    const handleKeyDown = (event) => {
      if (event.key === 'Escape' && !jsonImportBusy) setJsonImportDiffOpen(false);
    };

    window.addEventListener('keydown', handleKeyDown);
    return () => {
      document.body.style.overflow = previousBodyOverflow;
      window.removeEventListener('keydown', handleKeyDown);
    };
  }, [jsonImportDiffOpen, jsonImportBusy]);

  const reloadAssignments = React.useCallback(async () => queryClient.fetchQuery({
    queryKey: assignmentsKey,
    queryFn: async () => {
      const data = await getAssignmentsByCourse(courseId);
      return (data || []).map((item, index) => ({
        ...item,
        sort: typeof item.sort === 'number' ? item.sort : index,
      }));
    },
    staleTime: 0,
    force: true,
  }), [assignmentsKey, courseId, queryClient]);

  const reloadCourseData = React.useCallback(async () => {
    const next = await queryClient.fetchQuery({
      queryKey: courseBundleKey,
      queryFn: async () => {
        const [loadedCourse, coursesPayload] = await Promise.all([
          getCourse(courseId),
          getCourses().catch(() => []),
        ]);
        const payloadItems = Array.isArray(coursesPayload?.items) ? coursesPayload.items : coursesPayload;
        const all = Array.isArray(payloadItems) ? payloadItems : [];
        return {
          course: loadedCourse || null,
          allCourses: all,
          childCourses: all.filter((item) => String(item?.parentCourseId || '') === String(courseId)).sort(compareCourses),
          courseCanEdit: typeof loadedCourse?.canEdit === 'boolean' ? Boolean(loadedCourse.canEdit) : true,
        };
      },
      staleTime: 0,
      force: true,
    });
    return next?.childCourses || [];
  }, [courseBundleKey, courseId, queryClient]);

  const contentItems = useMemo(() => {
    const courses = (childCourses || []).map(makeCourseContentItem);
    const assignments = (items || []).map((assignment, index) => makeAssignmentContentItem(assignment, index));
    return [...courses, ...assignments];
  }, [childCourses, items]);


  const progressContext = useMemo(() => {
    if (!courseId || !courseBundleQuery.isSuccess) return null;
    const knownCourses = [...allCourses];
    if (course?.id && !knownCourses.some((item) => String(item?.id || '') === String(course.id))) {
      knownCourses.push(course);
    }
    const childrenByParent = buildChildrenByParent(knownCourses);
    const courseIds = collectCourseSubtreeIds(courseId, childrenByParent);
    return {
      childrenByParent,
      courseIds,
      requestKey: courseIds.map(String).sort().join(','),
    };
  }, [allCourses, course, courseBundleQuery.isSuccess, courseId]);

  const progressRevision = `${courseBundleQuery.updatedAt || 0}:${assignmentsQuery.updatedAt || 0}`;

  useEffect(() => {
    let cancelled = false;
    if (!progressContext || assignmentsQuery.isLoading || !progressContext.requestKey) {
      return () => {
        cancelled = true;
      };
    }

    const { childrenByParent, courseIds } = progressContext;
    setCourseProgressByCourseId((previous) => {
      if (previous[courseId]?.loading) return previous;
      return {
        ...previous,
        [courseId]: previous[courseId] || { loading: true, total: 0, solved: 0, percent: 0, isComplete: false },
      };
    });
    setChildProgressByCourseId((previous) => {
      let changed = false;
      const next = { ...previous };
      for (const child of childCourses) {
        if (child?.id && !next[child.id]) {
          next[child.id] = { loading: true, total: 0, solved: 0, percent: 0, isComplete: false };
          changed = true;
        }
      }
      return changed ? next : previous;
    });

    getCourseProgressByCourses(courseIds)
      .then((rows) => {
        if (cancelled) return;
        const directProgress = normalizeProgressRows(rows);
        const currentProgress = sumCourseProgress(courseIds, directProgress);
        const nextChildProgress = {};
        for (const child of childCourses) {
          if (!child?.id) continue;
          const childSubtreeIds = collectCourseSubtreeIds(child.id, childrenByParent);
          nextChildProgress[child.id] = sumCourseProgress(childSubtreeIds, directProgress);
        }
        setCourseProgressByCourseId({ [courseId]: currentProgress });
        setChildProgressByCourseId(nextChildProgress);
      })
      .catch(() => {
        if (cancelled) return;
        const directTotal = items.length;
        const directSolved = items.filter(isAssignmentSolved).length;
        const directPercent = directTotal > 0 ? Math.round((directSolved / directTotal) * 100) : 0;
        const failedCurrent = { total: directTotal, solved: directSolved, percent: directPercent, isComplete: directTotal > 0 && directSolved === directTotal, loading: false, failed: true };
        const failedChildren = {};
        for (const child of childCourses) {
          if (child?.id) failedChildren[child.id] = failedCurrent;
        }
        setCourseProgressByCourseId({ [courseId]: failedCurrent });
        setChildProgressByCourseId(failedChildren);
      });

    return () => {
      cancelled = true;
    };
  }, [assignmentsQuery.isLoading, childCourses, courseId, items, progressContext, progressRevision]);


  const filtered = useMemo(() => {
    const query = q.trim().toLowerCase();
    const s = (contentItems || []).filter((x) => {
      if (!query) return true;
      return (
        contentTitle(x).toLowerCase().includes(query) ||
        String(x.description || "").toLowerCase().includes(query) ||
        String(x.tags || "").toLowerCase().includes(query)
      );
    });

    const byTitle = (a, b, dir = 1) =>
      contentTitle(a).localeCompare(contentTitle(b), "ru", { sensitivity: "base" }) * dir;
    const byCreated = (a, b, dir = 1) =>
      ((new Date(contentCreatedAt(a) || 0).getTime()) - (new Date(contentCreatedAt(b) || 0).getTime())) * dir;

    switch (sortMode) {
      case "title_asc":
        return [...s].sort((a, b) => byTitle(a, b, +1));
      case "title_desc":
        return [...s].sort((a, b) => byTitle(a, b, -1));
      case "created_asc":
        return [...s].sort((a, b) => byCreated(a, b, +1));
      case "created_desc":
        return [...s].sort((a, b) => byCreated(a, b, -1));
      case "default":
      default:
        return [...s].sort(compareContentItems);
    }
  }, [contentItems, q, sortMode]);

  const orderedAll = useMemo(() => [...(contentItems || [])].sort(compareContentItems), [contentItems]);

  const positionByKey = useMemo(() => {
    const m = new Map();
    orderedAll.forEach((x, idx) => m.set(x.key, idx + 1));
    return m;
  }, [orderedAll]);

  const canEdit = useMemo(() => {
    if (!items || items.length === 0) return true;
    const any = items.find((x) => typeof x?.canEdit === "boolean");
    return any ? !!any.canEdit : true;
  }, [items]);

  const directCourseProgress = useMemo(() => {
    const total = (items || []).length;
    const solved = (items || []).filter(isAssignmentSolved).length;
    const percent = total > 0 ? Math.round((solved / total) * 100) : 0;
    return { total, solved, percent, isComplete: total > 0 && solved === total, loading: false };
  }, [items]);

  const courseProgress = useMemo(() => {
    return courseProgressByCourseId[courseId] || directCourseProgress;
  }, [courseProgressByCourseId, courseId, directCourseProgress]);

  const setSortMode = (mode) => {
    const next = new URLSearchParams(params);
    next.set("sort", mode);
    setParams(next, { replace: true });
  };

  const canReorderContentItem = (item) => {
    if (!courseCanEdit) return false;
    if (item?.kind === "course") return item.course?.canEdit !== false;
    return canEdit && item?.assignment?.canEdit !== false;
  };

  const contentDropIntentFromEvent = (event, targetEntry) => {
    const sourceKey = event?.dataTransfer?.getData?.('text/plain') || draggedContentKey;
    const source = orderedAll.find((entry) => entry.key === sourceKey);
    const allowInside = source?.kind === 'course'
      && targetEntry?.kind === 'course'
      && source.key !== targetEntry.key
      && canReorderContentItem(targetEntry);
    const isFlowNode = event?.currentTarget?.dataset?.courseFlowNode === 'true';
    return isFlowNode
      ? resolveFlowNodeDropIntent(event, { allowInside })
      : resolveCardDropIntent(event, { allowInside });
  };

  const moveCourseIntoParent = async (sourceKey, parentCourseId) => {
    const source = orderedAll.find((x) => x.key === sourceKey);
    if (!source || source.kind !== "course" || !canReorderContentItem(source)) {
      notify.error("Недостаточно прав");
      return;
    }

    const parentId = parentCourseId || null;
    setChildCourses((prev) => prev.filter((x) => String(x.id) !== String(source.id)));
    try {
      await moveCoursePosition(source.id, parentId, null);
      notify.success(parentId ? "Курс вложен" : "Курс вынесен на уровень выше");
      await reloadCourseData();
    } catch (e) {
      handleApiError(e, notify, "Не удалось переместить курс");
      await reloadCourseData().catch(() => []);
    }
  };

  const saveMixedOrder = async (nextOrder) => {
    const calls = [];
    nextOrder.forEach((entry, index) => {
      if (entry.sort === index) return;
      if (entry.kind === "course") calls.push(updateCourseSort(entry.id, index));
      else calls.push(updateAssignmentSort(entry.id, index));
    });
    if (calls.length > 0) await Promise.all(calls);
  };

  const moveContentRelativeToTarget = async (sourceKey, targetKey, mode) => {
    if (!courseCanEdit) {
      notify.error("Недостаточно прав");
      return;
    }
    if (sortMode !== "default") {
      notify.info("Изменение позиции доступно только в стандартной сортировке");
      return;
    }

    const source = orderedAll.find((x) => x.key === sourceKey);
    const target = orderedAll.find((x) => x.key === targetKey);
    if (!source || !target || source.key === target.key) return;
    if (!canReorderContentItem(source)) {
      notify.error("Недостаточно прав");
      return;
    }

    const nextOrder = orderedAll.filter((entry) => entry.key !== sourceKey);
    const targetIndex = nextOrder.findIndex((entry) => entry.key === targetKey);
    if (targetIndex < 0) return;
    const insertIndex = targetIndex + (mode === 'after' ? 1 : 0);
    nextOrder.splice(insertIndex, 0, source);

    const newSort = new Map(nextOrder.map((entry, index) => [entry.key, index]));
    setItems((prev) => prev.map((item) => {
      const next = newSort.get(contentKey("assignment", item.id));
      return Number.isFinite(next) ? { ...item, sort: next } : item;
    }));
    setChildCourses((prev) => prev.map((item) => {
      const next = newSort.get(contentKey("course", item.id));
      return Number.isFinite(next) ? { ...item, sort: next } : item;
    }));

    try {
      await saveMixedOrder(nextOrder);
      notify.success("Порядок обновлён");
    } catch (e) {
      handleApiError(e, notify, "Не удалось изменить порядок");
      await Promise.all([
        reloadAssignments(true).catch(() => []),
        reloadCourseData().catch(() => []),
      ]);
    }
  };

  const handleDropOnContentItem = async (targetKey, sourceFromEvent, mode = dragOverContentMode) => {
    const sourceKey = sourceFromEvent || draggedContentKey;
    setDraggedContentKey(null);
    setDragOverContentKey(null);
    setDragOverContentMode('before');
    setDragOverContentEdge('top');
    if (!sourceKey || !targetKey || sourceKey === targetKey) return;
    if (sortMode !== "default") {
      notify.info("Перетаскивание доступно только в стандартной сортировке");
      return;
    }

    if (mode === 'inside') {
      const source = orderedAll.find((entry) => entry.key === sourceKey);
      const target = orderedAll.find((entry) => entry.key === targetKey);
      if (source?.kind !== 'course' || target?.kind !== 'course') return;
      if (!canReorderContentItem(target)) {
        notify.warn('Нельзя вкладывать курс в курс без прав на целевой курс');
        return;
      }
      await moveCourseIntoParent(sourceKey, target.id);
      return;
    }

    await moveContentRelativeToTarget(sourceKey, targetKey, mode === 'after' ? 'after' : 'before');
  };

  const dragHandlersRef = useRef({});
  dragHandlersRef.current = {
    contentDropIntentFromEvent,
    handleDropOnContentItem,
    draggedContentKey,
    dragOverContentKey,
    setDraggedContentKey,
    setDragOverContentKey,
    setDragOverContentMode,
    setDragOverContentEdge,
    setExtractDropActive,
    dragStartedRef,
    nav,
  };
  const dragApi = useMemo(() => ({
    openAfterDrag(href) {
      const current = dragHandlersRef.current;
      if (current.dragStartedRef.current) {
        current.dragStartedRef.current = false;
        return;
      }
      current.nav(href);
    },
    onStart(event, entry) {
      const current = dragHandlersRef.current;
      current.dragStartedRef.current = true;
      event.dataTransfer.effectAllowed = 'move';
      event.dataTransfer.setData('text/plain', entry.key);
      current.setDraggedContentKey(entry.key);
    },
    onEnter(event, entry) {
      const current = dragHandlersRef.current;
      if (!current.draggedContentKey || current.draggedContentKey === entry.key) return;
      event.preventDefault();
      const intent = current.contentDropIntentFromEvent(event, entry);
      current.setDragOverContentKey(entry.key);
      current.setDragOverContentMode(intent.mode);
      current.setDragOverContentEdge(intent.edge);
    },
    onOver(event, entry) {
      const current = dragHandlersRef.current;
      if (!current.draggedContentKey || current.draggedContentKey === entry.key) return;
      event.preventDefault();
      event.dataTransfer.dropEffect = 'move';
      const intent = current.contentDropIntentFromEvent(event, entry);
      current.setDragOverContentKey(entry.key);
      current.setDragOverContentMode(intent.mode);
      current.setDragOverContentEdge(intent.edge);
    },
    onLeave(event, entry) {
      const current = dragHandlersRef.current;
      if (event.currentTarget?.contains?.(event.relatedTarget)) return;
      if (current.dragOverContentKey === entry.key) {
        current.setDragOverContentKey(null);
        current.setDragOverContentMode('before');
        current.setDragOverContentEdge('top');
      }
    },
    onDrop(event, entry) {
      event.preventDefault();
      event.stopPropagation();
      const sourceKey = event.dataTransfer.getData('text/plain');
      const current = dragHandlersRef.current;
      const intent = current.contentDropIntentFromEvent(event, entry);
      current.handleDropOnContentItem(entry.key, sourceKey, intent.mode);
    },
    onEnd() {
      const current = dragHandlersRef.current;
      current.setDraggedContentKey(null);
      current.setDragOverContentKey(null);
      current.setDragOverContentMode('before');
      current.setDragOverContentEdge('top');
      current.setExtractDropActive(false);
      setTimeout(() => { current.dragStartedRef.current = false; }, 0);
    },
  }), []);

  const handleDropCourseOneLevelUp = async (event) => {
    event.preventDefault();
    setExtractDropActive(false);
    const sourceKey = event.dataTransfer.getData("text/plain") || draggedContentKey;
    setDraggedContentKey(null);
    setDragOverContentKey(null);
    setDragOverContentMode('before');
    setDragOverContentEdge('top');
    if (!sourceKey) return;
    const source = orderedAll.find((x) => x.key === sourceKey);
    if (!source || source.kind !== 'course') return;
    await moveCourseIntoParent(sourceKey, course?.parentCourseId || null);
  };

  const getDraggedCourseItem = () => orderedAll.find((x) => x.key === draggedContentKey && x.kind === 'course');

  const handleExtractZoneDragOver = (event) => {
    const source = getDraggedCourseItem();
    if (!source || !canReorderContentItem(source) || sortMode !== "default") return;
    event.preventDefault();
    event.dataTransfer.dropEffect = "move";
    setExtractDropActive(true);
  };

  const handleExtractZoneDragLeave = (event) => {
    if (event.currentTarget.contains(event.relatedTarget)) return;
    setExtractDropActive(false);
  };

  const ensureCanManageAssignments = (actionText = "изменять задания") => {
    if (items.length > 0 && items[0].canEdit === false) {
      notifyOnce("no-edit-course", () =>
        notify.warn(`Вы не владелец курса — ${actionText} нельзя`)
      );
      return false;
    }
    if (!courseCanEdit) {
      notify.warn(`Вы не владелец курса — ${actionText} нельзя`);
      return false;
    }
    return true;
  };

  const handleCreateType = async (type) => {
    if (!ensureCanManageAssignments("создавать задания")) return;
    setCreateBusyType(type);
    try {
      const payload = buildDefaultAssignmentPayload(type, orderedAll.length);
      const res = await createAssignment(courseId, payload);
      const id = res && res.id;
      setCreateDialogOpen(false);
      setCreateMode("choice");
      notify.success("Задание создано");
      if (id) nav(`/assignment/${id}/edit`);
    } catch (e) {
      if (e?.response?.status === 403) {
        notifyOnce("no-edit-course", () =>
          notify.error(getApiErrorMessage(e, "Создание запрещено"))
        );
        return;
      }
      handleApiError(e, notify, "Не удалось создать задание");
    } finally {
      setCreateBusyType("");
    }
  };

  const handleCreateChildCourse = async () => {
    if (!ensureCanManageAssignments("создавать вложенный курс")) return;
    setCreateBusyType("course");
    try {
      const res = await createCourse({
        title: "Новый вложенный курс",
        description: "Описание курса",
        isPublic: false,
        visibleGroupIds: [],
        ownerIds: [],
        parentCourseId: courseId,
        sort: orderedAll.length,
      });
      const id = res && res.id;
      setCreateDialogOpen(false);
      setCreateMode("choice");
      notify.success("Вложенный курс создан");
      if (id) navigateToCourseEditor(nav, location, courseId, id);
    } catch (e) {
      handleApiError(e, notify, "Не удалось создать вложенный курс");
    } finally {
      setCreateBusyType("");
    }
  };

  const handleJsonImportTextChange = (value) => {
    setJsonImportText(value);
    if (!String(value || "").trim()) {
      setJsonImportPreview("пусто");
      return;
    }
    try {
      const parsed = JSON.parse(value);
      setJsonImportPreview(summarizeImportPayload(parsed));
    } catch {
      setJsonImportPreview("JSON не читается");
    }
  };

  const exampleToText = (example) => JSON.stringify(example.payload, null, 2);

  const handleUseJsonExample = (example) => {
    handleJsonImportTextChange(exampleToText(example));
    notify.info(`В редактор вставлен пример: ${example.title}`);
  };

  const handleCopyJsonExample = async (example) => {
    try {
      await navigator.clipboard.writeText(exampleToText(example));
      notify.success(`Скопирован пример: ${example.title}`);
    } catch {
      notify.warn("Браузер не дал скопировать автоматически");
    }
  };

  const handleJsonFile = async (file) => {
    if (!file) return;
    try {
      const text = await file.text();
      handleJsonImportTextChange(text);
      notify.info(`JSON загружен: ${file.name}`);
    } catch (e) {
      notify.error("Не удалось прочитать JSON-файл");
    }
  };

  const applyJsonImport = async (parsed) => {
    setJsonImportBusy(true);
    try {
      const res = await importAssignmentsFromJson(courseId, parsed);
      const changed = Array.isArray(res?.assignments) ? res.assignments : [];
      await reloadAssignments(true);
      setJsonImportDiffOpen(false);
      setJsonImportDiff(null);
      setJsonImportParsed(null);
      setCreateDialogOpen(false);
      setCreateMode("choice");
      const created = res?.createdCount ?? 0;
      const updated = res?.updatedCount ?? 0;
      notify.success(`Импорт завершён: создано ${created}, обновлено ${updated}`);
      if ((created + updated) === 1 && changed[0]?.id) {
        const returnTo = showFlowLayout ? `/course/${courseId}` : '';
        nav(`/assignment/${changed[0].id}/edit${returnTo ? `?returnTo=${encodeURIComponent(returnTo)}` : ''}`);
      }
    } catch (e) {
      handleApiError(e, notify, "Не удалось импортировать JSON");
    } finally {
      setJsonImportBusy(false);
    }
  };

  const handlePrepareJsonImportDiff = async () => {
    if (!ensureCanManageAssignments("импортировать JSON")) return;
    if (!String(jsonImportText || "").trim()) {
      notify.warn("Вставьте JSON для импорта");
      return;
    }

    let parsed;
    try {
      parsed = JSON.parse(jsonImportText);
    } catch (e) {
      notify.error(`JSON не читается: ${e.message}`);
      return;
    }

    setJsonImportBusy(true);
    try {
      const currentExport = await exportAssignmentsToJson(courseId);
      const diff = buildJsonImportDiff(parsed, currentExport);
      setJsonImportParsed(parsed);
      setJsonImportDiff(diff);
      setJsonImportDiffOpen(true);
      if (diff.total === 0) notify.warn("В JSON не найдено заданий для импорта");
      if (diff.validationErrorCount > 0) notify.warn(`В JSON есть ошибки: ${diff.validationErrorCount}`);
    } catch (e) {
      handleApiError(e, notify, "Не удалось подготовить дифф импорта");
    } finally {
      setJsonImportBusy(false);
    }
  };

  const handleApplyPreparedJsonImport = async () => {
    if (!jsonImportParsed) {
      notify.error("Сначала подготовьте дифф импорта");
      return;
    }
    if (jsonImportDiff?.validationErrorCount > 0) {
      notify.error("Сначала исправьте ошибки JSON");
      return;
    }
    await applyJsonImport(jsonImportParsed);
  };

  const handleExportJson = async () => {
    if (!ensureCanManageAssignments("экспортировать JSON")) return;
    setJsonExportBusy(true);
    try {
      const data = await exportAssignmentsToJson(courseId);
      const text = JSON.stringify(data, null, 2);
      const safeTitle = (course?.title || "course")
        .toLowerCase()
        .replace(/[^a-zа-яё0-9]+/gi, "-")
        .replace(/^-+|-+$/g, "") || "course";
      const includesNestedCourses = Number(data?.courseCount || 0) > 1;
      const blob = new Blob([text], { type: "application/json;charset=utf-8" });
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = `taskforge-${safeTitle}-${includesNestedCourses ? "course-tree" : "assignments"}.json`;
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
      if (!includesNestedCourses) handleJsonImportTextChange(text);
      notify.success(includesNestedCourses
        ? `JSON скачан: ${data.courseCount} курсов и ${data.assignmentCount || 0} заданий`
        : "JSON экспортирован и загружен в редактор импорта");
    } catch (e) {
      handleApiError(e, notify, "Не удалось экспортировать JSON");
    } finally {
      setJsonExportBusy(false);
    }
  };

  return (
    <>
      {!showFlowLayout ? (
      <div className="page-hero-card mb-6 rounded-[28px] p-5 sm:p-6">
      <div className="flex flex-col gap-5 xl:flex-row xl:items-start xl:justify-between">
        <div className="min-w-0 flex-1">
          <div className="flex min-w-0 flex-col gap-3 lg:flex-row lg:items-center">
            <div className="flex min-w-0 items-center gap-2 sm:gap-3">
              <Button
                variant="outline"
                title={course?.parentCourseId ? "Вернуться на уровень выше" : "Вернуться к курсам"}
                className="shrink-0"
                onClick={() => nav(course?.parentCourseId ? `/course/${course.parentCourseId}` : "/courses")}
                onDragOver={(e) => {
                  if (!draggedContentKey) return;
                  const source = orderedAll.find((x) => x.key === draggedContentKey);
                  if (source?.kind !== 'course') return;
                  e.preventDefault();
                }}
                onDrop={handleDropCourseOneLevelUp}
              >
                {course?.parentCourseId ? "← Назад" : "← Курсы"}
              </Button>
              <h1 className="min-w-0 text-xl font-semibold leading-tight sm:text-2xl flex items-center gap-2 flex-wrap">
                <Layers size={22} className="shrink-0" /> <span className="break-words">{course?.title || "Задания курса"}</span>
              </h1>
            </div>
            <div className="w-full max-w-[420px] rounded-2xl border border-[rgba(var(--border)/0.65)] bg-[rgba(var(--muted)/0.18)] px-4 py-3 lg:ml-2 lg:max-w-[360px]">
              <div className="mb-2 text-xs font-medium text-neutral-500">{courseProgress.solved}/{courseProgress.total}</div>
              <div className="h-2 overflow-hidden rounded-full bg-neutral-200/70 dark:bg-white/10">
                <div
                  className="h-full rounded-full bg-[rgb(var(--accent))] transition-all duration-500"
                  style={{ width: `${courseProgress.total > 0 ? courseProgress.percent : 0}%` }}
                />
              </div>
            </div>
          </div>
          {course?.description ? (
            <p className="mt-4 max-w-3xl text-sm leading-6 text-neutral-500">{course.description}</p>
          ) : null}
        </div>

        <div className="grid grid-cols-1 gap-2 sm:grid-cols-2 xl:flex xl:flex-wrap xl:items-center xl:justify-end xl:gap-3">
          <IfEditor>
            {courseCanEdit ? (
              <CourseLayoutToggle
                value={contentLayout}
                onChange={(layout) => {
                  setContentLayout(layout);
                  if (layout === 'flow' && sortMode !== 'default') setSortMode('default');
                }}
              />
            ) : null}
          </IfEditor>

          {!showFlowLayout ? (
            <div className="min-w-0 xl:min-w-[190px]">
              <select value={sortMode} onChange={(e) => setSortMode(e.target.value)} className="input w-full" title="Сортировка">
                {SORT_OPTIONS.map((o) => (
                  <option key={o.v} value={o.v}>
                    {o.label}
                  </option>
                ))}
              </select>
            </div>
          ) : null}

          <IfEditor>
            {courseCanEdit ? (
              <>
                <Button variant="outline" className="w-full sm:w-auto" onClick={handleExportJson} disabled={jsonExportBusy}>
                  <Download size={16} /> {jsonExportBusy ? "Экспортирую…" : "Экспорт JSON"}
                </Button>
                <Button className="w-full sm:w-auto" onClick={(event) => showFlowLayout ? openCreateDialog("json") : openCreateMenu(event)}>
                  {showFlowLayout ? <FileJson size={16} /> : <Plus size={16} />} {showFlowLayout ? 'Импорт JSON' : 'Создать'}
                </Button>
              </>
            ) : null}
          </IfEditor>
        </div>
      </div>
      </div>
      ) : null}


      <IfEditor>
        {!showFlowLayout && courseCanEdit && childCourses.length > 0 && getDraggedCourseItem() ? (
          <div
            className={
              "fixed bottom-5 left-1/2 z-[1000] w-[calc(100%-2rem)] max-w-xl -translate-x-1/2 rounded-2xl border border-dashed px-5 py-4 text-sm shadow-2xl backdrop-blur transition sm:bottom-7 " +
              (extractDropActive
                ? "border-[rgb(var(--accent))] bg-[rgba(var(--accent)/0.16)] text-[rgb(var(--accent))]"
                : "border-[rgba(var(--border)/0.9)] bg-[rgba(var(--card)/0.96)] text-neutral-500")
            }
            onDragOver={handleExtractZoneDragOver}
            onDragEnter={handleExtractZoneDragOver}
            onDragLeave={handleExtractZoneDragLeave}
            onDrop={handleDropCourseOneLevelUp}
          >
            <div className="font-medium text-current">
              Вынести курс на уровень выше
            </div>
            <div className="mt-1 text-xs opacity-80">
              Отпусти здесь курс: он переместится {course?.parentCourseId ? "в родительский курс" : "в корень каталога"}.
            </div>
          </div>
        ) : null}
      </IfEditor>

      {jsonImportDiffOpen && jsonImportDiff && (
        <div
          className="fixed inset-0 z-[9999] flex h-[100dvh] items-center justify-center overflow-hidden bg-black/65 p-3 sm:p-5"
          onMouseDown={(e) => { if (e.target === e.currentTarget && !jsonImportBusy) setJsonImportDiffOpen(false); }}
        >
          <Card className="flex max-h-[calc(100dvh-1.5rem)] w-full max-w-6xl flex-col overflow-hidden rounded-[28px] border border-[rgba(var(--border)/0.8)] bg-[rgb(var(--card))] p-0 shadow-2xl sm:max-h-[calc(100dvh-2.5rem)]">
            <div className="shrink-0 border-b border-[rgba(var(--border)/0.65)] bg-[rgb(var(--card))] px-4 py-4 sm:px-6">
              <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
                <div>
                  <div className="flex items-center gap-2 text-xl font-semibold">
                    <GitCompare size={20} /> Дифф JSON-импорта
                  </div>
                </div>
                <div className="flex flex-wrap gap-2">
                  <Button variant="outline" onClick={() => setJsonImportDiffOpen(false)} disabled={jsonImportBusy}>
                    <X size={16} /> Назад
                  </Button>
                  <Button onClick={handleApplyPreparedJsonImport} disabled={jsonImportBusy || jsonImportDiff.total === 0 || jsonImportDiff.validationErrorCount > 0}>
                    <FileJson size={16} /> {jsonImportBusy ? "Импортирую…" : "Применить изменения"}
                  </Button>
                </div>
              </div>
            </div>

            <div className="flex-1 overflow-y-auto px-4 py-4 sm:px-6">
              <div className="grid gap-3 sm:grid-cols-5">
                <div className="rounded-2xl border border-[rgba(var(--border)/0.65)] p-3">
                  <div className="text-xs text-neutral-500">Всего</div>
                  <div className="mt-1 text-2xl font-semibold">{jsonImportDiff.total}</div>
                </div>
                <div className="rounded-2xl border border-[rgba(var(--border)/0.65)] p-3">
                  <div className="text-xs text-neutral-500">Создать</div>
                  <div className="mt-1 text-2xl font-semibold">{jsonImportDiff.createCount}</div>
                </div>
                <div className="rounded-2xl border border-[rgba(var(--border)/0.65)] p-3">
                  <div className="text-xs text-neutral-500">Обновить</div>
                  <div className="mt-1 text-2xl font-semibold">{jsonImportDiff.updateCount}</div>
                </div>
                <div className="rounded-2xl border border-[rgba(var(--border)/0.65)] p-3">
                  <div className="text-xs text-neutral-500">Без изменений</div>
                  <div className="mt-1 text-2xl font-semibold">{jsonImportDiff.unchangedCount}</div>
                </div>
                <div className={`rounded-2xl border p-3 ${jsonImportDiff.validationErrorCount > 0 ? "border-red-400/70 bg-red-500/10" : "border-[rgba(var(--border)/0.65)]"}`}>
                  <div className="text-xs text-neutral-500">Ошибки</div>
                  <div className="mt-1 text-2xl font-semibold">{jsonImportDiff.validationErrorCount}</div>
                </div>
              </div>

              {jsonImportDiff.validationErrorCount > 0 && (
                <div className="mt-4 rounded-2xl border border-red-400/70 bg-red-500/10 px-4 py-3 text-sm leading-6 text-red-900 dark:text-red-100">
                  <div className="flex items-start gap-2">
                    <AlertTriangle size={18} className="mt-0.5 shrink-0" />
                    <div>Исправь ошибки ниже. Импорт не будет применён, пока JSON не совпадает со схемой проекта.</div>
                  </div>
                </div>
              )}

              {jsonImportDiff.duplicateTitleCount > 0 && (
                <div className="mt-4 rounded-2xl border border-amber-300/70 bg-amber-50 px-4 py-3 text-sm leading-6 text-amber-900 dark:border-amber-700/60 dark:bg-amber-950/30 dark:text-amber-100">
                  <div className="flex items-start gap-2">
                    <AlertTriangle size={18} className="mt-0.5 shrink-0" />
                    <div>Возможные дубли по названию: {jsonImportDiff.duplicateTitleCount}</div>
                  </div>
                </div>
              )}

              <div className="mt-4 space-y-3 pb-2">
                {jsonImportDiff.rows.map((row) => (
                  <div key={`${row.index}-${row.id || row.title}`} className="rounded-2xl border border-[rgba(var(--border)/0.7)] bg-[rgb(var(--muted))]/20 p-3">
                    <div className="flex flex-col gap-2 sm:flex-row sm:items-start sm:justify-between">
                      <div className="min-w-0">
                        <div className="flex flex-wrap items-center gap-2">
                          <Badge variant={row.action === "create" ? "success" : row.action === "update" ? "outline" : "secondary"}>
                            {row.action === "create" ? "Создать" : row.action === "update" ? "Обновить" : "Без изменений"}
                          </Badge>
                          <Badge variant="outline">{row.type}</Badge>
                          {row.duplicateTitle ? <Badge intent="danger">возможный дубль</Badge> : null}
                        </div>
                        <div className="mt-2 break-words font-semibold">{row.title}</div>
                        <div className="mt-1 break-all text-xs text-neutral-500">{row.id ? `id: ${row.id}` : "Будет создано как новое задание"}</div>
                      </div>
                      <div className="text-xs text-neutral-500">#{row.index + 1}</div>
                    </div>

                    {row.issues.length > 0 ? (
                      <div className="mt-3 rounded-xl border border-red-400/60 bg-red-500/10 px-3 py-2 text-sm text-red-900 dark:text-red-100">
                        <div className="font-semibold">Ошибки JSON</div>
                        <ul className="mt-1 list-disc space-y-1 pl-5">
                          {row.issues.map((issue) => <li key={issue}>{issue}</li>)}
                        </ul>
                      </div>
                    ) : row.action === "create" ? (
                      <div className="mt-3 rounded-xl border border-dashed border-[rgba(var(--border)/0.75)] px-3 py-2 text-sm text-neutral-500">
                        Будет создано.
                      </div>
                    ) : row.changes.length ? (
                      <div className="mt-3 space-y-2">
                        {row.changes.map((change) => (
                          <div key={change.key} className="rounded-xl border border-[rgba(var(--border)/0.65)] p-3">
                            <div className="mb-2 text-sm font-semibold">{change.label}</div>
                            <div className="grid gap-2 lg:grid-cols-2">
                              <div>
                                <div className="mb-1 text-xs uppercase tracking-wide text-neutral-500">Было</div>
                                <pre className="max-h-44 overflow-auto whitespace-pre-wrap rounded-lg bg-[rgb(var(--card))] p-2 text-xs leading-5">{shortImportValue(change.before)}</pre>
                              </div>
                              <div>
                                <div className="mb-1 text-xs uppercase tracking-wide text-neutral-500">Станет</div>
                                <pre className="max-h-44 overflow-auto whitespace-pre-wrap rounded-lg bg-[rgb(var(--card))] p-2 text-xs leading-5">{shortImportValue(change.after)}</pre>
                              </div>
                            </div>
                          </div>
                        ))}
                      </div>
                    ) : (
                      <div className="mt-3 rounded-xl border border-dashed border-[rgba(var(--border)/0.75)] px-3 py-2 text-sm text-neutral-500">
                        Изменений нет.
                      </div>
                    )}
                  </div>
                ))}
              </div>
            </div>
          </Card>
        </div>
      )}

      {createDialogOpen && (
        <div
          className="tf-modal-backdrop fixed inset-0 z-50 flex items-start justify-center overflow-y-auto bg-black/55 px-3 py-6 sm:px-6"
          onMouseDown={(e) => {
            if (e.target === e.currentTarget && !jsonImportBusy && !createBusyType) setCreateDialogOpen(false);
          }}
        >
          <Card className="tf-modal-panel w-full max-w-5xl rounded-[28px] border border-[rgba(var(--border)/0.8)] bg-[rgb(var(--card))] p-4 shadow-2xl sm:p-6">
            <div className="flex flex-col gap-3 border-b border-[rgba(var(--border)/0.65)] pb-4 sm:flex-row sm:items-center sm:justify-between">
              <div className="flex items-center gap-3">
                <div className="flex items-center gap-2 text-xl font-semibold">
                  <Sparkles size={20} />
                  JSON-импорт
                </div>
              </div>
              <Button variant="outline" onClick={() => setCreateDialogOpen(false)} disabled={jsonImportBusy || !!createBusyType} title="Закрыть">
                <X size={16} /> Закрыть
              </Button>
            </div>

            {createMode === "json" ? (
              <div className="mt-5">
                <div className="flex flex-col gap-3 rounded-2xl border border-[rgba(var(--border)/0.7)] bg-[rgb(var(--muted))]/20 p-3 lg:flex-row lg:items-center lg:justify-between">
                  <div className="flex flex-wrap items-center gap-2">
                    <FileJson size={18} />
                    <span className="font-semibold">JSON</span>
                    <Badge variant="outline">{jsonImportPreview}</Badge>
                  </div>
                  <div className="flex flex-wrap gap-2">
                    <Button variant="outline" onClick={handleExportJson} disabled={jsonExportBusy || jsonImportBusy}>
                      <Download size={16} /> {jsonExportBusy ? "Экспорт…" : "Экспорт"}
                    </Button>
                    <label className="btn-outline cursor-pointer">
                      <Upload size={16} /> Файл
                      <input
                        type="file"
                        accept="application/json,.json"
                        className="hidden"
                        onChange={(e) => handleJsonFile(e.target.files?.[0])}
                      />
                    </label>
                    <Button
                      variant="outline"
                      onClick={async () => {
                        try {
                          await navigator.clipboard.writeText(jsonImportText);
                          notify.success("JSON скопирован");
                        } catch {
                          notify.warn("Браузер не дал скопировать автоматически");
                        }
                      }}
                    >
                      <Copy size={16} /> Копировать
                    </Button>
                    <Button
                      variant="outline"
                      onClick={() => {
                        try {
                          handleJsonImportTextChange(JSON.stringify(JSON.parse(jsonImportText), null, 2));
                        } catch (e) {
                          notify.error(`Нельзя форматировать: ${e.message}`);
                        }
                      }}
                    >
                      Форматировать
                    </Button>
                    <Button variant="outline" onClick={() => setJsonDocsOpen((v) => !v)}>
                      <FileJson size={16} /> Справка
                    </Button>
                  </div>
                </div>

                <Textarea
                  rows={28}
                  value={jsonImportText}
                  onChange={(e) => handleJsonImportTextChange(e.target.value)}
                  spellCheck={false}
                  placeholder="Вставь JSON сюда"
                  className="mt-4 min-h-[560px] font-mono text-xs leading-5"
                />

                {jsonDocsOpen ? (
                  <div className="mt-4 rounded-2xl border border-[rgba(var(--border)/0.75)] bg-[rgb(var(--muted))]/20 p-4">
                    <div className="grid gap-4 lg:grid-cols-[0.95fr_1.25fr]">
                      <div>
                        <div className="mb-3 flex items-center gap-2 font-semibold">
                          <FileJson size={16} /> Поля
                        </div>
                        <div className="flex flex-wrap gap-2">
                          {JSON_IMPORT_DOC_FIELDS.map((field) => (
                            <code key={field} className="rounded-lg border border-[rgba(var(--border)/0.65)] bg-[rgb(var(--card))]/70 px-2 py-1 text-xs">
                              {field}
                            </code>
                          ))}
                        </div>
                      </div>
                      <div>
                        <div className="mb-3 flex items-center gap-2 font-semibold">
                          <FileJson size={16} /> Примеры
                        </div>
                        <div className="grid gap-2 sm:grid-cols-2">
                          {JSON_IMPORT_EXAMPLES.map((example) => (
                            <div key={example.key} className="rounded-2xl border border-[rgba(var(--border)/0.7)] bg-[rgb(var(--card))]/60 p-3">
                              <div className="flex items-center justify-between gap-2">
                                <div className="font-semibold">{example.title}</div>
                                <Badge variant="outline">{example.type}</Badge>
                              </div>
                              <div className="mt-3 flex flex-wrap gap-2">
                                <Button variant="outline" onClick={() => handleCopyJsonExample(example)} disabled={jsonImportBusy || !!createBusyType}>
                                  <Copy size={14} /> Копировать
                                </Button>
                                <Button variant="outline" onClick={() => handleUseJsonExample(example)} disabled={jsonImportBusy || !!createBusyType}>
                                  В поле
                                </Button>
                              </div>
                            </div>
                          ))}
                        </div>
                      </div>
                    </div>
                  </div>
                ) : null}

                <div className="mt-4 flex flex-col gap-3 xl:flex-row xl:items-center xl:justify-between">
                  <div className="text-xs text-neutral-500">
                    Импорт создаёт новые задания или обновляет существующие по <code>id</code>.
                  </div>
                  <Button onClick={handlePrepareJsonImportDiff} disabled={jsonImportBusy || !!createBusyType}>
                    <GitCompare size={16} /> {jsonImportBusy ? "Дифф…" : "Показать дифф"}
                  </Button>
                </div>
              </div>
            ) : null}
          </Card>
        </div>
      )}

      {!showFlowLayout ? (
      <Card className="page-search-card mb-6 rounded-[24px] p-3 sm:p-4">
        <div className="flex flex-col gap-3 lg:flex-row lg:items-center">
          <div className="relative flex-1">
            <Input
              placeholder="Поиск по названию или тегам"
              value={q}
              onChange={(e) => setQ(e.target.value)}
            />
          </div>        </div>
      </Card>
      ) : null}

      {err && <div className="text-red-500 mb-4">{err}</div>}
      {loading && !showFlowLayout ? <div className="text-neutral-500">Загрузка…</div> : null}

      {showFlowLayout ? (
        <CourseFlowEditor
          course={course}
          allCourses={allCourses}
          courseCanEdit={courseCanEdit}
          editorMode={Boolean(isEditorMode && courseCanEdit)}
          query={q}
          onQueryChange={setQ}
          onShowGrid={isEditorMode && courseCanEdit ? () => setContentLayout('grid') : null}
          onExportJson={isEditorMode && courseCanEdit ? handleExportJson : null}
          onImportJson={isEditorMode && courseCanEdit ? () => openCreateDialog('json') : null}
          exportBusy={jsonExportBusy}
          focusCourseId={params.get('focusCourse') || ''}
          dataRevision={assignmentsQuery.updatedAt || 0}
          onRefreshCourseData={async () => {
            await Promise.all([reloadCourseData(), reloadAssignments()]);
          }}
        />
      ) : (
        <CourseContentGrid
          entries={filtered}
          positionByKey={positionByKey}
          canReorderContentItem={canReorderContentItem}
          courseCanEdit={courseCanEdit}
          sortMode={sortMode}
          draggedContentKey={draggedContentKey}
          dragOverContentKey={dragOverContentKey}
          dragOverContentMode={dragOverContentMode}
          dragOverContentEdge={dragOverContentEdge}
          setDragOverContentKey={setDragOverContentKey}
          setDragOverContentMode={setDragOverContentMode}
          setDragOverContentEdge={setDragOverContentEdge}
          childProgressByCourseId={childProgressByCourseId}
          dragApi={dragApi}
          onContextMenu={openContextMenu}
          onDropContent={handleDropOnContentItem}
        />
      )}

      {!loading && !showFlowLayout && filtered.length === 0 && (
        <div className="card-muted p-8 text-center text-neutral-500 mt-6" onContextMenu={(event) => openContextMenu(event)}>
          Пока заданий нет. Создайте первое ✨
        </div>
      )}

      <ContextMenu
        open={createMenu.open}
        x={createMenu.x}
        y={createMenu.y}
        onClose={closeCreateMenu}
        ariaLabel="Создать в курсе"
      >
        <ContextMenuLabel>Создать</ContextMenuLabel>
        <ContextMenuItem
          icon={FolderPlus}
          disabled={Boolean(createBusyType || jsonImportBusy)}
          onClick={() => { closeCreateMenu(); void handleCreateChildCourse(); }}
        >
          Вложенный курс
        </ContextMenuItem>
        <ContextMenuSeparator />
        <ContextMenuLabel>Задание</ContextMenuLabel>
        {CREATE_OPTIONS.map((option) => (
          <ContextMenuItem
            key={option.type}
            icon={FilePlus2}
            disabled={Boolean(createBusyType || jsonImportBusy)}
            onClick={() => { closeCreateMenu(); void handleCreateType(option.type); }}
          >
            {option.title}
          </ContextMenuItem>
        ))}
        <ContextMenuSeparator />
        <ContextMenuItem icon={FileJson} onClick={() => openCreateDialog('json')}>Импорт из JSON</ContextMenuItem>
      </ContextMenu>

      <ContextMenu
        open={contextMenu.open}
        x={contextMenu.x}
        y={contextMenu.y}
        onClose={closeContextMenu}
        ariaLabel="Быстрые действия курса"
      >
        {contextMenu.entry ? (
          <>
            <ContextMenuLabel>{contextMenu.entry.kind === 'course' ? 'Курс' : 'Задание'}</ContextMenuLabel>
            <ContextMenuItem
              icon={ExternalLink}
              onClick={() => {
                const entry = contextMenu.entry;
                closeContextMenu();
                nav(entry.kind === 'course' ? `/course/${entry.id}` : `/assignment/${entry.id}`);
              }}
            >
              Открыть
            </ContextMenuItem>
            {contextMenu.canEdit ? (
              <ContextMenuItem
                icon={Pencil}
                onClick={() => {
                  const entry = contextMenu.entry;
                  closeContextMenu();
                  if (entry.kind === 'course') navigateToCourseEditor(nav, location, courseId, entry.id);
                  else nav(`/assignment/${entry.id}/edit`);
                }}
              >
                Редактировать
              </ContextMenuItem>
            ) : null}
            <ContextMenuSeparator />
          </>
        ) : null}

        <ContextMenuLabel>Создать</ContextMenuLabel>
        <ContextMenuItem
          icon={FolderPlus}
          disabled={Boolean(createBusyType || jsonImportBusy)}
          onClick={() => {
            closeContextMenu();
            void handleCreateChildCourse();
          }}
        >
          Вложенный курс
        </ContextMenuItem>
        <ContextMenuSeparator />
        <ContextMenuLabel>Задание</ContextMenuLabel>
        {CREATE_OPTIONS.map((option) => (
          <ContextMenuItem
            key={option.type}
            icon={FilePlus2}
            disabled={Boolean(createBusyType || jsonImportBusy)}
            onClick={() => { closeContextMenu(); void handleCreateType(option.type); }}
          >
            {option.title}
          </ContextMenuItem>
        ))}
        <ContextMenuSeparator />
        <ContextMenuItem icon={FileJson} onClick={() => openCreateDialog('json')}>
          Импорт из JSON
        </ContextMenuItem>
      </ContextMenu>
    </>
  );
}
