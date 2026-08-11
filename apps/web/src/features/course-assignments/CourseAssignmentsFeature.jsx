import React, { useEffect, useMemo, useRef, useState } from "react";
import { useLocation, useNavigate, useParams, useSearchParams } from "react-router-dom";

import { Card, Button, Input } from "../../components/ui";

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
import { Plus, Layers, FileJson, Download, ExternalLink, Pencil, FilePlus2, FolderPlus } from "lucide-react";
import IfEditor from "../../components/IfEditor";
import { useNotify } from "../../components/notify/NotifyProvider";
import { handleApiError } from "../../utils/handleApiError";
import { notifyOnce } from "../../utils/notifyOnce";
import { resolveCardDropIntent, resolveFlowNodeDropIntent } from "../../utils/gridDragDrop";
import useQuery from '../../hooks/useQuery';
import { useQueryClient } from '../../data/QueryClientProvider';
import { useEditorMode } from '../../contexts/EditorModeContext';
import { ContextMenu, ContextMenuItem, ContextMenuLabel, ContextMenuSeparator, claimContextMenuEvent } from '../../components/ui/ContextMenu';

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
  buildDefaultAssignmentPayload,
} from './courseAssignmentsModel';
import CourseContentGrid from './components/CourseContentGrid';
import CourseFlowEditor from './components/CourseFlowEditor';
import CourseLayoutToggle from './components/CourseLayoutToggle';
import JsonTaskGraphDialog from './components/JsonTaskGraphDialog';
import JsonTaskGraphDiffModal from './components/JsonTaskGraphDiffModal';
import {
  TASK_GRAPH_AI_PROMPT,
  buildTaskGraphImportDiff,
  summarizeTaskGraphPayload,
  taskGraphExampleToText,
} from './courseTaskGraphJson';
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
  const courseBundleKey = useMemo(() => ['course-bundle', courseId, isEditorMode ? 'editor' : 'learner'], [courseId, isEditorMode]);

  const assignmentsQuery = useQuery({
    queryKey: assignmentsKey,
    queryFn: async () => {
      const data = await getAssignmentsByCourse(courseId);
      return (data || []).map((item, index) => ({
        ...item,
        sort: typeof item.sort === 'number' ? item.sort : index,
      }));
    },
    enabled: Boolean(courseId) && Boolean(isEditorMode),
    staleTime: 20_000,
    keepPreviousData: true,
  });

  const courseBundleQuery = useQuery({
    queryKey: courseBundleKey,
    queryFn: async () => {
      const loadedCourse = await getCourse(courseId);
      if (!isEditorMode) {
        return {
          course: loadedCourse || null,
          allCourses: loadedCourse ? [loadedCourse] : [],
          childCourses: [],
          courseCanEdit: typeof loadedCourse?.canEdit === 'boolean' ? Boolean(loadedCourse.canEdit) : false,
        };
      }
      const coursesPayload = await getCourses().catch(() => []);
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

  const items = isEditorMode ? (assignmentsQuery.data || EMPTY_LIST) : EMPTY_LIST;
  const courseBundle = courseBundleQuery.data || EMPTY_COURSE_BUNDLE;
  const course = courseBundle.course || null;
  const childCourses = courseBundle.childCourses || EMPTY_LIST;
  const allCourses = courseBundle.allCourses || EMPTY_LIST;
  const courseCanEdit = courseBundle.courseCanEdit !== false;
  const loading = courseBundleQuery.isLoading || (isEditorMode && assignmentsQuery.isLoading);
  const err = isEditorMode && assignmentsQuery.error ? getApiErrorMessage(assignmentsQuery.error, 'Не удалось загрузить задания') : '';

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
  const [learnerFlowProgress, setLearnerFlowProgress] = useState(null);
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
  const [jsonDocsOpen, setJsonDocsOpen] = useState(false);
  const [createBusyType, setCreateBusyType] = useState('');
  const [jsonImportText, setJsonImportText] = useState('');
  const [jsonImportBusy, setJsonImportBusy] = useState(false);
  const [jsonExportBusy, setJsonExportBusy] = useState(false);
  const [jsonImportPreview, setJsonImportPreview] = useState('пусто');
  const [jsonImportDiffOpen, setJsonImportDiffOpen] = useState(false);
  const [jsonImportDiff, setJsonImportDiff] = useState(null);
  const [jsonImportParsed, setJsonImportParsed] = useState(null);
  const [graphImportRequest, setGraphImportRequest] = useState(null);
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
    const canCreate = isEditorMode && courseCanEdit;
    if (!entry && !canCreate) return;
    if (!claimContextMenuEvent(event)) return;
    setContextMenu({
      open: true,
      x: event.clientX,
      y: event.clientY,
      entry,
      canEdit: Boolean(canEditEntry),
    });
  }, [courseCanEdit, isEditorMode]);

  const openCreateDialog = React.useCallback(() => {
    closeContextMenu();
    closeCreateMenu();
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
        const loadedCourse = await getCourse(courseId);
        if (!isEditorMode) {
          return {
            course: loadedCourse || null,
            allCourses: loadedCourse ? [loadedCourse] : [],
            childCourses: [],
            courseCanEdit: typeof loadedCourse?.canEdit === 'boolean' ? Boolean(loadedCourse.canEdit) : false,
          };
        }
        const coursesPayload = await getCourses().catch(() => []);
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
  }, [courseBundleKey, courseId, isEditorMode, queryClient]);

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
    if (!isEditorMode || !progressContext || assignmentsQuery.isLoading || !progressContext.requestKey) {
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
  }, [assignmentsQuery.isLoading, childCourses, courseId, isEditorMode, items, progressContext, progressRevision]);


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
    if (!isEditorMode && learnerFlowProgress) return learnerFlowProgress;
    return courseProgressByCourseId[courseId] || directCourseProgress;
  }, [courseProgressByCourseId, courseId, directCourseProgress, isEditorMode, learnerFlowProgress]);

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
      setJsonImportPreview(summarizeTaskGraphPayload(parsed));
    } catch {
      setJsonImportPreview("JSON не читается");
    }
  };

  const handleUseJsonExample = (example) => {
    handleJsonImportTextChange(taskGraphExampleToText(example));
    notify.info(`В редактор вставлен пример: ${example.title}`);
  };

  const handleCopyJsonExample = async (example) => {
    try {
      await navigator.clipboard.writeText(taskGraphExampleToText(example));
      notify.success(`Скопирован пример: ${example.title}`);
    } catch {
      notify.warn("Браузер не дал скопировать автоматически");
    }
  };

  const handleCopyJsonAiPrompt = async () => {
    try {
      await navigator.clipboard.writeText(TASK_GRAPH_AI_PROMPT);
      notify.success("Промпт для генерации JSON скопирован");
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
      await reloadAssignments(true);
      setJsonImportDiffOpen(false);
      setJsonImportDiff(null);
      setJsonImportParsed(null);
      setCreateDialogOpen(false);
      const created = res?.createdCount ?? 0;
      const updated = res?.updatedCount ?? 0;
      const taskGraph = res?.taskGraph || res?.graph;
      const taskMappings = Array.isArray(res?.taskMappings) ? res.taskMappings : [];
      const hasGraph = Boolean(Array.isArray(taskGraph?.tasks) && Array.isArray(taskGraph?.connections));
      if (hasGraph) {
        const mappedIds = taskGraph.tasks
          .map((item) => item?.assignmentId)
          .filter(Boolean);
        setContentLayout('flow');
        setGraphImportRequest({
          key: `${Date.now()}:${[...mappedIds, ...taskMappings.map((item) => item?.assignmentId)].filter(Boolean).join(',')}`,
          taskGraph,
          taskMappings,
        });
        notify.success(`Импорт завершён: создано ${created}, обновлено ${updated}. Карта обновляется.`);
      } else {
        notify.success(`Импорт завершён: создано ${created}, обновлено ${updated}`);
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
      const diff = buildTaskGraphImportDiff(parsed, currentExport);
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
      const blob = new Blob([text], { type: "application/json;charset=utf-8" });
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = `taskforge-${safeTitle}-task-graph.json`;
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
      handleJsonImportTextChange(text);
      const count = Array.isArray(data?.tasks) ? data.tasks.length : 0;
      const links = Array.isArray(data?.connections) ? data.connections.length : 0;
      notify.success(`Граф экспортирован: ${count} заданий, ${links} связей`);
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
                <Button className="w-full sm:w-auto" onClick={(event) => showFlowLayout ? openCreateDialog() : openCreateMenu(event)}>
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

      <JsonTaskGraphDiffModal
        open={jsonImportDiffOpen}
        diff={jsonImportDiff}
        busy={jsonImportBusy}
        onClose={() => setJsonImportDiffOpen(false)}
        onApply={handleApplyPreparedJsonImport}
      />

      <JsonTaskGraphDialog
        open={createDialogOpen}
        busy={jsonImportBusy || Boolean(createBusyType)}
        exportBusy={jsonExportBusy}
        preview={jsonImportPreview}
        text={jsonImportText}
        docsOpen={jsonDocsOpen}
        onClose={() => setCreateDialogOpen(false)}
        onExport={handleExportJson}
        onFile={handleJsonFile}
        onCopy={async () => {
          try {
            await navigator.clipboard.writeText(jsonImportText);
            notify.success('JSON скопирован');
          } catch {
            notify.warn('Браузер не дал скопировать автоматически');
          }
        }}
        onFormat={() => {
          try {
            handleJsonImportTextChange(JSON.stringify(JSON.parse(jsonImportText), null, 2));
          } catch (error) {
            notify.error(`Нельзя форматировать: ${error.message}`);
          }
        }}
        onToggleDocs={() => setJsonDocsOpen((value) => !value)}
        onTextChange={handleJsonImportTextChange}
        onCopyAiPrompt={handleCopyJsonAiPrompt}
        onCopyExample={handleCopyJsonExample}
        onUseExample={handleUseJsonExample}
        onPrepareDiff={handlePrepareJsonImportDiff}
      />

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
          onImportJson={isEditorMode && courseCanEdit ? openCreateDialog : null}
          exportBusy={jsonExportBusy}
          focusCourseId={params.get('focusCourse') || ''}
          dataRevision={assignmentsQuery.updatedAt || 0}
          onLearnerProgress={isEditorMode ? null : setLearnerFlowProgress}
          graphImportRequest={graphImportRequest}
          onGraphImportComplete={(key) => setGraphImportRequest((current) => current?.key === key ? null : current)}
          onRefreshCourseData={async () => {
            if (isEditorMode) {
              await Promise.all([reloadCourseData(), reloadAssignments()]);
              return;
            }
            await reloadCourseData();
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
        <ContextMenuItem icon={FileJson} onClick={openCreateDialog}>Импорт из JSON</ContextMenuItem>
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

        {isEditorMode && courseCanEdit ? (
          <>
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
            <ContextMenuItem icon={FileJson} onClick={openCreateDialog}>Импорт из JSON</ContextMenuItem>
          </>
        ) : null}
      </ContextMenu>
    </>
  );
}
