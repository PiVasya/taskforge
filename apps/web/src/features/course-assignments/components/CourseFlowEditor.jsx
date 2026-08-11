import React from 'react';
import ReactFlow, {
  ReactFlowProvider,
  Background,
  Controls,
  MarkerType,
  SelectionMode,
  addEdge,
  applyEdgeChanges,
  applyNodeChanges,
  useEdgesState,
  useNodesState,
  useReactFlow,
} from 'reactflow';
import 'reactflow/dist/style.css';
import '../course-map.css';
import { ArrowLeft, Download, Eye, FileCode2, FileJson, FolderTree, Image as ImageIcon, LayoutGrid, ListOrdered, LockKeyhole, Pencil, Plus, Save, Search, Sigma, Trash2, X, ListChecks, RotateCcw, Unlink2 } from 'lucide-react';
import { useLocation, useNavigate } from 'react-router-dom';

import { ContextMenu, ContextMenuItem, ContextMenuLabel, ContextMenuSeparator } from '../../../components/ui/ContextMenu';
import { Button } from '../../../components/ui';
import { useNotify } from '../../../components/notify/NotifyProvider';
import { useAuth } from '../../../auth/AuthContext';
import { createAssignment, deleteAssignment, getAssignmentsByCourseTree } from '../../../api/assignments';
import { createCourse, deleteCourse } from '../../../api/courses';
import { getCourseMap, getLearningCourseMapDelta, saveCourseMap, streamLearningCourseMap } from '../../../api/courseMaps';
import { createCourseMapPresenceConnection, disposeCourseMapPresenceConnection } from '../../../realtime/courseMapHub';
import { getApiErrorMessage } from '../../../api/http';
import { buildDefaultAssignmentPayload, previewAssignmentDescription } from '../courseAssignmentsModel';
import {
  assignmentNodeId,
  assignmentNodeType,
  buildDefaultCourseMap,
  buildEntityIndex,
  computeCourseMapAccessEffects,
  computeCourseProgress,
  courseNodeId,
  readCourseProgress,
  writeCourseProgress,
  findUnplacedEntities,
  normalizeStoredMap,
  serializeCourseMap,
  wouldCreateCycle,
} from '../courseMapModel';
import { clearCourseMapSessionState, getCourseMapSessionState, setCourseMapSessionState } from '../courseMapSessionState';
import { clearCourseMapLocalCache, readCourseMapLocalCache, readCourseMapLocalCacheAsync, writeCourseMapLocalCache } from '../courseMapLocalCache';
import { navigateToCourseEditor } from '../courseMapNavigation';
import { applyTaskGraphImport } from '../courseTaskGraphImport';
import CourseNode from '../nodes/CourseNode';
import CodeTestNode from '../nodes/CodeTestNode';
import TestNode from '../nodes/TestNode';
import ImageCodeNode from '../nodes/ImageCodeNode';
import MathNode from '../nodes/MathNode';
import LockedNode from '../nodes/LockedNode';
import CourseMapEdge from './CourseMapEdge';
import useSaveShortcut from '../../../hooks/useSaveShortcut';

const NODE_TYPES = {
  course: CourseNode,
  'code-test': CodeTestNode,
  test: TestNode,
  'image-code': ImageCodeNode,
  math: MathNode,
  locked: LockedNode,
};

const EDGE_TYPES = {
  courseMap: CourseMapEdge,
};

function userDisplayName(user) {
  return user?.displayName || user?.fullName || [user?.firstName, user?.lastName].filter(Boolean).join(' ').trim() || user?.login || user?.email || 'Пользователь';
}

function userAvatar(user) {
  return user?.avatarUrl || user?.avatar || user?.imageUrl || null;
}

function isEditableShortcutTarget(target) {
  if (!(target instanceof Element)) return false;
  if (target.closest('input, textarea, select, [contenteditable="true"], .monaco-editor, .ProseMirror')) return true;
  return false;
}

function subtreeCourses(rootId, allCourses, rootCourse) {
  const rows = [...(Array.isArray(allCourses) ? allCourses : [])];
  if (rootCourse?.id && !rows.some((item) => String(item?.id) === String(rootCourse.id))) rows.push(rootCourse);
  const byParent = new Map();
  for (const item of rows) {
    const parent = String(item?.parentCourseId || '');
    if (!byParent.has(parent)) byParent.set(parent, []);
    byParent.get(parent).push(item);
  }
  const byId = new Map(rows.map((item) => [String(item?.id || ''), item]));
  const result = [];
  const seen = new Set();
  const walk = (id) => {
    const key = String(id);
    if (seen.has(key)) return;
    seen.add(key);
    const item = byId.get(key);
    if (item) result.push(item);
    for (const child of byParent.get(key) || []) walk(child.id);
  };
  walk(rootId);
  return result;
}

const EDGE_EFFECTS = new Set(['inherit', 'start', 'stop']);

function normalizeEdgeEffect(value, legacyStart = false) {
  const normalized = String(value || '').trim().toLowerCase();
  if (EDGE_EFFECTS.has(normalized)) return normalized;
  return legacyStart ? 'start' : 'inherit';
}

function edgeAccessSettings(edge) {
  const settings = edge?.settings && typeof edge.settings === 'object' ? edge.settings : {};
  const legacyMode = String(settings.accessMode || 'normal').trim().toLowerCase();
  const legacyHiddenStart = settings.gateUntilPrerequisites === true || legacyMode === 'after-prerequisites';
  const legacySequentialStart = settings.sequentialReveal === true || legacyMode === 'sequential';
  return {
    hiddenEffect: normalizeEdgeEffect(settings.hiddenEffect, legacyHiddenStart),
    sequentialEffect: normalizeEdgeEffect(settings.sequentialEffect, legacySequentialStart),
  };
}

function edgeEffectBadges(accessSettings) {
  const badges = [];
  if (accessSettings.hiddenEffect === 'start' || accessSettings.hiddenEffect === 'stop') {
    badges.push({ kind: 'hidden', transition: accessSettings.hiddenEffect });
  }
  if (accessSettings.sequentialEffect === 'start' || accessSettings.sequentialEffect === 'stop') {
    badges.push({ kind: 'sequential', transition: accessSettings.sequentialEffect });
  }
  return badges;
}

function edgeStyle(editorMode, edge = null) {
  const accessSettings = edgeAccessSettings(edge);
  const synthetic = Boolean(edge?.settings?.synthetic);
  const classes = ['course-map-edge'];
  if (editorMode) classes.push('is-editable');
  if (accessSettings.hiddenEffect === 'start') classes.push('is-hidden-start');
  if (accessSettings.hiddenEffect === 'stop') classes.push('is-hidden-stop');
  if (accessSettings.sequentialEffect === 'start') classes.push('is-sequential-start');
  if (accessSettings.sequentialEffect === 'stop') classes.push('is-sequential-stop');
  if (synthetic) classes.push('is-locked');
  const effectBadges = editorMode && !synthetic ? edgeEffectBadges(accessSettings) : [];
  return {
    markerEnd: { type: MarkerType.ArrowClosed, width: 18, height: 18 },
    className: classes.join(' '),
    type: 'courseMap',
    interactionWidth: editorMode ? 28 : 18,
    label: undefined,
    labelShowBg: false,
    labelStyle: undefined,
    labelBgStyle: undefined,
    data: { ...(edge?.data || {}), effectBadges },
  };
}

function nodeAccessClassName(searchMatch, accessEffects, editorMode) {
  const classes = [];
  if (!searchMatch) classes.push('course-map-search-dimmed');
  if (editorMode && accessEffects?.hidden) classes.push('course-map-access-hidden');
  if (editorMode && accessEffects?.sequential) classes.push('course-map-access-sequential');
  if (editorMode && accessEffects?.hiddenMixed) classes.push('course-map-access-hidden-mixed');
  if (editorMode && accessEffects?.sequentialMixed) classes.push('course-map-access-sequential-mixed');
  return classes.join(' ');
}


function findOpenMapPosition(basePosition, existingNodes, index = 0) {
  const base = { x: Number(basePosition?.x) || 0, y: Number(basePosition?.y) || 0 };
  const occupied = (existingNodes || []).map((node) => ({ x: Number(node?.position?.x) || 0, y: Number(node?.position?.y) || 0 }));
  for (let step = index; step < index + 160; step += 1) {
    const column = step % 5;
    const row = Math.floor(step / 5);
    const candidate = { x: base.x + column * 300, y: base.y + row * 170 };
    const overlaps = occupied.some((item) => Math.abs(item.x - candidate.x) < 240 && Math.abs(item.y - candidate.y) < 125);
    if (!overlaps) return candidate;
  }
  return { x: base.x + index * 34, y: base.y + index * 34 };
}

function CourseMapInner({ course, allCourses, courseCanEdit, editorMode, query = '', focusCourseId = '', dataRevision = 0, onRefreshCourseData, onQueryChange, onShowGrid, onExportJson, onImportJson, exportBusy = false, graphImportRequest = null, onGraphImportComplete, onLearnerProgress = null }) {
  const nav = useNavigate();
  const location = useLocation();
  const notify = useNotify();
  const { access, user } = useAuth();
  const flow = useReactFlow();
  const shellRef = React.useRef(null);
  const [nodes, setNodes] = useNodesState([]);
  const [edges, setEdges] = useEdgesState([]);
  const [record, setRecord] = React.useState({ version: 0, document: null });
  const [assignments, setAssignments] = React.useState([]);
  const [mapCourses, setMapCourses] = React.useState([]);
  const [loading, setLoading] = React.useState(true);
  const [dirty, setDirty] = React.useState(false);
  const [presence, setPresence] = React.useState([]);
  const [context, setContext] = React.useState({ open: false, x: 0, y: 0, flowPosition: null, node: null, edge: null });
  const [unplacedOpen, setUnplacedOpen] = React.useState(false);
  const [serverChanged, setServerChanged] = React.useState(false);
  const [interacting, setInteracting] = React.useState(false);
  const [searchOpen, setSearchOpen] = React.useState(Boolean(query));
  const [graphRevision, setGraphRevision] = React.useState(0);
  const [mapHeight, setMapHeight] = React.useState(null);
  const [courseProgressByNode, setCourseProgressByNode] = React.useState(() => new Map());
  const viewportRef = React.useRef({ x: 0, y: 0, zoom: 1 });
  const nodesRef = React.useRef([]);
  const edgesRef = React.useRef([]);
  const assignmentsRef = React.useRef([]);
  const recordRef = React.useRef(record);
  const dirtyRef = React.useRef(dirty);
  const presenceIdsRef = React.useRef(new Set());
  const focusedCourseRef = React.useRef('');
  const dataRevisionRef = React.useRef(dataRevision);
  const loadMapRef = React.useRef(null);
  const loadRequestRef = React.useRef(0);
  const loadedRootRef = React.useRef('');
  const copyBufferRef = React.useRef(null);
  const pasteSequenceRef = React.useRef(0);
  const searchInputRef = React.useRef(null);
  const unplacedRef = React.useRef(null);
  const canvasRef = React.useRef(null);
  const lastMapPointerRef = React.useRef(null);
  const appliedGraphImportRef = React.useRef('');
  const courseProgressRef = React.useRef(new Map());
  const mapCoursesRef = React.useRef([]);
  const projectionTokenRef = React.useRef('');
  const pendingLearnerEdgesRef = React.useRef([]);
  const streamAbortRef = React.useRef(null);
  const learnerPersistTimerRef = React.useRef(null);
  const persistLearnerGraphRef = React.useRef(null);

  const rootId = String(course?.id || '');
  const mergedCourseRows = React.useMemo(() => {
    const byId = new Map();
    for (const item of [...(Array.isArray(allCourses) ? allCourses : []), ...(Array.isArray(mapCourses) ? mapCourses : [])]) {
      if (item?.id) byId.set(String(item.id), item);
    }
    if (course?.id) byId.set(String(course.id), { ...byId.get(String(course.id)), ...course });
    return Array.from(byId.values());
  }, [allCourses, course, mapCourses]);
  const visibleCourses = React.useMemo(() => subtreeCourses(rootId, mergedCourseRows, course), [course, mergedCourseRows, rootId]);
  const hiddenCourseIdsForStudents = React.useMemo(() => {
    const byId = new Map(visibleCourses.map((item) => [String(item?.id || ''), item]));
    const hidden = new Set();
    for (const item of visibleCourses) {
      const originId = String(item?.id || '');
      let current = item;
      const seen = new Set();
      while (current?.id && !seen.has(String(current.id))) {
        seen.add(String(current.id));
        if (current.isHiddenFromStudents) {
          hidden.add(originId);
          break;
        }
        current = current.parentCourseId ? byId.get(String(current.parentCourseId)) : null;
      }
    }
    return hidden;
  }, [visibleCourses]);
  const entityIndex = React.useMemo(() => buildEntityIndex(visibleCourses, assignments), [assignments, visibleCourses]);
  const currentUserId = String(user?.id || user?.userId || user?.uuid || '');
  const cacheCourseIds = React.useMemo(() => visibleCourses.map((item) => String(item?.id || '')).filter(Boolean), [visibleCourses]);
  const sessionOptions = React.useMemo(() => ({
    scope: editorMode ? 'editor' : 'learner',
    userId: currentUserId || 'anonymous',
    rootCourseId: rootId,
    aliases: cacheCourseIds,
  }), [cacheCourseIds, currentUserId, editorMode, rootId]);

  React.useEffect(() => { recordRef.current = record; }, [record]);
  React.useEffect(() => { dirtyRef.current = dirty; }, [dirty]);
  React.useEffect(() => { nodesRef.current = nodes; }, [nodes]);
  React.useEffect(() => { edgesRef.current = edges; }, [edges]);
  React.useEffect(() => { assignmentsRef.current = assignments; }, [assignments]);
  React.useEffect(() => { mapCoursesRef.current = mapCourses; }, [mapCourses]);
  React.useEffect(() => () => {
    persistLearnerGraphRef.current?.();
    streamAbortRef.current?.abort?.();
    window.clearTimeout(learnerPersistTimerRef.current);
  }, []);
  React.useEffect(() => { if (query) setSearchOpen(true); }, [query]);
  React.useEffect(() => {
    if (!searchOpen) return undefined;
    const frame = window.requestAnimationFrame(() => searchInputRef.current?.focus());
    return () => window.cancelAnimationFrame(frame);
  }, [searchOpen]);

  React.useEffect(() => {
    if (!unplacedOpen) return undefined;
    const close = (event) => {
      if (unplacedRef.current?.contains(event.target)) return;
      setUnplacedOpen(false);
    };
    const onKeyDown = (event) => {
      if (event.key === 'Escape') setUnplacedOpen(false);
    };
    document.addEventListener('pointerdown', close, true);
    window.addEventListener('keydown', onKeyDown, true);
    return () => {
      document.removeEventListener('pointerdown', close, true);
      window.removeEventListener('keydown', onKeyDown, true);
    };
  }, [unplacedOpen]);

  React.useLayoutEffect(() => {
    if (loading) return undefined;
    let frame = 0;
    const updateHeight = () => {
      window.cancelAnimationFrame(frame);
      frame = window.requestAnimationFrame(() => {
        const rect = shellRef.current?.getBoundingClientRect();
        if (!rect) return;
        const available = Math.floor(window.innerHeight - rect.top - 14);
        setMapHeight(Math.max(360, Math.min(1000, available)));
      });
    };
    updateHeight();
    window.addEventListener('resize', updateHeight);
    return () => {
      window.cancelAnimationFrame(frame);
      window.removeEventListener('resize', updateHeight);
    };
  }, [editorMode, loading, rootId]);

  const persistSession = React.useCallback((nextDirty = dirtyRef.current) => {
    if (!rootId || !nodesRef.current.length) return;
    const document = serializeCourseMap(nodesRef.current, edgesRef.current, viewportRef.current);
    const cachedDocument = editorMode ? document : {
      ...document,
      courseProgressVersion: 1,
      courseProgress: writeCourseProgress(courseProgressRef.current),
    };
    const dirtyValue = editorMode && Boolean(nextDirty);
    const mapRecord = {
      ...recordRef.current,
      rootCourseId: recordRef.current.rootCourseId || rootId,
      requestedCourseId: rootId,
      document: cachedDocument,
    };
    setCourseMapSessionState(rootId, {
      version: recordRef.current.version || 0,
      dirty: dirtyValue,
      aliases: cacheCourseIds,
      document,
    }, sessionOptions);
    writeCourseMapLocalCache({
      courseId: rootId,
      rootCourseId: mapRecord.rootCourseId,
      editorMode,
      userId: currentUserId,
      aliases: cacheCourseIds,
      mapRecord,
      assignments,
      courses: mapCoursesRef.current.length ? mapCoursesRef.current : visibleCourses,
      dirty: dirtyValue,
    });
  }, [assignments, cacheCourseIds, currentUserId, editorMode, rootId, sessionOptions, visibleCourses]);

  const rememberBeforeNavigate = React.useCallback(() => persistSession(dirtyRef.current), [persistSession]);

  React.useEffect(() => {
    if (!rootId || !nodes.length) return undefined;
    const timer = window.setTimeout(() => persistSession(dirtyRef.current), 420);
    return () => window.clearTimeout(timer);
  }, [edges, nodes, persistSession, rootId]);

  const openAssignment = React.useCallback((assignmentId) => {
    rememberBeforeNavigate();
    nav(`/assignment/${assignmentId}`, {
      state: { courseMapRootId: rootId, courseMapRequestedCourseId: rootId },
    });
  }, [nav, rememberBeforeNavigate, rootId]);

  const editAssignment = React.useCallback((assignmentId) => {
    rememberBeforeNavigate();
    const returnTo = `/course/${rootId}`;
    nav(`/assignment/${assignmentId}/edit?returnTo=${encodeURIComponent(returnTo)}`);
  }, [nav, rememberBeforeNavigate, rootId]);

  const editCourse = React.useCallback((courseId) => {
    rememberBeforeNavigate();
    navigateToCourseEditor(nav, location, rootId, courseId);
  }, [location, nav, rememberBeforeNavigate, rootId]);

  const focusNode = React.useCallback((nodeId) => {
    const node = flow.getNode(nodeId);
    if (!node) return;
    flow.fitView({ nodes: [node], duration: 460, padding: 1.8, maxZoom: 1.2 });
  }, [flow]);

  const focusBranch = React.useCallback((nodeId) => {
    const currentNodes = flow.getNodes();
    const currentEdges = flow.getEdges();
    const byId = new Map(currentNodes.map((node) => [String(node.id), node]));
    if (!byId.has(String(nodeId))) return;
    const outgoing = new Map();
    for (const edge of currentEdges) {
      const source = String(edge.source);
      if (!outgoing.has(source)) outgoing.set(source, []);
      outgoing.get(source).push(String(edge.target));
    }
    const visited = new Set();
    const pending = [String(nodeId)];
    while (pending.length) {
      const current = pending.shift();
      if (!current || visited.has(current)) continue;
      visited.add(current);
      const currentNode = byId.get(current);
      if (current !== String(nodeId) && currentNode?.type === 'course') continue;
      for (const next of outgoing.get(current) || []) pending.push(next);
    }
    const branchNodes = currentNodes.filter((node) => visited.has(String(node.id)));
    flow.fitView({ nodes: branchNodes.length ? branchNodes : [byId.get(String(nodeId))], duration: 520, padding: 0.45, maxZoom: 1.15 });
  }, [flow]);

  const decorateNodesWithSources = React.useCallback((rawNodes, rawEdges, assignmentRows, courseRows, progressOverride = null) => {
    const q = String(query || '').trim().toLowerCase();
    const idx = buildEntityIndex(courseRows, assignmentRows);
    const computedProgress = computeCourseProgress(rawNodes, rawEdges, assignmentRows, rootId);
    const progressByCourse = !editorMode && progressOverride?.size ? progressOverride : computedProgress;
    const accessByNode = computeCourseMapAccessEffects(rawNodes, rawEdges);
    return rawNodes.map((node) => {
      if (node.type === 'locked') {
        const settings = node.settings || node.data?.settings || {};
        const searchText = `${settings.title || ''} ${settings.requirement || ''}`.toLowerCase();
        const searchMatch = !q || searchText.includes(q);
        return {
          ...node,
          type: 'locked',
          className: `${searchMatch ? '' : 'course-map-search-dimmed'}${String(node.className || '').includes('course-map-node-revealed') ? ' course-map-node-revealed' : ''}`.trim(),
          data: { settings, editorMode: false, searchMatch },
        };
      }

      const isCourse = node.type === 'course';
      const rawEntity = isCourse ? idx.courseById.get(String(node.entityId)) : idx.assignmentById.get(String(node.entityId));
      const entity = isCourse && rawEntity
        ? { ...rawEntity, isHiddenForStudents: hiddenCourseIdsForStudents.has(String(rawEntity.id)) }
        : rawEntity;
      const progress = isCourse ? (progressByCourse.get(String(node.id)) || { total: 0, solved: 0, percent: 0 }) : null;
      const searchText = `${entity?.title || ''} ${previewAssignmentDescription(entity?.description || '')} ${entity?.tags || ''}`.toLowerCase();
      const searchMatch = !q || searchText.includes(q);
      const visualType = isCourse ? 'course' : assignmentNodeType(entity?.type || node.type);
      const accessEffects = accessByNode.get(String(node.id)) || null;
      const revealClass = String(node.className || '').includes('course-map-node-revealed') ? ' course-map-node-revealed' : '';
      return {
        ...node,
        type: visualType,
        className: `${nodeAccessClassName(searchMatch, accessEffects, editorMode)}${revealClass}`.trim(),
        data: {
          entityId: node.entityId,
          entity,
          progress,
          settings: node.settings,
          editorMode,
          searchMatch,
          accessEffects,
          onOpen: isCourse ? () => focusBranch(node.id) : () => openAssignment(node.entityId),
          onFocus: isCourse ? () => focusBranch(node.id) : () => focusNode(node.id),
          onEdit: isCourse ? () => editCourse(node.entityId) : () => editAssignment(node.entityId),
        },
      };
    });
  }, [editAssignment, editCourse, editorMode, focusBranch, focusNode, hiddenCourseIdsForStudents, openAssignment, query, rootId]);

  const decorateNodes = React.useCallback((rawNodes) => (
    decorateNodesWithSources(rawNodes, edgesRef.current, assignments, visibleCourses, courseProgressByNode)
  ), [assignments, courseProgressByNode, decorateNodesWithSources, visibleCourses]);

  const decorateEdges = React.useCallback((rawEdges) => rawEdges.map((edge) => ({ ...edge, ...edgeStyle(editorMode, edge) })), [editorMode]);

  const applyMapPayload = React.useCallback((mapRecord, treeAssignments, {
    preferSession = true,
    fromCache = false,
    cachedState = null,
  } = {}) => {
    const nextAssignments = Array.isArray(treeAssignments) ? treeAssignments : [];
    const cachedCourses = Array.isArray(cachedState?.courses) ? cachedState.courses : [];
    const pendingRevealNodeIds = !editorMode && fromCache
      ? new Set((cachedState?.pendingRevealNodeIds || []).map(String).filter(Boolean))
      : new Set();
    const knownCoursesById = new Map();
    for (const item of [...(Array.isArray(allCourses) ? allCourses : []), ...cachedCourses, ...mapCoursesRef.current]) {
      if (item?.id) knownCoursesById.set(String(item.id), item);
    }
    if (course?.id) knownCoursesById.set(String(course.id), { ...knownCoursesById.get(String(course.id)), ...course });
    const nextCourses = subtreeCourses(rootId, Array.from(knownCoursesById.values()), course);
    if (!editorMode) {
      mapCoursesRef.current = nextCourses;
      setMapCourses(nextCourses);
    }
    const suppliedCourseProgress = editorMode ? new Map() : readCourseProgress(mapRecord?.document);
    const session = preferSession ? getCourseMapSessionState(rootId, sessionOptions) : null;
    const persistedDraft = editorMode && cachedState?.dirty && cachedState?.mapRecord?.document
      ? {
          version: Number(cachedState.mapRecord.version || 0),
          dirty: true,
          aliases: cachedState.aliases || cacheCourseIds,
          document: cachedState.mapRecord.document,
        }
      : null;
    const localState = session?.document ? session : persistedDraft;
    const serverVersion = Number(mapRecord?.version || 0);
    const localVersion = Number(localState?.version || 0);
    const localIsDirty = editorMode && Boolean(localState?.dirty);
    const localMatchesServer = Boolean(localState?.document) && localVersion === serverVersion;
    const keepDirtyAcrossConflict = Boolean(localState?.document) && localIsDirty && localVersion !== serverVersion;
    let document = localMatchesServer || keepDirtyAcrossConflict ? localState.document : null;
    let isDirty = document ? localIsDirty : false;
    let expectedRecord = mapRecord || { version: 0, document: null };
    let changedOnServer = false;

    if (keepDirtyAcrossConflict) {
      expectedRecord = { ...expectedRecord, version: localVersion };
      changedOnServer = true;
    }

    if (!document) {
      document = normalizeStoredMap(mapRecord?.document, nextCourses, nextAssignments);
      if (!document) {
        document = buildDefaultCourseMap(rootId, nextCourses, nextAssignments);
        isDirty = Boolean(editorMode && (document.nodes.length || document.edges.length));
      }
    } else {
      document = normalizeStoredMap(document, nextCourses, nextAssignments)
        || buildDefaultCourseMap(rootId, nextCourses, nextAssignments);
    }

    setAssignments(nextAssignments);
    setRecord(expectedRecord);
    recordRef.current = expectedRecord;
    setDirty(isDirty);
    dirtyRef.current = isDirty;
    setServerChanged(changedOnServer);
    viewportRef.current = document.viewport || { x: 0, y: 0, zoom: 1 };

    const idx = buildEntityIndex(nextCourses, nextAssignments);
    const computedProgress = computeCourseProgress(document.nodes || [], document.edges || [], nextAssignments, rootId);
    const resolvedProgress = !editorMode && suppliedCourseProgress.size ? suppliedCourseProgress : computedProgress;
    courseProgressRef.current = new Map(resolvedProgress);
    setCourseProgressByNode(new Map(resolvedProgress));
    if (!editorMode && onLearnerProgress) {
      const rootNode = (document.nodes || []).find((node) => node?.type === 'course' && String(node?.entityId || '') === rootId);
      const rootProgress = rootNode ? resolvedProgress.get(String(rootNode.id)) : null;
      if (rootProgress) onLearnerProgress({ ...rootProgress, loading: false, isComplete: rootProgress.total > 0 && rootProgress.solved >= rootProgress.total });
    }
    const accessByNode = computeCourseMapAccessEffects(document.nodes || [], document.edges || []);
    const nextNodes = (document.nodes || []).map((node) => {
      if (node.type === 'locked') {
        return {
          ...node,
          type: 'locked',
          className: pendingRevealNodeIds.has(String(node.id)) ? 'course-map-node-revealed' : '',
          data: { settings: node.settings || {}, editorMode: false, searchMatch: true },
        };
      }
      const isCourse = node.type === 'course';
      const entity = isCourse ? idx.courseById.get(String(node.entityId)) : idx.assignmentById.get(String(node.entityId));
      const accessEffects = accessByNode.get(String(node.id)) || null;
      return {
        ...node,
        type: isCourse ? 'course' : assignmentNodeType(entity?.type || node.type),
        className: `${nodeAccessClassName(true, accessEffects, editorMode)}${pendingRevealNodeIds.has(String(node.id)) ? ' course-map-node-revealed' : ''}`.trim(),
        data: {
          entityId: node.entityId,
          entity,
          progress: isCourse ? (resolvedProgress.get(String(node.id)) || { total: 0, solved: 0, percent: 0 }) : null,
          settings: node.settings,
          editorMode,
          accessEffects,
          onOpen: isCourse ? () => focusBranch(node.id) : () => openAssignment(node.entityId),
          onFocus: isCourse ? () => focusBranch(node.id) : () => focusNode(node.id),
          onEdit: isCourse ? () => editCourse(node.entityId) : () => editAssignment(node.entityId),
        },
      };
    });
    const nextEdges = decorateEdges(document.edges || []);
    nodesRef.current = nextNodes;
    edgesRef.current = nextEdges;
    setNodes(nextNodes);
    setEdges(nextEdges);
    loadedRootRef.current = rootId;
    setLoading(false);

    setCourseMapSessionState(rootId, {
      version: Number(expectedRecord.version || 0),
      dirty: Boolean(isDirty),
      aliases: cacheCourseIds,
      document,
    }, sessionOptions);

    window.requestAnimationFrame(() => {
      try { flow.setViewport(document.viewport || { x: 0, y: 0, zoom: 1 }, { duration: 0 }); } catch {}
    });

    if (pendingRevealNodeIds.size) {
      window.setTimeout(() => {
        setNodes((current) => {
          const next = current.map((node) => pendingRevealNodeIds.has(String(node.id))
            ? { ...node, className: String(node.className || '').replace(/\bcourse-map-node-revealed\b/g, '').replace(/\s+/g, ' ').trim() }
            : node);
          nodesRef.current = next;
          return next;
        });
      }, 720);

      writeCourseMapLocalCache({
        courseId: rootId,
        rootCourseId: mapRecord?.rootCourseId || expectedRecord.rootCourseId || rootId,
        editorMode: false,
        userId: currentUserId,
        aliases: cachedState?.aliases || cacheCourseIds,
        mapRecord: { ...(mapRecord || expectedRecord), document },
        assignments: nextAssignments,
        courses: cachedState?.courses?.length ? cachedState.courses : nextCourses,
        dirty: false,
        pendingRevealNodeIds: [],
      });
    }

    if (!fromCache || isDirty) {
      const mapRecordForCache = {
        ...(mapRecord || expectedRecord),
        version: Number(expectedRecord.version || 0),
        rootCourseId: mapRecord?.rootCourseId || expectedRecord.rootCourseId || rootId,
        requestedCourseId: mapRecord?.requestedCourseId || rootId,
        document: editorMode ? document : {
          ...document,
          courseProgressVersion: 1,
          courseProgress: writeCourseProgress(resolvedProgress),
        },
      };
      writeCourseMapLocalCache({
        courseId: rootId,
        rootCourseId: mapRecordForCache.rootCourseId,
        editorMode,
        userId: currentUserId,
        aliases: cacheCourseIds,
        mapRecord: mapRecordForCache,
        assignments: nextAssignments,
        courses: cachedState?.courses?.length ? cachedState.courses : visibleCourses,
        dirty: Boolean(isDirty),
      });
    }
  }, [allCourses, cacheCourseIds, course, currentUserId, decorateEdges, editAssignment, editCourse, editorMode, flow, focusBranch, focusNode, onLearnerProgress, openAssignment, rootId, sessionOptions, setEdges, setNodes, visibleCourses]);

  const mergeRowsById = React.useCallback((current, incoming) => {
    const byId = new Map();
    for (const item of Array.isArray(current) ? current : []) {
      if (item?.id) byId.set(String(item.id), item);
    }
    for (const item of Array.isArray(incoming) ? incoming : []) {
      if (!item?.id) continue;
      const previous = byId.get(String(item.id)) || {};
      byId.set(String(item.id), { ...previous, ...item });
    }
    return Array.from(byId.values());
  }, []);

  const updateLearnerProjectionRecord = React.useCallback((patch = {}) => {
    const current = recordRef.current || { version: 0, document: null };
    const next = { ...current, ...patch };
    setRecord(next);
    recordRef.current = next;
    if (next.projectionToken) projectionTokenRef.current = String(next.projectionToken);
    return next;
  }, []);

  const mergeLearnerGraph = React.useCallback(({
    nodes: incomingNodes = [],
    edges: incomingEdges = [],
    assignments: incomingAssignments = [],
    courses: incomingCourses = [],
    courseProgress = {},
    animate = true,
  } = {}) => {
    const normalizedAssignments = (Array.isArray(incomingAssignments) ? incomingAssignments : []).map((item) => ({
      ...item,
      solvedByCurrentUser: item?.solvedByCurrentUser === true,
      isSolved: item?.solvedByCurrentUser === true,
      progressStatus: item?.solvedByCurrentUser === true ? 'solved' : 'not-started',
    }));
    const mergedAssignments = mergeRowsById(assignmentsRef.current, normalizedAssignments);
    assignmentsRef.current = mergedAssignments;
    setAssignments(mergedAssignments);

    const normalizedCourses = Array.isArray(incomingCourses) ? incomingCourses : [];
    const mergedCourses = mergeRowsById(mapCoursesRef.current.length ? mapCoursesRef.current : visibleCourses, normalizedCourses);
    mapCoursesRef.current = mergedCourses;
    setMapCourses(mergedCourses);

    const nextProgress = new Map(courseProgressRef.current);
    for (const [nodeId, value] of Object.entries(courseProgress || {})) {
      const total = Math.max(0, Number(value?.total) || 0);
      const solved = Math.max(0, Math.min(total, Number(value?.solved) || 0));
      const percent = Math.max(0, Math.min(100, Number(value?.percent) || 0));
      nextProgress.set(String(nodeId), { total, solved, percent });
    }
    courseProgressRef.current = nextProgress;
    setCourseProgressByNode(new Map(nextProgress));

    const incomingNodeById = new Map((Array.isArray(incomingNodes) ? incomingNodes : [])
      .filter((node) => node?.id)
      .map((node) => [String(node.id), node]));
    const incomingAssignmentIds = new Set(normalizedAssignments.map((item) => String(item?.id || '')).filter(Boolean));
    const incomingCourseIds = new Set(normalizedCourses.map((item) => String(item?.id || '')).filter(Boolean));
    const assignmentById = new Map(mergedAssignments.map((item) => [String(item?.id || ''), item]));
    const courseById = new Map(mergedCourses.map((item) => [String(item?.id || ''), item]));
    const existingNodeIds = new Set(nodesRef.current.map((node) => String(node.id)));
    const rawNewNodes = (Array.isArray(incomingNodes) ? incomingNodes : [])
      .filter((node) => node?.id && !existingNodeIds.has(String(node.id)));

    const updatedExistingNodes = nodesRef.current.map((node) => {
      const incoming = incomingNodeById.get(String(node.id));
      const entityId = String(incoming?.entityId || node?.entityId || node?.data?.entityId || '');
      const isCourse = node.type === 'course' || incoming?.type === 'course';
      const entityChanged = isCourse ? incomingCourseIds.has(entityId) : incomingAssignmentIds.has(entityId);
      const progress = isCourse ? nextProgress.get(String(node.id)) : null;
      if (!incoming && !entityChanged && !progress) return node;
      const entity = isCourse ? courseById.get(entityId) : assignmentById.get(entityId);
      return {
        ...node,
        ...(incoming ? {
          entityId: incoming.entityId ?? node.entityId,
          position: incoming.position || node.position,
          settings: incoming.settings ?? node.settings,
        } : {}),
        data: {
          ...(node.data || {}),
          entityId: incoming?.entityId ?? node.data?.entityId ?? node.entityId,
          ...(entity ? { entity } : {}),
          ...(progress ? { progress } : {}),
          ...(incoming ? { settings: incoming.settings ?? node.data?.settings } : {}),
        },
      };
    });
    const allRawNodes = [...updatedExistingNodes, ...rawNewNodes];

    if (!editorMode && onLearnerProgress) {
      const rootCourseNode = allRawNodes.find((node) => node?.type === 'course' && String(node?.entityId || node?.data?.entityId || '') === rootId);
      const rootProgress = rootCourseNode ? nextProgress.get(String(rootCourseNode.id)) : null;
      if (rootProgress) {
        onLearnerProgress({
          ...rootProgress,
          loading: false,
          isComplete: rootProgress.total > 0 && rootProgress.solved >= rootProgress.total,
        });
      }
    }

    const incomingEdgeById = new Map((Array.isArray(incomingEdges) ? incomingEdges : [])
      .filter((edge) => edge?.id)
      .map((edge) => [String(edge.id), edge]));
    const updatedExistingEdges = edgesRef.current.map((edge) => {
      const incoming = incomingEdgeById.get(String(edge.id));
      if (!incoming) return edge;
      return decorateEdges([{ ...edge, ...incoming }])[0];
    });
    const knownEdgeIds = new Set(updatedExistingEdges.map((edge) => String(edge.id)));
    const pendingById = new Map();
    for (const edge of [...pendingLearnerEdgesRef.current, ...(Array.isArray(incomingEdges) ? incomingEdges : [])]) {
      if (!edge?.id || knownEdgeIds.has(String(edge.id))) continue;
      pendingById.set(String(edge.id), edge);
    }
    const allNodeIds = new Set(allRawNodes.map((node) => String(node.id)));
    const readyEdges = [];
    const stillPending = [];
    for (const edge of pendingById.values()) {
      if (allNodeIds.has(String(edge.source)) && allNodeIds.has(String(edge.target))) readyEdges.push(edge);
      else stillPending.push(edge);
    }
    pendingLearnerEdgesRef.current = stillPending;

    const allRawEdgesForDecoration = [...updatedExistingEdges, ...readyEdges];
    const decoratedNewNodes = decorateNodesWithSources(
      rawNewNodes.map((node) => ({
        ...node,
        className: animate ? `${node.className || ''} course-map-node-revealed`.trim() : node.className,
      })),
      allRawEdgesForDecoration,
      mergedAssignments,
      mergedCourses,
      nextProgress,
    );
    const decoratedNewEdges = decorateEdges(readyEdges);
    const nextNodes = [...updatedExistingNodes, ...decoratedNewNodes];
    const nextEdges = [...updatedExistingEdges, ...decoratedNewEdges];
    nodesRef.current = nextNodes;
    edgesRef.current = nextEdges;
    setNodes(nextNodes);
    setEdges(nextEdges);

    if (animate && decoratedNewNodes.length) {
      window.setTimeout(() => {
        setNodes((current) => {
          const changed = current.map((node) => ({
            ...node,
            className: String(node.className || '').replace(/\bcourse-map-node-revealed\b/g, '').replace(/\s+/g, ' ').trim(),
          }));
          nodesRef.current = changed;
          return changed;
        });
      }, 720);
    }

    loadedRootRef.current = rootId;
    setLoading(false);
  }, [decorateEdges, decorateNodesWithSources, editorMode, mergeRowsById, onLearnerProgress, rootId, setEdges, setNodes, visibleCourses]);

  const persistLearnerGraph = React.useCallback(() => {
    if (editorMode || !rootId || !nodesRef.current.length) return;
    const document = {
      ...serializeCourseMap(nodesRef.current, edgesRef.current, viewportRef.current),
      courseProgressVersion: 1,
      courseProgress: writeCourseProgress(courseProgressRef.current),
    };
    const mapRecord = {
      ...recordRef.current,
      rootCourseId: recordRef.current.rootCourseId || rootId,
      requestedCourseId: recordRef.current.requestedCourseId || rootId,
      projectionToken: projectionTokenRef.current || recordRef.current.projectionToken || '',
      document,
    };
    recordRef.current = mapRecord;
    setRecord(mapRecord);
    writeCourseMapLocalCache({
      courseId: rootId,
      rootCourseId: mapRecord.rootCourseId,
      editorMode: false,
      userId: currentUserId,
      aliases: mapCoursesRef.current.map((item) => String(item?.id || '')).filter(Boolean),
      mapRecord,
      assignments: assignmentsRef.current,
      courses: mapCoursesRef.current,
      dirty: false,
    });
  }, [currentUserId, editorMode, rootId]);

  persistLearnerGraphRef.current = persistLearnerGraph;

  const applyLearnerDelta = React.useCallback((delta) => {
    if (!delta || delta.resetRequired) return false;
    const removeNodeIds = new Set((delta.nodeIdsRemoved || []).map(String));
    const removeEdgeIds = new Set((delta.edgeIdsRemoved || []).map(String));
    if (removeNodeIds.size || removeEdgeIds.size) {
      const nextNodes = nodesRef.current.filter((node) => !removeNodeIds.has(String(node.id)));
      const nextEdges = edgesRef.current.filter((edge) => !removeEdgeIds.has(String(edge.id)) && !removeNodeIds.has(String(edge.source)) && !removeNodeIds.has(String(edge.target)));
      nodesRef.current = nextNodes;
      edgesRef.current = nextEdges;
      setNodes(nextNodes);
      setEdges(nextEdges);
    }

    mergeLearnerGraph({
      nodes: delta.nodesAdded || [],
      edges: delta.edgesAdded || [],
      assignments: delta.assignmentsChanged || [],
      courses: delta.coursesChanged || [],
      courseProgress: delta.courseProgress || {},
      animate: true,
    });
    updateLearnerProjectionRecord({
      version: Number(delta.version || recordRef.current.version || 0),
      projectionToken: delta.projectionToken || projectionTokenRef.current,
      projectionRevision: Number(delta.projectionRevision || 0),
    });
    persistLearnerGraph();
    return true;
  }, [mergeLearnerGraph, persistLearnerGraph, setEdges, setNodes, updateLearnerProjectionRecord]);

  const streamLearnerMap = React.useCallback(async (requestId, { quiet = false, preserveExisting = false } = {}) => {
    streamAbortRef.current?.abort?.();
    const controller = new AbortController();
    streamAbortRef.current = controller;
    let receivedSegment = false;
    const seenNodeIds = new Set();
    const seenEdgeIds = new Set();
    if (!quiet) setLoading(true);

    await streamLearningCourseMap(rootId, {
      signal: controller.signal,
      onMeta: (meta) => {
        if (requestId !== loadRequestRef.current) return;
        projectionTokenRef.current = '';
        pendingLearnerEdgesRef.current = [];
        if (!preserveExisting) {
          viewportRef.current = meta?.viewport || viewportRef.current;
          assignmentsRef.current = [];
          mapCoursesRef.current = course?.id ? [course] : [];
          setAssignments([]);
          setMapCourses(mapCoursesRef.current);
          nodesRef.current = [];
          edgesRef.current = [];
          setNodes([]);
          setEdges([]);
        }
        updateLearnerProjectionRecord({
          rootCourseId: meta?.rootCourseId || rootId,
          requestedCourseId: meta?.requestedCourseId || rootId,
          version: Number(meta?.version || 0),
          projectionToken: '',
          projectionRevision: 0,
          updatedAt: meta?.updatedAt || null,
          updatedBy: meta?.updatedBy || null,
          ...(preserveExisting ? {} : {
            document: {
              schemaVersion: 1,
              courseProgressVersion: 1,
              courseProgress: {},
              viewport: meta?.viewport || { x: 0, y: 0, zoom: 1 },
              nodes: [],
              edges: [],
            },
          }),
        });
        if (!preserveExisting) {
          try { flow.setViewport(meta?.viewport || { x: 0, y: 0, zoom: 1 }, { duration: 0 }); } catch {}
        }
      },
      onSegment: (segment) => {
        if (requestId !== loadRequestRef.current || !segment) return;
        receivedSegment = true;
        for (const node of segment.nodes || []) if (node?.id) seenNodeIds.add(String(node.id));
        for (const edge of segment.edges || []) if (edge?.id) seenEdgeIds.add(String(edge.id));
        mergeLearnerGraph({ ...segment, animate: nodesRef.current.length > 0 });
        window.clearTimeout(learnerPersistTimerRef.current);
        learnerPersistTimerRef.current = window.setTimeout(() => persistLearnerGraph(), 180);
      },
      onDone: (done, meta) => {
        if (requestId !== loadRequestRef.current) return;
        updateLearnerProjectionRecord({
          rootCourseId: meta?.rootCourseId || rootId,
          requestedCourseId: meta?.requestedCourseId || rootId,
          version: Number(meta?.version || recordRef.current.version || 0),
          projectionToken: done?.projectionToken || meta?.projectionToken || '',
          projectionRevision: Number(done?.projectionRevision || meta?.projectionRevision || 0),
          updatedAt: meta?.updatedAt || recordRef.current.updatedAt || null,
          updatedBy: meta?.updatedBy || recordRef.current.updatedBy || null,
        });
        if (preserveExisting) {
          const staleNodeIds = new Set(nodesRef.current
            .map((node) => String(node.id))
            .filter((id) => !seenNodeIds.has(id)));
          const nextNodes = nodesRef.current.filter((node) => !staleNodeIds.has(String(node.id)));
          const nextEdges = edgesRef.current.filter((edge) => seenEdgeIds.has(String(edge.id))
            && !staleNodeIds.has(String(edge.source))
            && !staleNodeIds.has(String(edge.target)));
          nodesRef.current = nextNodes;
          edgesRef.current = nextEdges;
          setNodes(nextNodes);
          setEdges(nextEdges);
          pendingLearnerEdgesRef.current = [];

          const visibleNodeIds = new Set(nextNodes.map((node) => String(node.id)));
          courseProgressRef.current = new Map(
            Array.from(courseProgressRef.current.entries()).filter(([nodeId]) => visibleNodeIds.has(String(nodeId)))
          );
          setCourseProgressByNode(new Map(courseProgressRef.current));

          const visibleEntityIds = new Set(nextNodes
            .map((node) => String(node?.entityId || node?.data?.entityId || ''))
            .filter(Boolean));
          assignmentsRef.current = assignmentsRef.current.filter((item) => visibleEntityIds.has(String(item?.id || '')));
          mapCoursesRef.current = mapCoursesRef.current.filter((item) => visibleEntityIds.has(String(item?.id || '')) || String(item?.id || '') === rootId);
          setAssignments(assignmentsRef.current);
          setMapCourses(mapCoursesRef.current);
        }
        window.clearTimeout(learnerPersistTimerRef.current);
        learnerPersistTimerRef.current = null;
        persistLearnerGraph();
      },
    });

    if (requestId === loadRequestRef.current && !receivedSegment) setLoading(false);
  }, [course, flow, mergeLearnerGraph, persistLearnerGraph, rootId, setEdges, setNodes, updateLearnerProjectionRecord]);

  const loadMap = React.useCallback(async ({ preferSession = true, quiet = false } = {}) => {
    if (!rootId) return;
    const requestId = ++loadRequestRef.current;
    const initialForRoot = loadedRootRef.current !== rootId;
    let restoredFromCache = false;
    let cached = null;

    if (initialForRoot && preferSession) {
      cached = readCourseMapLocalCache({ courseId: rootId, editorMode, userId: currentUserId });
      if (!cached) cached = await readCourseMapLocalCacheAsync({ courseId: rootId, editorMode, userId: currentUserId });
      if (requestId !== loadRequestRef.current) return;
      const cacheHasProgress = editorMode || Number(cached?.mapRecord?.document?.courseProgressVersion) === 1;
      if (cached?.mapRecord && cacheHasProgress) {
        applyMapPayload(cached.mapRecord, cached.assignments, { preferSession: true, fromCache: true, cachedState: cached });
        if (!editorMode) {
          projectionTokenRef.current = String(cached.mapRecord?.projectionToken || '');
          mapCoursesRef.current = Array.isArray(cached.courses) ? cached.courses : [];
          setMapCourses(mapCoursesRef.current);
        }
        restoredFromCache = true;
      } else if (cached?.mapRecord && !editorMode) {
        clearCourseMapLocalCache({ courseId: rootId, editorMode: false, userId: currentUserId });
      }
    }

    if (!quiet && initialForRoot && !restoredFromCache) setLoading(true);

    if (!editorMode) {
      try {
        const token = String(cached?.mapRecord?.projectionToken || projectionTokenRef.current || '');
        if (restoredFromCache && token) {
          const projectionCourseId = String(cached?.mapRecord?.requestedCourseId || cached?.requestedCourseId || rootId);
          const delta = await getLearningCourseMapDelta(projectionCourseId, token, null);
          if (requestId !== loadRequestRef.current) return;
          if (!delta?.resetRequired) {
            applyLearnerDelta(delta);
            return;
          }
        }
        await streamLearnerMap(requestId, { quiet: restoredFromCache || quiet, preserveExisting: restoredFromCache });
      } catch (error) {
        if (error?.name === 'AbortError') return;
        if (requestId === loadRequestRef.current && !restoredFromCache) {
          notify.error(getApiErrorMessage(error, 'Не удалось загрузить карту курса'));
        }
      } finally {
        if (requestId === loadRequestRef.current && !restoredFromCache) setLoading(false);
      }
      return;
    }

    try {
      const [mapRecord, treeAssignments] = await Promise.all([
        getCourseMap(rootId),
        getAssignmentsByCourseTree(rootId),
      ]);
      if (requestId !== loadRequestRef.current) return;
      applyMapPayload(mapRecord, treeAssignments, { preferSession, fromCache: false });
    } catch (error) {
      if (requestId === loadRequestRef.current && !restoredFromCache) {
        notify.error(getApiErrorMessage(error, 'Не удалось загрузить карту курса'));
      }
    } finally {
      if (requestId === loadRequestRef.current && initialForRoot && !restoredFromCache) setLoading(false);
    }
  }, [applyLearnerDelta, applyMapPayload, currentUserId, editorMode, notify, rootId, streamLearnerMap]);

  loadMapRef.current = loadMap;
  React.useEffect(() => {
    if (!rootId) return;
    if (loadedRootRef.current !== rootId) {
      setLoading(true);
      setNodes([]);
      setEdges([]);
    }
    void loadMapRef.current?.({ preferSession: true, quiet: loadedRootRef.current === rootId });
  }, [editorMode, rootId, setEdges, setNodes]);

  React.useEffect(() => {
    if (!editorMode) return undefined;
    if (dataRevisionRef.current === dataRevision) return undefined;
    dataRevisionRef.current = dataRevision;
    if (!rootId) return;
    let disposed = false;
    getAssignmentsByCourseTree(rootId).then((rows) => {
      if (disposed) return;
      const nextAssignments = Array.isArray(rows) ? rows : [];
      setAssignments(nextAssignments);
      const document = serializeCourseMap(nodesRef.current, edgesRef.current, viewportRef.current);
      const cachedDocument = editorMode ? document : {
        ...document,
        courseProgressVersion: 1,
        courseProgress: writeCourseProgress(courseProgressRef.current),
      };
      writeCourseMapLocalCache({
        courseId: rootId,
        rootCourseId: recordRef.current.rootCourseId || rootId,
        editorMode,
        userId: currentUserId,
        aliases: cacheCourseIds,
        mapRecord: { ...recordRef.current, document: cachedDocument },
        assignments: nextAssignments,
        courses: mapCoursesRef.current.length ? mapCoursesRef.current : visibleCourses,
        dirty: editorMode && dirtyRef.current,
      });
    }).catch(() => {});
    return () => { disposed = true; };
  }, [cacheCourseIds, currentUserId, dataRevision, editorMode, rootId, visibleCourses]);

  React.useEffect(() => {
    if (!nodesRef.current.length) return;
    setNodes((current) => {
      const next = decorateNodes(current);
      nodesRef.current = next;
      return next;
    });
  }, [decorateNodes, graphRevision, setNodes]);

  React.useEffect(() => {
    const wanted = String(focusCourseId || '').trim();
    if (!wanted || !nodes.length) return;
    const targetId = courseNodeId(wanted);
    if (!nodes.some((node) => node.id === targetId)) return;
    const focusKey = `${rootId}:${wanted}`;
    if (focusedCourseRef.current === focusKey) return;
    focusedCourseRef.current = focusKey;
    window.requestAnimationFrame(() => focusBranch(targetId));
  }, [focusBranch, focusCourseId, nodes, rootId]);

  React.useEffect(() => {
    if (!editorMode || !rootId) return undefined;
    let disposed = false;
    let heartbeat = null;
    const connection = createCourseMapPresenceConnection(access, rootId);
    const handlePresence = (rows) => {
      if (disposed) return;
      const list = Array.isArray(rows) ? rows : [];
      const nextIds = new Set(list.map((row) => String(row?.userId || '')).filter(Boolean));
      const previous = presenceIdsRef.current;
      for (const row of list) {
        const uid = String(row?.userId || '');
        if (uid && uid !== currentUserId && !previous.has(uid)) notify.info(`${row.displayName || 'Редактор'} открыл карту`);
      }
      presenceIdsRef.current = nextIds;
      setPresence(list);
    };
    const handleMapSaved = (event) => {
      if (disposed) return;
      const by = String(event?.updatedBy || '');
      if (by && by === currentUserId) return;
      if (dirtyRef.current) {
        setServerChanged(true);
        notify.warn('Другой редактор сохранил новую версию карты. Ваши изменения не перезаписаны.');
      } else {
        notify.info('Карта обновлена другим редактором');
        void loadMapRef.current?.({ preferSession: false, quiet: true });
      }
    };
    connection.on('PresenceChanged', handlePresence);
    connection.on('MapSaved', handleMapSaved);
    connection.start().then(async () => {
      if (disposed) return;
      await connection.invoke('JoinMap', rootId, userDisplayName(user), userAvatar(user), dirtyRef.current);
      heartbeat = window.setInterval(() => {
        connection.invoke('Heartbeat', rootId, dirtyRef.current, userDisplayName(user), userAvatar(user)).catch(() => {});
      }, 15000);
    }).catch(() => {});
    return () => {
      disposed = true;
      if (heartbeat) window.clearInterval(heartbeat);
      connection.off('PresenceChanged', handlePresence);
      connection.off('MapSaved', handleMapSaved);
      connection.invoke('LeaveMap', rootId).catch(() => {});
      disposeCourseMapPresenceConnection(access, rootId).catch(() => {});
    };
  }, [access, currentUserId, editorMode, notify, rootId, user]);

  React.useEffect(() => {
    if (!editorMode || !rootId) return;
    const connection = createCourseMapPresenceConnection(access, rootId);
    connection?.invoke('Heartbeat', rootId, dirty, userDisplayName(user), userAvatar(user)).catch(() => {});
  }, [access, dirty, editorMode, rootId, user]);

  const markDirty = React.useCallback(() => {
    if (!editorMode) return;
    setDirty(true);
    dirtyRef.current = true;
  }, [editorMode]);

  const removeEdges = React.useCallback((edgeIds) => {
    if (!editorMode) return false;
    const ids = edgeIds instanceof Set ? edgeIds : new Set(edgeIds || []);
    if (!ids.size) return false;
    const existing = edgesRef.current;
    const next = existing.filter((edge) => !ids.has(String(edge.id)));
    if (next.length === existing.length) return false;
    edgesRef.current = next;
    setEdges(next);
    markDirty();
    setGraphRevision((value) => value + 1);
    return true;
  }, [editorMode, markDirty, setEdges]);

  const updateEdgeAccessSettings = React.useCallback((edgeId, patch) => {
    if (!editorMode) return;
    const buildUpdatedEdge = (edge) => {
      const currentAccess = edgeAccessSettings(edge);
      const nextAccess = {
        hiddenEffect: normalizeEdgeEffect(patch?.hiddenEffect ?? currentAccess.hiddenEffect),
        sequentialEffect: normalizeEdgeEffect(patch?.sequentialEffect ?? currentAccess.sequentialEffect),
      };
      const otherSettings = { ...(edge.settings || {}) };
      delete otherSettings.accessMode;
      delete otherSettings.gateUntilPrerequisites;
      delete otherSettings.sequentialReveal;
      const updated = {
        ...edge,
        settings: {
          ...otherSettings,
          hiddenEffect: nextAccess.hiddenEffect,
          sequentialEffect: nextAccess.sequentialEffect,
        },
      };
      return { ...updated, ...edgeStyle(true, updated) };
    };

    setEdges((current) => {
      const next = current.map((edge) => String(edge.id) === String(edgeId) ? buildUpdatedEdge(edge) : edge);
      edgesRef.current = next;
      return next;
    });
    setContext((current) => {
      if (!current?.edge || String(current.edge.id) !== String(edgeId)) return current;
      return { ...current, edge: buildUpdatedEdge(current.edge) };
    });
    markDirty();
    setGraphRevision((value) => value + 1);
  }, [editorMode, markDirty, setEdges]);

  const onNodesChange = React.useCallback((changes) => {
    setNodes((current) => {
      const next = applyNodeChanges(changes, current);
      nodesRef.current = next;
      return next;
    });
    if (editorMode && changes.some((change) => change.type === 'position' || change.type === 'remove')) markDirty();
  }, [editorMode, markDirty, setNodes]);

  const onEdgesChange = React.useCallback((changes) => {
    if (!editorMode) return;
    setEdges((current) => {
      const next = applyEdgeChanges(changes, current);
      edgesRef.current = next;
      return next;
    });
    if (changes.some((change) => change.type === 'remove')) {
      markDirty();
      setGraphRevision((value) => value + 1);
    }
  }, [editorMode, markDirty, setEdges]);

  const onConnect = React.useCallback((connection) => {
    if (!editorMode) return;
    if ((connection.sourceHandle && connection.sourceHandle !== 'out') || (connection.targetHandle && connection.targetHandle !== 'in')) return;
    const currentNodes = nodesRef.current;
    const currentEdges = edgesRef.current;
    if (wouldCreateCycle(currentNodes, currentEdges, connection.source, connection.target)) {
      notify.warn('Такое соединение создаст цикл. Карта должна оставаться направленной без циклов.');
      return;
    }
    const duplicate = currentEdges.some((edge) => edge.source === connection.source && edge.target === connection.target);
    if (duplicate) return;
    setEdges((current) => {
      const created = {
        ...connection,
        sourceHandle: connection.sourceHandle || 'out',
        targetHandle: connection.targetHandle || 'in',
        id: `edge:${Date.now()}:${Math.random().toString(36).slice(2, 8)}`,
        settings: { hiddenEffect: 'inherit', sequentialEffect: 'inherit' },
      };
      const next = addEdge({ ...created, ...edgeStyle(true, created) }, current);
      edgesRef.current = next;
      return next;
    });
    markDirty();
    setGraphRevision((value) => value + 1);
  }, [editorMode, markDirty, notify, setEdges]);

  const onEdgeDoubleClick = React.useCallback((event, edge) => {
    if (!editorMode) return;
    event.preventDefault();
    event.stopPropagation();
    removeEdges([String(edge.id)]);
  }, [editorMode, removeEdges]);

  const onEdgeContextMenu = React.useCallback((event, edge) => {
    if (!editorMode || !courseCanEdit) return;
    event.preventDefault();
    event.stopPropagation();
    setContext({ open: true, x: event.clientX, y: event.clientY, flowPosition: null, node: null, edge });
  }, [courseCanEdit, editorMode]);

  const onPaneContextMenu = React.useCallback((event) => {
    if (!editorMode || !courseCanEdit) return;
    event.preventDefault();
    const position = flow.screenToFlowPosition({ x: event.clientX, y: event.clientY });
    setContext({ open: true, x: event.clientX, y: event.clientY, flowPosition: position, node: null, edge: null });
  }, [courseCanEdit, editorMode, flow]);

  const onNodeContextMenu = React.useCallback((event, node) => {
    if (!node || node.type === 'locked') return;
    event.preventDefault();
    event.stopPropagation();
    setContext({ open: true, x: event.clientX, y: event.clientY, flowPosition: null, node, edge: null });
  }, []);

  const closeContext = React.useCallback(() => setContext((current) => ({ ...current, open: false })), []);

  const appendNode = React.useCallback((node) => {
    setNodes((current) => {
      const next = [...current, node];
      nodesRef.current = next;
      if (rootId) {
        const document = serializeCourseMap(next, edgesRef.current, viewportRef.current);
        setCourseMapSessionState(rootId, {
          version: recordRef.current.version || 0,
          dirty: true,
          aliases: cacheCourseIds,
          document,
        }, sessionOptions);
      }
      return next;
    });
    markDirty();
    setGraphRevision((value) => value + 1);
  }, [cacheCourseIds, markDirty, rootId, sessionOptions, setNodes]);

  const createMapNode = React.useCallback(async (kind, position) => {
    if (!editorMode || !courseCanEdit) return;
    closeContext();
    try {
      if (kind === 'course') {
        const created = await createCourse({
          title: 'Новый курс',
          description: 'Описание курса',
          isPublic: false,
          visibleGroupIds: [],
          ownerIds: [],
          parentCourseId: rootId,
          sort: visibleCourses.length,
        });
        const node = { id: courseNodeId(created.id), type: 'course', entityId: String(created.id), position: position || { x: 0, y: 0 } };
        appendNode({ ...node, data: { entityId: node.entityId, entity: created, progress: { total: 0, solved: 0, percent: 0 }, editorMode, onFocus: () => focusBranch(node.id), onOpen: () => focusBranch(node.id), onEdit: () => editCourse(created.id) } });
        await onRefreshCourseData?.();
        notify.success('Курс добавлен на карту');
        const returnTo = `/course/${rootId}`;
        nav(`/courses/${created.id}/edit?returnTo=${encodeURIComponent(returnTo)}`);
        return;
      }

      const assignmentType = kind === 'image-code' ? 'image-test' : kind;
      const payload = buildDefaultAssignmentPayload(assignmentType, assignments.length);
      const created = await createAssignment(rootId, payload);
      const node = { id: assignmentNodeId(created.id), type: assignmentNodeType(created.type), entityId: String(created.id), position: position || { x: 0, y: 0 } };
      setAssignments((current) => [...current, created]);
      appendNode({ ...node, data: { entityId: node.entityId, entity: created, editorMode, onOpen: () => openAssignment(created.id), onEdit: () => editAssignment(created.id) } });
      notify.success('Задание создано');
      const returnTo = `/course/${rootId}`;
      window.setTimeout(() => nav(`/assignment/${created.id}/edit?returnTo=${encodeURIComponent(returnTo)}`), 0);
    } catch (error) {
      notify.error(getApiErrorMessage(error, kind === 'course' ? 'Не удалось создать курс' : 'Не удалось создать задание'));
    }
  }, [appendNode, assignments.length, closeContext, courseCanEdit, editAssignment, editCourse, editorMode, focusBranch, nav, notify, onRefreshCourseData, openAssignment, rootId, setAssignments, visibleCourses.length]);

  const removeNodeFromMap = React.useCallback((node) => {
    if (!node || (node.type === 'course' && String(node.entityId) === rootId)) {
      if (node?.type === 'course') notify.warn('Корневой курс нельзя убрать с собственной карты.');
      return;
    }
    setNodes((current) => {
      const next = current.filter((item) => item.id !== node.id);
      nodesRef.current = next;
      return next;
    });
    setEdges((current) => {
      const next = current.filter((edge) => edge.source !== node.id && edge.target !== node.id);
      edgesRef.current = next;
      return next;
    });
    markDirty();
    setGraphRevision((value) => value + 1);
  }, [markDirty, notify, rootId, setEdges, setNodes]);

  const removeEntityFromMap = React.useCallback((entityId) => {
    const key = String(entityId || '');
    if (!key) return;
    const nodeIds = new Set(nodesRef.current
      .filter((item) => String(item.entityId || item.data?.entityId || '') === key)
      .map((item) => String(item.id)));
    if (!nodeIds.size) return;
    const nextNodes = nodesRef.current.filter((item) => !nodeIds.has(String(item.id)));
    const nextEdges = edgesRef.current.filter((edge) => !nodeIds.has(String(edge.source)) && !nodeIds.has(String(edge.target)));
    nodesRef.current = nextNodes;
    edgesRef.current = nextEdges;
    setNodes(nextNodes);
    setEdges(nextEdges);
    markDirty();
    setGraphRevision((value) => value + 1);
  }, [markDirty, setEdges, setNodes]);

  const deleteEntity = React.useCallback(async (node) => {
    if (!node) return;
    const entity = node.data?.entity;
    const label = node.type === 'course' ? 'курс' : 'задание';
    const ok = await notify.confirm({ title: `Удалить ${label}?`, message: entity?.title || 'Действие необратимо.', okText: 'Удалить', cancelText: 'Отмена' });
    if (!ok) return;
    try {
      if (node.type === 'course') await deleteCourse(node.entityId);
      else await deleteAssignment(node.entityId);
      removeEntityFromMap(node.entityId);
      if (node.type !== 'course') setAssignments((current) => current.filter((item) => String(item.id) !== String(node.entityId)));
      await onRefreshCourseData?.();
      notify.success(node.type === 'course' ? 'Курс удалён' : 'Задание удалено');
    } catch (error) {
      notify.error(getApiErrorMessage(error, `Не удалось удалить ${label}`));
    }
  }, [notify, onRefreshCourseData, removeEntityFromMap, setAssignments]);

  const save = React.useCallback(async (options = {}) => {
    if (!editorMode || !rootId) return false;
    if (!dirtyRef.current) return true;
    const quietSuccess = Boolean(options?.quietSuccess);
    try {
      const document = serializeCourseMap(nodesRef.current, edgesRef.current, viewportRef.current);
      const saved = await saveCourseMap(rootId, Number(recordRef.current.version || 0), document);
      setRecord(saved);
      recordRef.current = saved;
      setDirty(false);
      dirtyRef.current = false;
      setServerChanged(false);
      setCourseMapSessionState(rootId, {
        version: saved.version || 0,
        dirty: false,
        aliases: cacheCourseIds,
        document,
      }, sessionOptions);
      writeCourseMapLocalCache({
        courseId: rootId,
        rootCourseId: saved.rootCourseId || rootId,
        editorMode: true,
        userId: currentUserId,
        aliases: cacheCourseIds,
        mapRecord: { ...saved, document },
        assignments,
        courses: visibleCourses,
        dirty: false,
      });
      clearCourseMapLocalCache({ courseId: rootId, editorMode: false, userId: currentUserId });
      if (!quietSuccess) notify.success('Карта сохранена');
      return true;
    } catch (error) {
      if (error?.response?.status === 409) {
        setServerChanged(true);
        notify.warn('Карту уже изменил другой редактор. Ваши локальные изменения сохранены на экране.');
        return false;
      }
      notify.error(getApiErrorMessage(error, 'Не удалось сохранить карту'));
      return false;
    }
  }, [assignments, cacheCourseIds, currentUserId, editorMode, notify, rootId, sessionOptions]);

  React.useEffect(() => {
    const requestKey = String(graphImportRequest?.key || '');
    if (!editorMode || loading || !requestKey || appliedGraphImportRef.current === requestKey) return;

    const result = applyTaskGraphImport({
      nodes: nodesRef.current,
      edges: edgesRef.current,
      taskGraph: graphImportRequest?.taskGraph,
      taskMappings: graphImportRequest?.taskMappings,
      assignments,
      courseId: course?.id || rootId,
    });
    if (result.pending) return;

    appliedGraphImportRef.current = requestKey;
    if (result.error === 'cycle') {
      notify.error('Импортированные связи создают цикл вместе с текущей картой.');
      onGraphImportComplete?.(requestKey);
      return;
    }

    const nextEdges = decorateEdges(result.edges);
    edgesRef.current = nextEdges;
    const nextNodes = decorateNodes(result.nodes);
    nodesRef.current = nextNodes;
    setEdges(nextEdges);
    setNodes(nextNodes);
    markDirty();
    setGraphRevision((value) => value + 1);
    persistSession(true);

    window.requestAnimationFrame(() => {
      const importedIds = new Set(result.importedNodeIds);
      const imported = nextNodes.filter((node) => importedIds.has(String(node.id)));
      if (!imported.length) return;
      try { flow.fitView({ nodes: imported, padding: 0.55, duration: 380, maxZoom: 1.05 }); } catch {}
    });

    void (async () => {
      const saved = await save({ quietSuccess: true });
      if (saved) {
        const unplacedCount = result.unplacedAssignmentIds.length;
        const unplacedText = unplacedCount ? `, вне карты: ${unplacedCount}` : '';
        notify.success(`Граф применён: ${result.connectionCount} связей${unplacedText}`);
      }
      onGraphImportComplete?.(requestKey);
    })();
  }, [assignments, course?.id, decorateEdges, decorateNodes, editorMode, flow, graphImportRequest, loading, markDirty, notify, onGraphImportComplete, persistSession, rootId, save, setEdges, setNodes]);

  const copySelection = React.useCallback(() => {
    if (!editorMode) return false;
    const selected = nodesRef.current.filter((node) => node.selected && !(node.type === 'course' && String(node.entityId || node.data?.entityId || '') === rootId));
    if (!selected.length) return false;
    const selectedIds = new Set(selected.map((node) => String(node.id)));
    copyBufferRef.current = {
      nodes: selected.map((node) => ({
        id: String(node.id),
        type: String(node.type),
        entityId: String(node.entityId || node.data?.entityId || ''),
        position: { x: Number(node.position?.x) || 0, y: Number(node.position?.y) || 0 },
        settings: node.settings || node.data?.settings || undefined,
      })),
      edges: edgesRef.current
        .filter((edge) => selectedIds.has(String(edge.source)) && selectedIds.has(String(edge.target)))
        .map((edge) => ({
          id: String(edge.id),
          source: String(edge.source),
          target: String(edge.target),
          sourceHandle: edge.sourceHandle || 'out',
          targetHandle: edge.targetHandle || 'in',
          settings: edge.settings && typeof edge.settings === 'object' ? { ...edge.settings } : undefined,
        })),
    };
    pasteSequenceRef.current = 0;
    notify.info(`Скопировано узлов: ${selected.length}`);
    return true;
  }, [editorMode, notify, rootId]);

  const pasteSelection = React.useCallback(() => {
    if (!editorMode || !courseCanEdit) return false;
    const buffer = copyBufferRef.current;
    if (!buffer?.nodes?.length) return false;

    pasteSequenceRef.current += 1;
    const serial = pasteSequenceRef.current;
    const stamp = Date.now().toString(36);
    const offset = 34 * serial;
    const idMap = new Map();
    const rawClones = buffer.nodes.map((node, index) => {
      const id = `copy:${stamp}:${serial}:${index}:${Math.random().toString(36).slice(2, 7)}`;
      idMap.set(String(node.id), id);
      return {
        id,
        type: node.type,
        entityId: node.entityId,
        position: { x: node.position.x + offset, y: node.position.y + offset },
        settings: node.settings,
        selected: true,
      };
    });

    const currentEdges = edgesRef.current.map((edge) => ({ ...edge, selected: false }));
    const pastedEdges = buffer.edges.map((edge, index) => {
      const pasted = {
        id: `edge:copy:${stamp}:${serial}:${index}:${Math.random().toString(36).slice(2, 7)}`,
        source: idMap.get(String(edge.source)),
        target: idMap.get(String(edge.target)),
        sourceHandle: edge.sourceHandle || 'out',
        targetHandle: edge.targetHandle || 'in',
        settings: edge.settings && typeof edge.settings === 'object' ? { ...edge.settings } : { hiddenEffect: 'inherit', sequentialEffect: 'inherit' },
      };
      return { ...pasted, ...edgeStyle(true, pasted) };
    }).filter((edge) => edge.source && edge.target);
    const nextEdges = [...currentEdges, ...pastedEdges];
    edgesRef.current = nextEdges;

    const currentNodes = nodesRef.current.map((node) => ({ ...node, selected: false }));
    const nextNodes = decorateNodes([...currentNodes, ...rawClones]);
    nodesRef.current = nextNodes;
    setNodes(nextNodes);
    setEdges(nextEdges);
    markDirty();
    setGraphRevision((value) => value + 1);
    persistSession(true);
    notify.success(`Вставлено узлов: ${rawClones.length}`);
    return true;
  }, [courseCanEdit, decorateNodes, editorMode, markDirty, notify, persistSession, setEdges, setNodes]);

  const shouldHandleMapSaveShortcut = React.useCallback(() => !document.querySelector('.course-map-route-overlay, .tf-modal-backdrop'), []);
  useSaveShortcut(save, { enabled: editorMode, shouldHandle: shouldHandleMapSaveShortcut });

  React.useEffect(() => {
    if (!editorMode) return undefined;
    const onKeyDown = (event) => {
      if (document.querySelector('.course-map-route-overlay')) return;
      if (isEditableShortcutTarget(event.target)) return;
      const mod = event.ctrlKey || event.metaKey;
      const key = String(event.key || '').toLowerCase();

      if (mod && key === 'c') {
        if (copySelection()) event.preventDefault();
        return;
      }
      if (mod && key === 'v') {
        if (pasteSelection()) event.preventDefault();
        return;
      }
      if (!mod && (event.key === 'Delete' || event.key === 'Backspace')) {
        const selectedEdgeIds = new Set(edgesRef.current.filter((edge) => edge.selected).map((edge) => String(edge.id)));
        if (selectedEdgeIds.size && removeEdges(selectedEdgeIds)) event.preventDefault();
      }
    };
    window.addEventListener('keydown', onKeyDown, true);
    return () => window.removeEventListener('keydown', onKeyDown, true);
  }, [copySelection, editorMode, pasteSelection, removeEdges, save]);

  const reloadServerVersion = React.useCallback(async () => {
    if (dirtyRef.current) {
      const ok = await notify.confirm({ title: 'Отбросить локальные изменения?', message: 'Будет загружена последняя сохранённая версия карты.', okText: 'Загрузить', cancelText: 'Оставить мои' });
      if (!ok) return;
    }
    clearCourseMapSessionState(rootId, sessionOptions);
    clearCourseMapLocalCache({ courseId: rootId, editorMode, userId: currentUserId });
    await loadMap({ preferSession: false });
  }, [currentUserId, editorMode, loadMap, notify, rootId, sessionOptions]);

  const documentNow = React.useMemo(() => serializeCourseMap(nodes, edges, viewportRef.current), [edges, nodes]);
  const unplaced = React.useMemo(() => findUnplacedEntities(documentNow, visibleCourses, assignments), [assignments, documentNow, visibleCourses]);

  const buildPlacedNode = React.useCallback((entry, position) => {
    const entity = entry.entity;
    const id = entry.kind === 'course' ? courseNodeId(entity.id) : assignmentNodeId(entity.id);
    return {
      id,
      type: entry.type,
      entityId: String(entity.id),
      position,
      selected: true,
      data: {
        entityId: String(entity.id),
        entity,
        progress: entry.kind === 'course' ? { total: 0, solved: 0, percent: 0 } : null,
        editorMode,
        onOpen: entry.kind === 'course' ? () => focusBranch(id) : () => openAssignment(entity.id),
        onFocus: entry.kind === 'course' ? () => focusBranch(id) : () => focusNode(id),
        onEdit: entry.kind === 'course' ? () => editCourse(entity.id) : () => editAssignment(entity.id),
      },
    };
  }, [editAssignment, editCourse, editorMode, focusBranch, focusNode, openAssignment]);

  const dropUnplaced = React.useCallback((entry, requestedPosition) => {
    if (!entry || !editorMode) return;
    const current = nodesRef.current.map((node) => ({ ...node, selected: false }));
    const position = findOpenMapPosition(requestedPosition, current);
    const added = buildPlacedNode(entry, position);
    const next = [...current, added];
    nodesRef.current = next;
    setNodes(next);
    markDirty();
    setGraphRevision((value) => value + 1);
    persistSession(true);
    if (unplaced.length <= 1) setUnplacedOpen(false);
  }, [buildPlacedNode, editorMode, markDirty, persistSession, setNodes, unplaced.length]);

  const addUnplacedNearCursor = React.useCallback((entry) => {
    if (!entry || !editorMode) return;
    const rect = canvasRef.current?.getBoundingClientRect();
    const remembered = lastMapPointerRef.current;
    const screenPoint = remembered && rect
      && remembered.x >= rect.left && remembered.x <= rect.right
      && remembered.y >= rect.top && remembered.y <= rect.bottom
      ? { x: remembered.x + 24, y: remembered.y + 24 }
      : rect
        ? { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 }
        : { x: window.innerWidth / 2, y: window.innerHeight / 2 };
    dropUnplaced(entry, flow.screenToFlowPosition(screenPoint));
  }, [dropUnplaced, editorMode, flow]);

  if (loading) return <div className="course-map-loading">Загрузка карты…</div>;

  return (
    <div
      className={`course-map-shell${editorMode ? ' is-editor' : ' is-viewer'}${interacting || context.open ? ' is-interacting' : ''}`}
      ref={shellRef}
      style={mapHeight ? { height: `${mapHeight}px` } : undefined}
      data-taskforge-agent-role="course-map"
      data-taskforge-ready="true"
    >
      <div className="course-map-toolbar">
        <div className="course-map-toolbar-left">
          <button
            type="button"
            className="course-map-toolbar-icon"
            title={course?.parentCourseId ? 'Назад к родительскому курсу' : 'Назад к курсам'}
            aria-label={course?.parentCourseId ? 'Назад к родительскому курсу' : 'Назад к курсам'}
            onClick={() => { rememberBeforeNavigate(); nav(course?.parentCourseId ? `/course/${course.parentCourseId}` : '/courses'); }}
          >
            <ArrowLeft size={16} />
          </button>

          <div className={`course-map-search${searchOpen ? ' is-open' : ''}`}>
            <button
              type="button"
              className="course-map-toolbar-icon course-map-search-trigger"
              title="Поиск по карте"
              aria-label="Поиск по карте"
              onClick={() => setSearchOpen((value) => !value)}
            >
              <Search size={16} />
            </button>
            {searchOpen ? (
              <div className="course-map-search-field">
                <input
                  ref={searchInputRef}
                  value={query}
                  onChange={(event) => onQueryChange?.(event.target.value)}
                  onKeyDown={(event) => {
                    if (event.key === 'Escape') {
                      event.stopPropagation();
                      if (query) onQueryChange?.('');
                      else setSearchOpen(false);
                    }
                  }}
                  placeholder="Найти узел…"
                  aria-label="Поиск по карте курса"
                />
                {query ? (
                  <button type="button" className="course-map-search-clear" title="Очистить поиск" aria-label="Очистить поиск" onClick={() => onQueryChange?.('')}>
                    <X size={13} />
                  </button>
                ) : null}
              </div>
            ) : null}
          </div>

          {editorMode ? <span className={`course-map-dirty-dot${dirty ? ' is-dirty' : ''}`} title={dirty ? 'Есть несохранённые изменения' : 'Все изменения сохранены'} /> : null}
          {serverChanged ? <button type="button" className="course-map-server-changed" onClick={reloadServerVersion}><RotateCcw size={13} /> новая версия</button> : null}
        </div>
        <div className="course-map-toolbar-right">
          {editorMode && unplaced.length > 0 ? (
            <div className="course-map-unplaced" ref={unplacedRef}>
              <button
                type="button"
                className={`course-map-unplaced-trigger${unplacedOpen ? ' is-open' : ''}`}
                onClick={() => setUnplacedOpen((value) => !value)}
                aria-expanded={unplacedOpen}
              >
                Не на карте · <strong>{unplaced.length}</strong>
              </button>
              {unplacedOpen ? (
                <div className="course-map-unplaced-popover">
                  <div className="course-map-unplaced-head">
                    <div className="course-map-popover-title">Не на карте</div>
                    <button type="button" className="course-map-unplaced-close" onClick={() => setUnplacedOpen(false)} aria-label="Закрыть"><X size={14} /></button>
                  </div>
                  <div className="course-map-unplaced-list">
                    {unplaced.map((entry) => (
                      <div
                        key={`${entry.kind}:${entry.entity.id}`}
                        className="course-map-unplaced-row"
                      >
                        <span className="course-map-unplaced-kind">{entry.kind === 'course' ? 'Курс' : 'Задание'}</span>
                        <span className="course-map-unplaced-title" title={entry.entity.title || 'Без названия'}>{entry.entity.title || 'Без названия'}</span>
                        <button
                          type="button"
                          className="course-map-unplaced-add"
                          title="Добавить рядом с курсором"
                          aria-label={`Добавить «${entry.entity.title || 'Без названия'}» на карту`}
                          onClick={() => addUnplacedNearCursor(entry)}
                        >
                          <Plus size={14} />
                        </button>
                      </div>
                    ))}
                  </div>
                </div>
              ) : null}
            </div>
          ) : null}

          {editorMode ? (
            <div className="course-map-presence">
              <button type="button" className="course-map-presence-trigger" aria-label={`Редакторов в карте: ${Math.max(1, presence.length)}`}>
                <span className="course-map-presence-dot" />{presence.length > 1 ? <span>{presence.length}</span> : null}
              </button>
              <div className="course-map-presence-popover">
                <div className="course-map-popover-title">Сейчас в редакторе</div>
                {(presence.length ? presence : [{ userId: currentUserId, displayName: userDisplayName(user), isDirty: dirty }]).map((row) => (
                  <div className="course-map-presence-row" key={row.userId || row.displayName}>
                    <span className="course-map-presence-dot" />
                    <span className="course-map-presence-name">{row.displayName || 'Пользователь'}{String(row.userId || '') === currentUserId ? ' · вы' : ''}</span>
                    {row.isDirty ? <span className="course-map-presence-dirty">изменяет</span> : null}
                  </div>
                ))}
              </div>
            </div>
          ) : null}

          {editorMode && onShowGrid ? <button type="button" className="course-map-toolbar-icon" title="Открыть карточки" aria-label="Открыть карточки" onClick={onShowGrid}><LayoutGrid size={16} /></button> : null}
          {editorMode && onExportJson ? <button type="button" className="course-map-toolbar-icon" title="Экспорт JSON" aria-label="Экспорт JSON" disabled={exportBusy} onClick={onExportJson}><Download size={16} /></button> : null}
          {editorMode && onImportJson ? <button type="button" className="course-map-toolbar-icon" title="Импорт JSON" aria-label="Импорт JSON" onClick={onImportJson}><FileJson size={16} /></button> : null}
          {editorMode ? <Button className="course-map-save-button" onClick={save} disabled={!dirty} title="Сохранить карту (Ctrl+S)"><Save size={15} /> {dirty ? 'Сохранить' : 'Сохранено'}</Button> : null}
        </div>
      </div>

      <div
        className="course-map-canvas"
        ref={canvasRef}
        onPointerMoveCapture={(event) => {
          lastMapPointerRef.current = { x: event.clientX, y: event.clientY };
        }}
      >
        <ReactFlow
          nodes={nodes}
          edges={edges}
          nodeTypes={NODE_TYPES}
          edgeTypes={EDGE_TYPES}
          onNodesChange={onNodesChange}
          onEdgesChange={onEdgesChange}
          onConnect={onConnect}
          onConnectStart={() => setInteracting(true)}
          onConnectEnd={() => setInteracting(false)}
          onNodeDragStart={() => setInteracting(true)}
          onNodeDragStop={() => { setInteracting(false); if (!editorMode) persistSession(false); }}
          onEdgeDoubleClick={onEdgeDoubleClick}
          onEdgeContextMenu={onEdgeContextMenu}
          onPaneContextMenu={onPaneContextMenu}
          onPaneClick={() => setUnplacedOpen(false)}
          onNodeContextMenu={onNodeContextMenu}
          onNodeDoubleClick={(event, node) => {
            event.preventDefault();
            if (node.type === 'locked') return;
            if (node.type === 'course') focusBranch(node.id);
            else openAssignment(node.entityId);
          }}
          onMoveEnd={(_, viewport) => { viewportRef.current = viewport; if (!editorMode) persistSession(false); }}
          nodesDraggable
          nodesConnectable={editorMode}
          elementsSelectable
          deleteKeyCode={null}
          fitView={false}
          minZoom={0.12}
          maxZoom={1.8}
          selectionOnDrag={editorMode}
          selectionMode={SelectionMode.Partial}
          panOnDrag={editorMode ? [1] : true}
          panActivationKeyCode="Space"
          multiSelectionKeyCode={["Shift", "Meta", "Control"]}
          selectionKeyCode={null}
          proOptions={{ hideAttribution: true }}
        >
          <Background gap={18} size={1.15} className="course-map-background" />
          <Controls showInteractive={false} className="course-map-controls" />
        </ReactFlow>
      </div>

      <ContextMenu open={context.open} x={context.x} y={context.y} onClose={closeContext} ariaLabel="Действия карты курса">
        {context.edge ? (
          <>
            <ContextMenuLabel>Эффекты стрелки</ContextMenuLabel>
            <ContextMenuItem icon={Eye} checked={edgeAccessSettings(context.edge).hiddenEffect === 'inherit' && edgeAccessSettings(context.edge).sequentialEffect === 'inherit'} onClick={() => { const edge = context.edge; updateEdgeAccessSettings(edge.id, { hiddenEffect: 'inherit', sequentialEffect: 'inherit' }); closeContext(); }}>Обычная</ContextMenuItem>
            <ContextMenuSeparator />
            <ContextMenuLabel>Скрытие</ContextMenuLabel>
            <ContextMenuItem icon={LockKeyhole} checked={edgeAccessSettings(context.edge).hiddenEffect === 'start'} onClick={() => { const edge = context.edge; updateEdgeAccessSettings(edge.id, { hiddenEffect: 'start' }); }}>Начать скрытие</ContextMenuItem>
            <ContextMenuItem icon={RotateCcw} checked={edgeAccessSettings(context.edge).hiddenEffect === 'stop'} onClick={() => { const edge = context.edge; updateEdgeAccessSettings(edge.id, { hiddenEffect: 'stop' }); }}>Закончить скрытие</ContextMenuItem>
            <ContextMenuItem icon={Eye} checked={edgeAccessSettings(context.edge).hiddenEffect === 'inherit'} onClick={() => { const edge = context.edge; updateEdgeAccessSettings(edge.id, { hiddenEffect: 'inherit' }); }}>Наследовать скрытие</ContextMenuItem>
            <ContextMenuSeparator />
            <ContextMenuLabel>По одному</ContextMenuLabel>
            <ContextMenuItem icon={ListOrdered} checked={edgeAccessSettings(context.edge).sequentialEffect === 'start'} onClick={() => { const edge = context.edge; updateEdgeAccessSettings(edge.id, { sequentialEffect: 'start' }); }}>Начать</ContextMenuItem>
            <ContextMenuItem icon={RotateCcw} checked={edgeAccessSettings(context.edge).sequentialEffect === 'stop'} onClick={() => { const edge = context.edge; updateEdgeAccessSettings(edge.id, { sequentialEffect: 'stop' }); }}>Закончить</ContextMenuItem>
            <ContextMenuItem icon={Eye} checked={edgeAccessSettings(context.edge).sequentialEffect === 'inherit'} onClick={() => { const edge = context.edge; updateEdgeAccessSettings(edge.id, { sequentialEffect: 'inherit' }); }}>Наследовать</ContextMenuItem>
            <ContextMenuSeparator />
            <ContextMenuItem icon={Unlink2} danger onClick={() => { const edge = context.edge; closeContext(); removeEdges([String(edge.id)]); }}>Разорвать связь</ContextMenuItem>
          </>
        ) : context.node ? (
          <>
            <ContextMenuLabel>{context.node.type === 'course' ? 'Курс' : 'Задание'}</ContextMenuLabel>
            <ContextMenuItem icon={Eye} onClick={() => { const node = context.node; closeContext(); if (node.type === 'course') focusBranch(node.id); else openAssignment(node.entityId); }}>Открыть</ContextMenuItem>
            {editorMode && courseCanEdit ? (
              <>
                <ContextMenuItem icon={Pencil} onClick={() => { const node = context.node; closeContext(); if (node.type === 'course') editCourse(node.entityId); else editAssignment(node.entityId); }}>Редактировать</ContextMenuItem>
                <ContextMenuSeparator />
                <ContextMenuItem icon={X} disabled={context.node.type === 'course' && String(context.node.entityId) === rootId} onClick={() => { const node = context.node; closeContext(); removeNodeFromMap(node); }}>Убрать с карты</ContextMenuItem>
                <ContextMenuItem icon={Trash2} disabled={context.node.type === 'course' && String(context.node.entityId) === rootId} danger onClick={() => { const node = context.node; closeContext(); void deleteEntity(node); }}>Удалить полностью</ContextMenuItem>
              </>
            ) : null}
          </>
        ) : (
          <>
            <ContextMenuLabel>Добавить узел</ContextMenuLabel>
            <ContextMenuItem icon={FolderTree} onClick={() => void createMapNode('course', context.flowPosition)}>Курс</ContextMenuItem>
            <ContextMenuItem icon={FileCode2} onClick={() => void createMapNode('code-test', context.flowPosition)}>Code test</ContextMenuItem>
            <ContextMenuItem icon={ListChecks} onClick={() => void createMapNode('test', context.flowPosition)}>Тест</ContextMenuItem>
            <ContextMenuItem icon={ImageIcon} onClick={() => void createMapNode('image-code', context.flowPosition)}>Картинки / код</ContextMenuItem>
            <ContextMenuItem icon={Sigma} onClick={() => void createMapNode('math', context.flowPosition)}>Математика</ContextMenuItem>
          </>
        )}
      </ContextMenu>
    </div>
  );
}

export default function CourseFlowEditor(props) {
  return (
    <ReactFlowProvider>
      <CourseMapInner {...props} />
    </ReactFlowProvider>
  );
}
