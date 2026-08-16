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
  useNodesInitialized,
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
import { buildDefaultAssignmentPayload, isAssignmentSolved, previewAssignmentDescription } from '../courseAssignmentsModel';
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
import { clearCourseMapLocalCache, courseMapCacheNeedsFullRevalidation, readCourseMapLocalCache, readCourseMapLocalCacheAsync, writeCourseMapLocalCache } from '../courseMapLocalCache';
import { courseMapConsole, courseMapConsoleGraph, hasLearnerSyntheticArtifacts } from '../courseMapDebug';
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

function isBrowserReloadNavigation() {
  if (typeof window === 'undefined' || typeof performance === 'undefined') return false;
  try {
    const navigation = performance.getEntriesByType?.('navigation')?.[0];
    if (navigation?.type) return navigation.type === 'reload';
    return Number(performance.navigation?.type) === 1;
  } catch {
    return false;
  }
}

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
  if (String(edge?.className || '').includes('course-map-edge-layout-pending')) classes.push('course-map-edge-layout-pending');
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

function courseMapMode(editorMode) {
  return editorMode ? 'editor' : 'learner';
}

function documentHasLearnerSyntheticArtifacts(document) {
  if (!document || typeof document !== 'object') return false;
  return hasLearnerSyntheticArtifacts(document.nodes || [], document.edges || []);
}

function sourceMatchesMode(source, mode, viewKey) {
  return Boolean(source && source.mode === mode && source.viewKey === viewKey);
}


function learnerEntryFocusCandidate(nodes, edges, rootCourseId) {
  const rows = Array.isArray(nodes) ? nodes.filter(Boolean) : [];
  const links = Array.isArray(edges) ? edges.filter(Boolean) : [];
  if (!rows.length) return { targetId: '', rootId: '', fallbackId: '' };

  const byId = new Map(rows.map((node) => [String(node?.id || ''), node]));
  const outgoing = new Map(rows.map((node) => [String(node?.id || ''), []]));
  const incomingCount = new Map(rows.map((node) => [String(node?.id || ''), 0]));

  for (const edge of links) {
    const source = String(edge?.source || '');
    const target = String(edge?.target || '');
    if (!byId.has(source) || !byId.has(target) || source === target) continue;
    outgoing.get(source).push(target);
    incomingCount.set(target, (incomingCount.get(target) || 0) + 1);
  }

  const wantedCourseId = String(rootCourseId || '');
  const rootNode = rows.find((node) => node?.type === 'course'
    && String(node?.entityId || node?.data?.entityId || '') === wantedCourseId)
    || rows.find((node) => (incomingCount.get(String(node?.id || '')) || 0) === 0)
    || rows[0];
  const rootNodeId = String(rootNode?.id || '');
  if (!rootNodeId) return { targetId: '', rootId: '', fallbackId: '' };

  const queue = [rootNodeId];
  const visited = new Set();
  let fallbackId = rootNodeId;

  while (queue.length) {
    const currentId = String(queue.shift() || '');
    if (!currentId || visited.has(currentId)) continue;
    visited.add(currentId);
    const node = byId.get(currentId);
    if (!node) continue;

    if (node.type === 'locked') continue;

    if (node.type !== 'course') {
      const entity = node?.data?.entity || null;
      if (entity && !isAssignmentSolved(entity)) {
        return { targetId: currentId, rootId: rootNodeId, fallbackId: currentId };
      }
      if (entity) fallbackId = currentId;
    }

    for (const nextId of outgoing.get(currentId) || []) {
      if (!visited.has(nextId)) queue.push(nextId);
    }
  }

  return { targetId: '', rootId: rootNodeId, fallbackId };
}

function centerLearnerEntryNode(flow, nodeId, { duration = 0, zoom = 0.92 } = {}) {
  const node = flow.getNode(String(nodeId || ''));
  if (!node) return false;
  const position = node.positionAbsolute || node.position || { x: 0, y: 0 };
  const width = Number(node.width || node.measured?.width) || 300;
  const height = Number(node.height || node.measured?.height) || 120;
  const centerX = (Number(position.x) || 0) + width / 2;
  const centerY = (Number(position.y) || 0) + height / 2;
  try {
    flow.setCenter(centerX, centerY, { zoom, duration });
    return true;
  } catch {
    return false;
  }
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
  const nodesInitialized = useNodesInitialized({ includeHiddenNodes: true });
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
  const [sceneReady, setSceneReady] = React.useState(false);
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
  const loadedViewRef = React.useRef('');
  const activeViewRef = React.useRef('');
  const activeModeRef = React.useRef('learner');
  const previousViewRef = React.useRef('');
  const graphSourceRef = React.useRef({ mode: '', source: 'empty', viewKey: '' });
  const modeTransitionRef = React.useRef(true);
  const autosaveTimerRef = React.useRef(null);
  const editorTreeFetchRef = React.useRef({ rootId: '', promise: null });
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
  const pendingLayoutNodeIdsRef = React.useRef(new Set());
  const pendingLayoutEdgeIdsRef = React.useRef(new Set());
  const streamAbortRef = React.useRef(null);
  const learnerPersistTimerRef = React.useRef(null);
  const persistLearnerGraphRef = React.useRef(null);
  const unplacedDebugRef = React.useRef('');
  const learnerEntryFocusRef = React.useRef({ viewKey: '', lastTargetId: '', fallbackCentered: false, completed: false });
  const learnerProjectionSettledRef = React.useRef(false);

  const rootId = String(course?.id || '');
  const modeName = courseMapMode(editorMode);
  const viewKey = `${rootId}:${modeName}`;
  activeViewRef.current = viewKey;
  activeModeRef.current = modeName;
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
  const groupRestrictedCourseIdsForStudents = React.useMemo(() => {
    const byId = new Map(visibleCourses.map((item) => [String(item?.id || ''), item]));
    const restricted = new Set();
    for (const item of visibleCourses) {
      const originId = String(item?.id || '');
      let current = item;
      const seen = new Set();
      while (current?.id && !seen.has(String(current.id))) {
        seen.add(String(current.id));
        if (current.isHiddenFromStudents) break;
        if (current.isPublic === false) restricted.add(originId);
        current = current.parentCourseId ? byId.get(String(current.parentCourseId)) : null;
      }
    }
    return restricted;
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

  React.useLayoutEffect(() => {
    const previousView = previousViewRef.current;
    if (previousView === viewKey) return;

    courseMapConsole(previousView ? 'MODE_SWITCH' : 'MODE_INIT', {
      previousView: previousView || null,
      nextView: viewKey,
      rootCourseId: rootId,
      mode: modeName,
      nodesBefore: nodesRef.current.length,
      edgesBefore: edgesRef.current.length,
      dirtyBefore: dirtyRef.current,
      sourceBefore: graphSourceRef.current,
    });

    previousViewRef.current = viewKey;
    setSceneReady(false);
    modeTransitionRef.current = true;
    graphSourceRef.current = { mode: '', source: 'mode-transition', viewKey };
    loadedViewRef.current = '';
    loadRequestRef.current += 1;
    streamAbortRef.current?.abort?.();
    streamAbortRef.current = null;
    pendingLearnerEdgesRef.current = [];
    learnerEntryFocusRef.current = { viewKey, lastTargetId: '', fallbackCentered: false, completed: false };
    learnerProjectionSettledRef.current = false;
    pendingLayoutNodeIdsRef.current.clear();
    pendingLayoutEdgeIdsRef.current.clear();
    window.clearTimeout(learnerPersistTimerRef.current);
    learnerPersistTimerRef.current = null;
    window.clearTimeout(autosaveTimerRef.current);
    autosaveTimerRef.current = null;
    setContext((current) => ({ ...current, open: false }));

    if (previousView) {
      nodesRef.current = [];
      edgesRef.current = [];
      assignmentsRef.current = [];
      mapCoursesRef.current = course?.id ? [course] : [];
      courseProgressRef.current = new Map();
      projectionTokenRef.current = '';
      setNodes([]);
      setEdges([]);
      setAssignments([]);
      setMapCourses(mapCoursesRef.current);
      setCourseProgressByNode(new Map());
      setLoading(true);
    }
  }, [course, modeName, rootId, setEdges, setNodes, viewKey]);

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
    if (sceneReady || loading || !nodes.length || !nodesInitialized) return undefined;
    const invalidNodes = nodes.filter((node) => !Number.isFinite(Number(node?.position?.x)) || !Number.isFinite(Number(node?.position?.y)));
    if (invalidNodes.length) {
      courseMapConsole('LAYOUT_WAIT', {
        viewKey,
        reason: 'invalid-position',
        invalidNodeIds: invalidNodes.slice(0, 12).map((node) => String(node?.id || '')),
        invalidCount: invalidNodes.length,
        nodes: nodes.length,
      }, 'error');
      return undefined;
    }

    courseMapConsole('LAYOUT_WAIT', { viewKey, reason: 'react-flow-initializing', nodes: nodes.length, edges: edges.length });
    let secondFrame = 0;
    const firstFrame = window.requestAnimationFrame(() => {
      secondFrame = window.requestAnimationFrame(() => {
        if (activeViewRef.current !== viewKey) return;
        setSceneReady(true);
        courseMapConsole('LAYOUT_READY', {
          viewKey,
          mode: modeName,
          nodes: nodesRef.current.length,
          edges: edgesRef.current.length,
          viewport: viewportRef.current,
        });
      });
    });
    return () => {
      window.cancelAnimationFrame(firstFrame);
      if (secondFrame) window.cancelAnimationFrame(secondFrame);
    };
  }, [edges.length, loading, modeName, nodes, nodesInitialized, sceneReady, viewKey]);

  React.useEffect(() => {
    if (!nodesInitialized || pendingLayoutNodeIdsRef.current.size === 0) return undefined;
    const expectedView = viewKey;
    let secondFrame = 0;
    const firstFrame = window.requestAnimationFrame(() => {
      secondFrame = window.requestAnimationFrame(() => {
        if (activeViewRef.current !== expectedView || !nodesInitialized) return;
        const nodeIds = new Set(pendingLayoutNodeIdsRef.current);
        const edgeIds = new Set(pendingLayoutEdgeIdsRef.current);
        if (!nodeIds.size) return;
        pendingLayoutNodeIdsRef.current.clear();
        pendingLayoutEdgeIdsRef.current.clear();
        setNodes((current) => {
          const next = current.map((node) => nodeIds.has(String(node.id))
            ? { ...node, className: String(node.className || '').replace(/\bcourse-map-node-layout-pending\b/g, '').replace(/\s+/g, ' ').trim() }
            : node);
          nodesRef.current = next;
          return next;
        });
        setEdges((current) => {
          const next = current.map((edge) => edgeIds.has(String(edge.id))
            ? { ...edge, className: String(edge.className || '').replace(/\bcourse-map-edge-layout-pending\b/g, '').replace(/\s+/g, ' ').trim() }
            : edge);
          edgesRef.current = next;
          return next;
        });
        courseMapConsole('LAYOUT_INCREMENT_READY', {
          viewKey: expectedView,
          nodes: nodeIds.size,
          edges: edgeIds.size,
        });
      });
    });
    return () => {
      window.cancelAnimationFrame(firstFrame);
      if (secondFrame) window.cancelAnimationFrame(secondFrame);
    };
  }, [nodesInitialized, setEdges, setNodes, viewKey]);

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

  const persistSession = React.useCallback((nextDirty = dirtyRef.current, options = {}) => {
    const reason = String(options?.reason || 'manual');
    const expectedViewKey = String(options?.expectedViewKey || viewKey);
    const source = graphSourceRef.current;

    if (!rootId || !nodesRef.current.length) {
      courseMapConsole('SESSION_SKIP', { reason, skip: 'empty-graph', expectedViewKey, activeView: activeViewRef.current, mode: modeName });
      return false;
    }
    if (activeViewRef.current !== expectedViewKey || activeModeRef.current !== modeName) {
      courseMapConsole('SESSION_SKIP', {
        reason,
        skip: 'stale-view-callback',
        expectedViewKey,
        activeView: activeViewRef.current,
        callbackMode: modeName,
        activeMode: activeModeRef.current,
      }, 'warn');
      return false;
    }
    if (modeTransitionRef.current) {
      courseMapConsole('SESSION_SKIP', { reason, skip: 'mode-transition', viewKey, mode: modeName, source });
      return false;
    }
    if (!sourceMatchesMode(source, modeName, viewKey)) {
      courseMapConsole('SESSION_SKIP', { reason, skip: 'source-mode-mismatch', viewKey, mode: modeName, source }, 'warn');
      return false;
    }
    if (editorMode && hasLearnerSyntheticArtifacts(nodesRef.current, edgesRef.current)) {
      courseMapConsoleGraph('SYNTHETIC_GUARD', {
        reason: 'editor-persist-blocked',
        viewKey,
        source,
        nodes: nodesRef.current,
        edges: edgesRef.current,
      }, 'error');
      return false;
    }

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
      sourceMode: modeName,
      source: source.source,
      document,
    }, sessionOptions);
    writeCourseMapLocalCache({
      courseId: rootId,
      rootCourseId: mapRecord.rootCourseId,
      editorMode,
      userId: currentUserId,
      aliases: cacheCourseIds,
      mapRecord,
      assignments: assignmentsRef.current,
      courses: mapCoursesRef.current.length ? mapCoursesRef.current : visibleCourses,
      dirty: dirtyValue,
    });
    courseMapConsoleGraph('SESSION_SAVE', {
      reason,
      viewKey,
      mode: modeName,
      source: source.source,
      dirty: dirtyValue,
      version: Number(recordRef.current.version || 0),
      nodes: nodesRef.current,
      edges: edgesRef.current,
    });
    return true;
  }, [cacheCourseIds, currentUserId, editorMode, modeName, rootId, sessionOptions, viewKey, visibleCourses]);

  const rememberBeforeNavigate = React.useCallback(() => persistSession(dirtyRef.current, { reason: 'before-navigate', expectedViewKey: viewKey }), [persistSession, viewKey]);

  React.useEffect(() => {
    window.clearTimeout(autosaveTimerRef.current);
    autosaveTimerRef.current = null;
    if (!rootId || !nodes.length) return undefined;
    const expectedViewKey = viewKey;
    autosaveTimerRef.current = window.setTimeout(() => {
      autosaveTimerRef.current = null;
      persistSession(dirtyRef.current, { reason: 'autosave', expectedViewKey });
    }, 420);
    return () => {
      window.clearTimeout(autosaveTimerRef.current);
      autosaveTimerRef.current = null;
    };
  }, [edges, nodes, persistSession, rootId, viewKey]);

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
    return rawNodes
      .filter((node) => !(editorMode && node?.type === 'locked'))
      .map((node) => {
      if (node.type === 'locked') {
        const settings = node.settings || node.data?.settings || {};
        const searchText = `${settings.title || ''} ${settings.requirement || ''}`.toLowerCase();
        const searchMatch = !q || searchText.includes(q);
        return {
          ...node,
          type: 'locked',
          className: `${searchMatch ? '' : 'course-map-search-dimmed'}${String(node.className || '').includes('course-map-node-revealed') ? ' course-map-node-revealed' : ''}${String(node.className || '').includes('course-map-node-layout-pending') ? ' course-map-node-layout-pending' : ''}`.trim(),
          data: { settings, editorMode: false, searchMatch },
        };
      }

      const isCourse = node.type === 'course';
      const rawEntity = isCourse ? idx.courseById.get(String(node.entityId)) : idx.assignmentById.get(String(node.entityId));
      const entity = isCourse && rawEntity
        ? {
            ...rawEntity,
            isHiddenForStudents: hiddenCourseIdsForStudents.has(String(rawEntity.id)),
            isGroupRestrictedForStudents: groupRestrictedCourseIdsForStudents.has(String(rawEntity.id)),
          }
        : rawEntity;
      const progress = isCourse ? (progressByCourse.get(String(node.id)) || { total: 0, solved: 0, percent: 0 }) : null;
      const searchText = `${entity?.title || ''} ${previewAssignmentDescription(entity?.description || '')} ${entity?.tags || ''}`.toLowerCase();
      const searchMatch = !q || searchText.includes(q);
      const visualType = isCourse ? 'course' : assignmentNodeType(entity?.type || node.type);
      const accessEffects = accessByNode.get(String(node.id)) || null;
      const revealClass = String(node.className || '').includes('course-map-node-revealed') ? ' course-map-node-revealed' : '';
      const layoutPendingClass = String(node.className || '').includes('course-map-node-layout-pending') ? ' course-map-node-layout-pending' : '';
      return {
        ...node,
        type: visualType,
        className: `${nodeAccessClassName(searchMatch, accessEffects, editorMode)}${revealClass}${layoutPendingClass}`.trim(),
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
  }, [editAssignment, editCourse, editorMode, focusBranch, focusNode, groupRestrictedCourseIdsForStudents, hiddenCourseIdsForStudents, openAssignment, query, rootId]);

  const decorateNodes = React.useCallback((rawNodes) => (
    decorateNodesWithSources(rawNodes, edgesRef.current, assignments, visibleCourses, courseProgressByNode)
  ), [assignments, courseProgressByNode, decorateNodesWithSources, visibleCourses]);

  const decorateEdges = React.useCallback((rawEdges) => rawEdges
    .filter((edge) => !(editorMode && edge?.settings?.synthetic === true))
    .map((edge) => ({ ...edge, ...edgeStyle(editorMode, edge) })), [editorMode]);

  const applyMapPayload = React.useCallback((mapRecord, treeAssignments, {
    preferSession = true,
    fromCache = false,
    cachedState = null,
    source = '',
  } = {}) => {
    if (activeViewRef.current !== viewKey || activeModeRef.current !== modeName) {
      courseMapConsole('SOURCE_APPLY_SKIP', {
        source: source || (fromCache ? `${modeName}-cache` : `${modeName}-server`),
        callbackView: viewKey,
        activeView: activeViewRef.current,
        callbackMode: modeName,
        activeMode: activeModeRef.current,
      }, 'warn');
      return false;
    }
    const sourceLabel = source || (fromCache ? `${modeName}-cache` : `${modeName}-server`);
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
    let session = preferSession ? getCourseMapSessionState(rootId, sessionOptions) : null;
    if (editorMode && session?.document && documentHasLearnerSyntheticArtifacts(session.document)) {
      courseMapConsoleGraph('SYNTHETIC_GUARD', {
        reason: 'discard-editor-session',
        rootCourseId: rootId,
        source: session?.source || 'session',
        nodes: session.document.nodes || [],
        edges: session.document.edges || [],
      }, 'error');
      clearCourseMapSessionState(rootId, sessionOptions);
      session = null;
    }
    const cachedDraftDocument = editorMode && cachedState?.dirty && cachedState?.mapRecord?.document
      ? cachedState.mapRecord.document
      : null;
    const cachedDraftValid = !cachedDraftDocument || !documentHasLearnerSyntheticArtifacts(cachedDraftDocument);
    if (editorMode && cachedDraftDocument && !cachedDraftValid) {
      courseMapConsoleGraph('SYNTHETIC_GUARD', {
        reason: 'discard-editor-local-cache',
        rootCourseId: rootId,
        nodes: cachedDraftDocument.nodes || [],
        edges: cachedDraftDocument.edges || [],
      }, 'error');
      clearCourseMapLocalCache({ courseId: rootId, editorMode: true, userId: currentUserId });
    }
    const persistedDraft = editorMode && cachedState?.dirty && cachedDraftDocument && cachedDraftValid
      ? {
          version: Number(cachedState.mapRecord.version || 0),
          dirty: true,
          aliases: cachedState.aliases || cacheCourseIds,
          sourceMode: 'editor',
          source: 'editor-cache',
          document: cachedDraftDocument,
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
    graphSourceRef.current = { mode: modeName, source: sourceLabel, viewKey };
    modeTransitionRef.current = false;
    loadedViewRef.current = viewKey;
    setNodes(nextNodes);
    setEdges(nextEdges);
    setLoading(false);

    courseMapConsoleGraph('SOURCE_APPLIED', {
      source: sourceLabel,
      viewKey,
      mode: modeName,
      version: Number(expectedRecord.version || 0),
      assignments: nextAssignments.length,
      courses: nextCourses.length,
      dirty: Boolean(isDirty),
      nodes: nextNodes,
      edges: nextEdges,
    });

    setCourseMapSessionState(rootId, {
      version: Number(expectedRecord.version || 0),
      dirty: Boolean(isDirty),
      aliases: cacheCourseIds,
      sourceMode: modeName,
      source: sourceLabel,
      document,
    }, sessionOptions);

    if (editorMode) {
      window.requestAnimationFrame(() => {
        try { flow.setViewport(document.viewport || { x: 0, y: 0, zoom: 1 }, { duration: 0 }); } catch {}
      });
    }

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
    return true;
  }, [allCourses, cacheCourseIds, course, currentUserId, decorateEdges, editAssignment, editCourse, editorMode, flow, focusBranch, focusNode, modeName, onLearnerProgress, openAssignment, rootId, sessionOptions, setEdges, setNodes, viewKey, visibleCourses]);

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
    if (editorMode || activeModeRef.current !== 'learner' || activeViewRef.current !== viewKey) {
      courseMapConsole('LEARNER_MERGE_SKIP', {
        reason: 'inactive-learner-view',
        callbackView: viewKey,
        activeView: activeViewRef.current,
        activeMode: activeModeRef.current,
        incomingNodes: Array.isArray(incomingNodes) ? incomingNodes.length : 0,
        incomingEdges: Array.isArray(incomingEdges) ? incomingEdges.length : 0,
      }, 'warn');
      return false;
    }
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
    const newNodeIds = new Set(rawNewNodes.map((node) => String(node?.id || '')).filter(Boolean));
    const layoutPendingEdgeIds = new Set(readyEdges
      .filter((edge) => newNodeIds.has(String(edge?.source || '')) || newNodeIds.has(String(edge?.target || '')))
      .map((edge) => String(edge?.id || ''))
      .filter(Boolean));
    newNodeIds.forEach((id) => pendingLayoutNodeIdsRef.current.add(id));
    layoutPendingEdgeIds.forEach((id) => pendingLayoutEdgeIdsRef.current.add(id));
    const decoratedNewNodes = decorateNodesWithSources(
      rawNewNodes.map((node) => ({
        ...node,
        className: `${node.className || ''}${animate ? ' course-map-node-revealed' : ''} course-map-node-layout-pending`.trim(),
      })),
      allRawEdgesForDecoration,
      mergedAssignments,
      mergedCourses,
      nextProgress,
    );
    const decoratedNewEdges = decorateEdges(readyEdges.map((edge) => layoutPendingEdgeIds.has(String(edge?.id || ''))
      ? { ...edge, className: `${edge.className || ''} course-map-edge-layout-pending`.trim() }
      : edge));
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

    loadedViewRef.current = viewKey;
    modeTransitionRef.current = false;
    setLoading(false);
    courseMapConsoleGraph('LEARNER_MERGE', {
      viewKey,
      source: graphSourceRef.current?.source || 'learner',
      incomingNodes: Array.isArray(incomingNodes) ? incomingNodes.length : 0,
      incomingEdges: Array.isArray(incomingEdges) ? incomingEdges.length : 0,
      pendingEdges: pendingLearnerEdgesRef.current.length,
      nodes: nextNodes,
      edges: nextEdges,
    });
    return true;
  }, [decorateEdges, decorateNodesWithSources, editorMode, mergeRowsById, onLearnerProgress, rootId, setEdges, setNodes, viewKey, visibleCourses]);

  const persistLearnerGraph = React.useCallback((reason = 'learner-persist') => {
    const expectedViewKey = viewKey;
    const source = graphSourceRef.current;
    if (editorMode || activeModeRef.current !== 'learner' || activeViewRef.current !== expectedViewKey) {
      courseMapConsole('LEARNER_PERSIST_SKIP', {
        reason,
        skip: 'inactive-learner-view',
        expectedViewKey,
        activeView: activeViewRef.current,
        activeMode: activeModeRef.current,
      });
      return false;
    }
    if (modeTransitionRef.current || !sourceMatchesMode(source, 'learner', expectedViewKey)) {
      courseMapConsole('LEARNER_PERSIST_SKIP', {
        reason,
        skip: modeTransitionRef.current ? 'mode-transition' : 'source-mode-mismatch',
        expectedViewKey,
        source,
      });
      return false;
    }
    if (!rootId || !nodesRef.current.length) return false;
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
    courseMapConsoleGraph('LEARNER_PERSIST', {
      reason,
      viewKey: expectedViewKey,
      source: source.source,
      version: Number(mapRecord.version || 0),
      projectionRevision: Number(mapRecord.projectionRevision || 0),
      hasProjectionToken: Boolean(mapRecord.projectionToken),
      nodes: nodesRef.current,
      edges: edgesRef.current,
    });
    return true;
  }, [currentUserId, editorMode, rootId, viewKey]);

  persistLearnerGraphRef.current = persistLearnerGraph;

  const applyLearnerDelta = React.useCallback((delta) => {
    if (editorMode || activeModeRef.current !== 'learner' || activeViewRef.current !== viewKey) {
      courseMapConsole('DELTA_SKIP', { reason: 'inactive-learner-view', viewKey, activeView: activeViewRef.current, activeMode: activeModeRef.current }, 'warn');
      return false;
    }
    if (!delta || delta.resetRequired) {
      courseMapConsole('DELTA_RESET', {
        viewKey,
        resetRequired: Boolean(delta?.resetRequired),
        version: Number(delta?.version || 0),
        projectionRevision: Number(delta?.projectionRevision || 0),
      }, 'warn');
      return false;
    }
    graphSourceRef.current = { mode: 'learner', source: 'learner-delta', viewKey };
    modeTransitionRef.current = false;
    learnerProjectionSettledRef.current = true;
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
    courseMapConsole('DELTA_APPLIED', {
      viewKey,
      nodesAdded: (delta.nodesAdded || []).length,
      nodesRemoved: removeNodeIds.size,
      edgesAdded: (delta.edgesAdded || []).length,
      edgesRemoved: removeEdgeIds.size,
      assignmentsChanged: (delta.assignmentsChanged || []).length,
      coursesChanged: (delta.coursesChanged || []).length,
      openedCourses: (delta.openedCourseIds || []).length,
      projectionRevision: Number(delta.projectionRevision || 0),
    });
    persistLearnerGraph('delta-applied');
    return true;
  }, [editorMode, mergeLearnerGraph, persistLearnerGraph, setEdges, setNodes, updateLearnerProjectionRecord, viewKey]);

  const streamLearnerMap = React.useCallback(async (requestId, { quiet = false, preserveExisting = false, fresh = false } = {}) => {
    if (editorMode || activeModeRef.current !== 'learner' || activeViewRef.current !== viewKey) {
      courseMapConsole('STREAM_SKIP', { reason: 'inactive-learner-view', requestId, viewKey, activeView: activeViewRef.current, activeMode: activeModeRef.current }, 'warn');
      return;
    }
    streamAbortRef.current?.abort?.();
    const controller = new AbortController();
    streamAbortRef.current = controller;
    let receivedSegment = false;
    let keepExisting = Boolean(preserveExisting);
    const seenNodeIds = new Set();
    const seenEdgeIds = new Set();
    if (!quiet) setLoading(true);
    courseMapConsoleGraph('STREAM_BEGIN', {
      requestId,
      viewKey,
      quiet: Boolean(quiet),
      preserveExisting: keepExisting,
      fresh: Boolean(fresh),
      versionBefore: Number(recordRef.current.version || 0),
      nodes: nodesRef.current,
      edges: edgesRef.current,
    });

    await streamLearningCourseMap(rootId, {
      signal: controller.signal,
      fresh: Boolean(fresh),
      onMeta: (meta) => {
        if (requestId !== loadRequestRef.current || activeModeRef.current !== 'learner' || activeViewRef.current !== viewKey) {
          courseMapConsole('STREAM_META_SKIP', { requestId, currentRequestId: loadRequestRef.current, viewKey, activeView: activeViewRef.current, activeMode: activeModeRef.current }, 'warn');
          return;
        }
        const incomingVersion = Number(meta?.version || 0);
        const cachedVersion = Number(recordRef.current.version || 0);
        if (keepExisting && cachedVersion > 0 && incomingVersion > 0 && incomingVersion !== cachedVersion) {
          courseMapConsole('CACHE_REBASE', { reason: 'map-version-changed', cachedVersion, incomingVersion, viewKey }, 'warn');
        }
        graphSourceRef.current = { mode: 'learner', source: 'learner-stream', viewKey };
        modeTransitionRef.current = false;
        learnerProjectionSettledRef.current = false;
        projectionTokenRef.current = '';
        pendingLearnerEdgesRef.current = [];
        if (!keepExisting) {
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
          ...(keepExisting ? {} : {
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
        if (!keepExisting) viewportRef.current = meta?.viewport || viewportRef.current;
        courseMapConsole('STREAM_META', {
          requestId,
          viewKey,
          version: incomingVersion,
          preserveExisting: keepExisting,
          rootCourseId: meta?.rootCourseId || rootId,
          requestedCourseId: meta?.requestedCourseId || rootId,
        });
      },
      onSegment: (segment) => {
        if (requestId !== loadRequestRef.current || activeModeRef.current !== 'learner' || activeViewRef.current !== viewKey || !segment) {
          if (segment) courseMapConsole('STREAM_SEGMENT_SKIP', { requestId, currentRequestId: loadRequestRef.current, viewKey, activeView: activeViewRef.current }, 'warn');
          return;
        }
        receivedSegment = true;
        for (const node of segment.nodes || []) if (node?.id) seenNodeIds.add(String(node.id));
        for (const edge of segment.edges || []) if (edge?.id) seenEdgeIds.add(String(edge.id));
        courseMapConsole('STREAM_SEGMENT', {
          requestId,
          viewKey,
          courseId: segment.courseId || null,
          nodes: (segment.nodes || []).length,
          edges: (segment.edges || []).length,
          assignments: (segment.assignments || []).length,
          courses: (segment.courses || []).length,
        });
        mergeLearnerGraph({ ...segment, animate: nodesRef.current.length > 0 });
        window.clearTimeout(learnerPersistTimerRef.current);
        learnerPersistTimerRef.current = window.setTimeout(() => persistLearnerGraph('stream-partial'), 180);
      },
      onDone: (done, meta) => {
        if (requestId !== loadRequestRef.current || activeModeRef.current !== 'learner' || activeViewRef.current !== viewKey) {
          courseMapConsole('STREAM_DONE_SKIP', { requestId, currentRequestId: loadRequestRef.current, viewKey, activeView: activeViewRef.current, activeMode: activeModeRef.current }, 'warn');
          return;
        }
        updateLearnerProjectionRecord({
          rootCourseId: meta?.rootCourseId || rootId,
          requestedCourseId: meta?.requestedCourseId || rootId,
          version: Number(meta?.version || recordRef.current.version || 0),
          projectionToken: done?.projectionToken || meta?.projectionToken || '',
          projectionRevision: Number(done?.projectionRevision || meta?.projectionRevision || 0),
          updatedAt: meta?.updatedAt || recordRef.current.updatedAt || null,
          updatedBy: meta?.updatedBy || recordRef.current.updatedBy || null,
          fullSyncAt: Date.now(),
          verifiedAt: Date.now(),
        });
        let staleNodeCount = 0;
        let staleEdgeCount = 0;
        if (keepExisting) {
          const staleNodeIds = new Set(nodesRef.current
            .map((node) => String(node.id))
            .filter((id) => !seenNodeIds.has(id)));
          const nextNodes = nodesRef.current.filter((node) => !staleNodeIds.has(String(node.id)));
          const nextEdges = edgesRef.current.filter((edge) => seenEdgeIds.has(String(edge.id))
            && !staleNodeIds.has(String(edge.source))
            && !staleNodeIds.has(String(edge.target)));
          staleNodeCount = staleNodeIds.size;
          staleEdgeCount = Math.max(0, edgesRef.current.length - nextEdges.length);
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
        graphSourceRef.current = { mode: 'learner', source: 'learner-stream', viewKey };
        modeTransitionRef.current = false;
        learnerProjectionSettledRef.current = true;
        loadedViewRef.current = viewKey;
        courseMapConsoleGraph('STREAM_DONE', {
          requestId,
          viewKey,
          version: Number(meta?.version || recordRef.current.version || 0),
          projectionRevision: Number(done?.projectionRevision || meta?.projectionRevision || 0),
          staleNodesRemoved: staleNodeCount,
          staleEdgesRemoved: staleEdgeCount,
          seenNodes: seenNodeIds.size,
          seenEdges: seenEdgeIds.size,
          nodes: nodesRef.current,
          edges: edgesRef.current,
        });
        persistLearnerGraph('stream-done');
      },
    });

    if (requestId === loadRequestRef.current && !receivedSegment) setLoading(false);
  }, [course, editorMode, flow, mergeLearnerGraph, persistLearnerGraph, rootId, setEdges, setNodes, updateLearnerProjectionRecord, viewKey]);

  const fetchEditorTree = React.useCallback(async (reason = 'editor-load') => {
    const existing = editorTreeFetchRef.current;
    if (existing.rootId === rootId && existing.promise) {
      courseMapConsole('EDITOR_TREE_JOIN', { rootCourseId: rootId, reason });
      return existing.promise;
    }
    const started = performance.now();
    courseMapConsole('EDITOR_TREE_BEGIN', { rootCourseId: rootId, reason });
    const promise = getAssignmentsByCourseTree(rootId)
      .then((rows) => {
        courseMapConsole('EDITOR_TREE_END', {
          rootCourseId: rootId,
          reason,
          assignments: Array.isArray(rows) ? rows.length : 0,
          durationMs: Math.round((performance.now() - started) * 10) / 10,
        });
        return rows;
      })
      .catch((error) => {
        courseMapConsole('EDITOR_TREE_FAIL', {
          rootCourseId: rootId,
          reason,
          durationMs: Math.round((performance.now() - started) * 10) / 10,
          status: error?.response?.status || null,
          message: error?.message || String(error),
        }, 'error');
        throw error;
      })
      .finally(() => {
        if (editorTreeFetchRef.current.promise === promise) {
          editorTreeFetchRef.current = { rootId: '', promise: null };
        }
      });
    editorTreeFetchRef.current = { rootId, promise };
    return promise;
  }, [rootId]);

  const loadMap = React.useCallback(async ({ preferSession = true, quiet = false } = {}) => {
    if (!rootId) return;
    const requestId = ++loadRequestRef.current;
    const initialForView = loadedViewRef.current !== viewKey;
    let restoredFromCache = false;
    let cached = null;
    const started = performance.now();
    const browserReload = initialForView && isBrowserReloadNavigation();
    let forceFullRevalidation = false;

    courseMapConsoleGraph('LOAD_BEGIN', {
      requestId,
      viewKey,
      mode: modeName,
      preferSession: Boolean(preferSession),
      quiet: Boolean(quiet),
      initialForView,
      browserReload,
      sourceBefore: graphSourceRef.current,
      nodes: nodesRef.current,
      edges: edgesRef.current,
    });

    if (initialForView && preferSession) {
      cached = readCourseMapLocalCache({ courseId: rootId, editorMode, userId: currentUserId });
      let cacheLayer = cached?.cacheLayer || 'miss';
      if (!cached) {
        cached = await readCourseMapLocalCacheAsync({ courseId: rootId, editorMode, userId: currentUserId });
        cacheLayer = cached?.cacheLayer || 'miss';
      }
      if (requestId !== loadRequestRef.current || activeViewRef.current !== viewKey) {
        courseMapConsole('CACHE_SKIP', { requestId, viewKey, cacheLayer, reason: 'stale-request' }, 'warn');
        return;
      }
      const cacheHasProgress = editorMode || Number(cached?.mapRecord?.document?.courseProgressVersion) === 1;
      const cacheHasSynthetic = Boolean(editorMode && cached?.mapRecord?.document && documentHasLearnerSyntheticArtifacts(cached.mapRecord.document));
      courseMapConsole('CACHE_LOOKUP', {
        requestId,
        viewKey,
        mode: modeName,
        result: cached?.mapRecord ? 'hit' : 'miss',
        layer: cacheLayer,
        version: Number(cached?.mapRecord?.version || 0),
        dirty: Boolean(cached?.dirty),
        projectionToken: Boolean(cached?.mapRecord?.projectionToken),
        sourceMode: cached?.sourceMode || null,
        synthetic: cacheHasSynthetic,
      }, cacheHasSynthetic ? 'error' : 'info');

      if (cacheHasSynthetic) {
        clearCourseMapLocalCache({ courseId: rootId, editorMode: true, userId: currentUserId });
        clearCourseMapSessionState(rootId, sessionOptions);
        cached = null;
      } else if (cached?.mapRecord && cacheHasProgress) {
        const applied = applyMapPayload(cached.mapRecord, cached.assignments, {
          preferSession: true,
          fromCache: true,
          cachedState: cached,
          source: `${modeName}-cache:${cacheLayer}`,
        });
        restoredFromCache = applied === true;
        if (restoredFromCache && !editorMode) {
          projectionTokenRef.current = String(cached.mapRecord?.projectionToken || '');
          mapCoursesRef.current = Array.isArray(cached.courses) ? cached.courses : [];
          setMapCourses(mapCoursesRef.current);
        }
      } else if (cached?.mapRecord && !editorMode) {
        courseMapConsole('CACHE_DISCARD', { viewKey, reason: 'learner-progress-schema-missing' }, 'warn');
        clearCourseMapLocalCache({ courseId: rootId, editorMode: false, userId: currentUserId });
        cached = null;
      }
    }

    if (!editorMode && restoredFromCache) {
      forceFullRevalidation = courseMapCacheNeedsFullRevalidation(cached, { force: browserReload });
      courseMapConsole('CACHE_REVALIDATE_POLICY', {
        requestId,
        viewKey,
        browserReload,
        forceFullRevalidation,
        fullSyncAt: Number(cached?.fullSyncAt || cached?.mapRecord?.fullSyncAt || 0),
        verifiedAt: Number(cached?.verifiedAt || cached?.mapRecord?.verifiedAt || cached?.fullSyncAt || cached?.mapRecord?.fullSyncAt || 0),
      });
    }

    if (!quiet && initialForView && !restoredFromCache) setLoading(true);

    if (!editorMode) {
      try {
        const token = String(cached?.mapRecord?.projectionToken || projectionTokenRef.current || '');
        let deltaVerificationFailed = false;
        if (restoredFromCache && token) {
          const projectionCourseId = String(cached?.mapRecord?.requestedCourseId || cached?.requestedCourseId || rootId);
          try {
            courseMapConsole('DELTA_REQUEST', {
              requestId,
              viewKey,
              projectionCourseId,
              version: Number(cached?.mapRecord?.version || 0),
              projectionRevision: Number(cached?.mapRecord?.projectionRevision || 0),
            });
            const deltaStarted = performance.now();
            const delta = await getLearningCourseMapDelta(projectionCourseId, token, null);
            courseMapConsole('DELTA_RESPONSE', {
              requestId,
              viewKey,
              durationMs: Math.round((performance.now() - deltaStarted) * 10) / 10,
              resetRequired: Boolean(delta?.resetRequired),
              version: Number(delta?.version || 0),
              projectionRevision: Number(delta?.projectionRevision || 0),
              nodesAdded: (delta?.nodesAdded || []).length,
              nodesRemoved: (delta?.nodeIdsRemoved || []).length,
              edgesAdded: (delta?.edgesAdded || []).length,
              edgesRemoved: (delta?.edgeIdsRemoved || []).length,
            });
            if (requestId !== loadRequestRef.current || activeViewRef.current !== viewKey) return;
            if (!delta?.resetRequired) {
              applyLearnerDelta(delta);
              // CreateDeltaAsync always verifies map metadata against education-api with
              // forceFreshMeta=true. A successful delta therefore proves that the cached
              // graph version is still authoritative without rebuilding the whole snapshot.
              updateLearnerProjectionRecord({ verifiedAt: Date.now() });
              persistLearnerGraph('server-version-verified');
              courseMapConsole('LOAD_END', {
                requestId,
                viewKey,
                path: forceFullRevalidation ? 'cache+authoritative-delta' : 'cache+delta',
                durationMs: Math.round((performance.now() - started) * 10) / 10,
              });
              return;
            }
            // Projection tokens are intentionally shorter-lived than the local graph cache.
            // An expired token therefore means "rebase from the server", not "erase the UI".
            // Keep the currently rendered graph until the fresh stream has replaced/pruned it.
            projectionTokenRef.current = '';
            updateLearnerProjectionRecord({ projectionToken: '', projectionRevision: 0 });
            persistLearnerGraph('delta-reset-token-invalidated');
            courseMapConsoleGraph('LEARNER_REBASE_REQUIRED', {
              reason: 'delta-reset-required',
              viewKey,
              serverVersion: Number(delta?.version || 0),
              nodes: nodesRef.current,
              edges: edgesRef.current,
            }, 'warn');
            restoredFromCache = true;
            cached = null;
            forceFullRevalidation = false;
          } catch (deltaError) {
            if (deltaError?.name === 'AbortError') throw deltaError;
            // Rolling deploys can briefly leave a frontend talking to an older tasks-api
            // where learner POST /learning-map/delta was classified as Editor-only. Never
            // strand the user on stale data: fall back to an authoritative fresh stream.
            deltaVerificationFailed = true;
            courseMapConsole('DELTA_FALLBACK', {
              requestId,
              viewKey,
              projectionCourseId,
              status: deltaError?.response?.status || null,
              message: deltaError?.message || String(deltaError),
            }, 'warn');
          }
        }
        await streamLearnerMap(requestId, {
          quiet: restoredFromCache || quiet,
          preserveExisting: restoredFromCache,
          // A version-changing delta reset gives us a new versioned snapshot key, so a
          // normal stream cannot reuse the old graph. Explicit fresh is reserved for
          // legacy cache without a token or as a safe fallback when delta verification
          // could not run (for example during a rolling deployment).
          fresh: (forceFullRevalidation && !token) || deltaVerificationFailed,
        });
        courseMapConsole('LOAD_END', {
          requestId,
          viewKey,
          path: restoredFromCache ? 'partial-cache+stream' : 'stream',
          durationMs: Math.round((performance.now() - started) * 10) / 10,
        });
      } catch (error) {
        if (error?.name === 'AbortError') {
          courseMapConsole('LOAD_ABORT', { requestId, viewKey, mode: 'learner' }, 'warn');
          return;
        }
        courseMapConsole('LOAD_FAIL', {
          requestId,
          viewKey,
          mode: 'learner',
          restoredFromCache,
          status: error?.response?.status || null,
          message: error?.message || String(error),
          durationMs: Math.round((performance.now() - started) * 10) / 10,
        }, 'error');
        if (requestId === loadRequestRef.current && !restoredFromCache) {
          notify.error(getApiErrorMessage(error, 'Не удалось загрузить карту курса'));
        } else if (requestId === loadRequestRef.current && forceFullRevalidation) {
          notify.warn('Показана сохранённая карта: не удалось проверить свежую версию на сервере.');
        }
      } finally {
        if (requestId === loadRequestRef.current && !restoredFromCache) setLoading(false);
      }
      return;
    }

    try {
      const mapStarted = performance.now();
      const mapPromise = getCourseMap(rootId).then((value) => {
        courseMapConsole('EDITOR_MAP_END', {
          requestId,
          viewKey,
          version: Number(value?.version || 0),
          durationMs: Math.round((performance.now() - mapStarted) * 10) / 10,
          documentNodes: Array.isArray(value?.document?.nodes) ? value.document.nodes.length : 0,
          documentEdges: Array.isArray(value?.document?.edges) ? value.document.edges.length : 0,
        });
        return value;
      });
      courseMapConsole('EDITOR_MAP_BEGIN', { requestId, viewKey, rootCourseId: rootId });
      const [mapRecord, treeAssignments] = await Promise.all([
        mapPromise,
        fetchEditorTree('editor-map-load'),
      ]);
      if (requestId !== loadRequestRef.current || activeViewRef.current !== viewKey || activeModeRef.current !== 'editor') {
        courseMapConsole('EDITOR_LOAD_SKIP', { requestId, currentRequestId: loadRequestRef.current, viewKey, activeView: activeViewRef.current, activeMode: activeModeRef.current }, 'warn');
        return;
      }
      applyMapPayload(mapRecord, treeAssignments, {
        preferSession,
        fromCache: false,
        source: 'editor-server',
      });
      courseMapConsole('LOAD_END', {
        requestId,
        viewKey,
        path: restoredFromCache ? 'editor-cache+server' : 'editor-server',
        durationMs: Math.round((performance.now() - started) * 10) / 10,
      });
    } catch (error) {
      courseMapConsole('LOAD_FAIL', {
        requestId,
        viewKey,
        mode: 'editor',
        restoredFromCache,
        status: error?.response?.status || null,
        message: error?.message || String(error),
        durationMs: Math.round((performance.now() - started) * 10) / 10,
      }, 'error');
      if (requestId === loadRequestRef.current && !restoredFromCache) {
        notify.error(getApiErrorMessage(error, 'Не удалось загрузить карту курса'));
      }
    } finally {
      if (requestId === loadRequestRef.current && initialForView && !restoredFromCache) setLoading(false);
    }
  }, [applyLearnerDelta, applyMapPayload, currentUserId, editorMode, fetchEditorTree, modeName, notify, persistLearnerGraph, rootId, sessionOptions, streamLearnerMap, updateLearnerProjectionRecord, viewKey]);

  loadMapRef.current = loadMap;
  React.useEffect(() => {
    if (!rootId) return;
    const alreadyLoaded = loadedViewRef.current === viewKey;
    courseMapConsole('LOAD_TRIGGER', { viewKey, alreadyLoaded, mode: modeName });
    void loadMapRef.current?.({ preferSession: true, quiet: alreadyLoaded });
  }, [modeName, rootId, viewKey]);

  React.useEffect(() => {
    if (!editorMode || !rootId) return undefined;
    if (dataRevisionRef.current === dataRevision) return undefined;
    const previousRevision = dataRevisionRef.current;
    dataRevisionRef.current = dataRevision;
    if (modeTransitionRef.current || loadedViewRef.current !== viewKey || !sourceMatchesMode(graphSourceRef.current, 'editor', viewKey)) {
      courseMapConsole('EDITOR_TREE_REVISION_DEFER', {
        rootCourseId: rootId,
        viewKey,
        previousRevision,
        nextRevision: dataRevision,
        transition: modeTransitionRef.current,
        loadedView: loadedViewRef.current,
        source: graphSourceRef.current,
      });
      return undefined;
    }
    let disposed = false;
    courseMapConsole('EDITOR_TREE_REVISION', { rootCourseId: rootId, viewKey, previousRevision, nextRevision: dataRevision });
    fetchEditorTree('data-revision').then((rows) => {
      if (disposed || activeViewRef.current !== viewKey || activeModeRef.current !== 'editor') return;
      const nextAssignments = Array.isArray(rows) ? rows : [];
      assignmentsRef.current = nextAssignments;
      setAssignments(nextAssignments);
      if (hasLearnerSyntheticArtifacts(nodesRef.current, edgesRef.current)) {
        courseMapConsoleGraph('SYNTHETIC_GUARD', {
          reason: 'editor-tree-revision-cache-blocked',
          viewKey,
          nodes: nodesRef.current,
          edges: edgesRef.current,
        }, 'error');
        return;
      }
      const document = serializeCourseMap(nodesRef.current, edgesRef.current, viewportRef.current);
      writeCourseMapLocalCache({
        courseId: rootId,
        rootCourseId: recordRef.current.rootCourseId || rootId,
        editorMode: true,
        userId: currentUserId,
        aliases: cacheCourseIds,
        mapRecord: { ...recordRef.current, document },
        assignments: nextAssignments,
        courses: mapCoursesRef.current.length ? mapCoursesRef.current : visibleCourses,
        dirty: dirtyRef.current,
      });
      courseMapConsole('EDITOR_TREE_REVISION_APPLIED', { rootCourseId: rootId, viewKey, assignments: nextAssignments.length, revision: dataRevision });
    }).catch((error) => {
      courseMapConsole('EDITOR_TREE_REVISION_FAIL', { rootCourseId: rootId, viewKey, status: error?.response?.status || null, message: error?.message || String(error) }, 'error');
    });
    return () => { disposed = true; };
  }, [cacheCourseIds, currentUserId, dataRevision, editorMode, fetchEditorTree, rootId, viewKey, visibleCourses]);

  React.useEffect(() => {
    if (!nodesRef.current.length) return;
    setNodes((current) => {
      const next = decorateNodes(current);
      nodesRef.current = next;
      return next;
    });
  }, [decorateNodes, graphRevision, setNodes]);

  React.useEffect(() => {
    if (editorMode || focusCourseId || !nodesInitialized || !nodes.length) return;
    const state = learnerEntryFocusRef.current;
    if (state.viewKey !== viewKey) {
      learnerEntryFocusRef.current = { viewKey, lastTargetId: '', fallbackCentered: false, completed: false };
    }
    const current = learnerEntryFocusRef.current;
    if (current.completed) return;

    const candidate = learnerEntryFocusCandidate(nodes, edges, rootId);
    const source = String(graphSourceRef.current?.source || '');
    const provisional = source.includes('cache');
    const preferredId = candidate.targetId || candidate.rootId || candidate.fallbackId;
    if (!preferredId) return;

    const targetChanged = current.lastTargetId !== String(preferredId);
    if (targetChanged || !current.fallbackCentered) {
      const duration = current.fallbackCentered ? 280 : 0;
      if (centerLearnerEntryNode(flow, preferredId, { duration, zoom: 0.92 })) {
        current.lastTargetId = String(preferredId);
        current.fallbackCentered = true;
        window.requestAnimationFrame(() => {
          try { viewportRef.current = flow.getViewport(); } catch {}
        });
        courseMapConsole('LEARNER_ENTRY_FOCUS', {
          viewKey,
          source,
          targetNodeId: String(preferredId),
          reason: candidate.targetId ? 'first-unsolved-from-start' : 'root-fallback',
          provisional,
        });
      }
    }

    if (!provisional && candidate.targetId) current.completed = true;
    if (!provisional && learnerProjectionSettledRef.current && !candidate.targetId) current.completed = true;
  }, [edges, editorMode, flow, focusCourseId, nodes, nodesInitialized, record?.projectionRevision, rootId, viewKey]);

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
    const source = graphSourceRef.current;
    if (activeModeRef.current !== 'editor' || activeViewRef.current !== viewKey || modeTransitionRef.current || !sourceMatchesMode(source, 'editor', viewKey)) {
      courseMapConsole('SAVE_BLOCKED', {
        reason: 'editor-source-not-ready',
        viewKey,
        activeView: activeViewRef.current,
        activeMode: activeModeRef.current,
        transition: modeTransitionRef.current,
        source,
      }, 'error');
      notify.error('Полная карта редактора ещё загружается. Повторите сохранение после загрузки.');
      return false;
    }
    if (hasLearnerSyntheticArtifacts(nodesRef.current, edgesRef.current)) {
      courseMapConsoleGraph('SYNTHETIC_GUARD', {
        reason: 'editor-save-blocked',
        viewKey,
        source,
        nodes: nodesRef.current,
        edges: edgesRef.current,
      }, 'error');
      notify.error('Карта редактора содержит временные элементы ученического режима. Карта перезагружена без сохранения.');
      clearCourseMapSessionState(rootId, sessionOptions);
      clearCourseMapLocalCache({ courseId: rootId, editorMode: true, userId: currentUserId });
      void loadMapRef.current?.({ preferSession: false, quiet: false });
      return false;
    }
    try {
      const document = serializeCourseMap(nodesRef.current, edgesRef.current, viewportRef.current);
      courseMapConsoleGraph('SAVE_BEGIN', {
        viewKey,
        source: source.source,
        expectedVersion: Number(recordRef.current.version || 0),
        nodes: nodesRef.current,
        edges: edgesRef.current,
      });
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
      graphSourceRef.current = { mode: 'editor', source: 'editor-server-saved', viewKey };
      modeTransitionRef.current = false;
      loadedViewRef.current = viewKey;
      courseMapConsole('SAVE_END', {
        viewKey,
        version: Number(saved.version || 0),
        nodes: nodesRef.current.length,
        edges: edgesRef.current.length,
      });
      if (!quietSuccess) notify.success('Карта сохранена');
      return true;
    } catch (error) {
      if (error?.response?.status === 409) {
        courseMapConsole('SAVE_FAIL', { viewKey, status: 409, reason: 'version-conflict', message: error?.message || String(error) }, 'error');
        setServerChanged(true);
        notify.warn('Карту уже изменил другой редактор. Ваши локальные изменения сохранены на экране.');
        return false;
      }
      courseMapConsole('SAVE_FAIL', { viewKey, status: error?.response?.status || null, message: error?.message || String(error) }, 'error');
      notify.error(getApiErrorMessage(error, 'Не удалось сохранить карту'));
      return false;
    }
  }, [assignments, cacheCourseIds, currentUserId, editorMode, notify, rootId, sessionOptions, viewKey]);

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
      if (result.viewport && Number.isFinite(Number(result.viewport.zoom))) {
        const viewport = {
          x: Number(result.viewport.x) || 0,
          y: Number(result.viewport.y) || 0,
          zoom: Number(result.viewport.zoom) || 1,
        };
        viewportRef.current = viewport;
        try { flow.setViewport(viewport, { duration: 280 }); } catch {}
        return;
      }
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

  React.useEffect(() => {
    if (!editorMode || modeTransitionRef.current || loadedViewRef.current !== viewKey) return;
    const signature = `${viewKey}:${nodes.length}:${edges.length}:${assignments.length}:${visibleCourses.length}:${unplaced.length}`;
    if (unplacedDebugRef.current === signature) return;
    unplacedDebugRef.current = signature;
    courseMapConsole('EDITOR_UNPLACED', {
      viewKey,
      source: graphSourceRef.current,
      nodes: nodes.length,
      edges: edges.length,
      assignments: assignments.length,
      courses: visibleCourses.length,
      unplaced: unplaced.length,
    }, unplaced.length > 50 ? 'warn' : 'info');
  }, [assignments.length, edges.length, editorMode, nodes.length, unplaced.length, viewKey, visibleCourses.length]);

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
      className={`course-map-shell${editorMode ? ' is-editor' : ' is-viewer'}${interacting || context.open ? ' is-interacting' : ''}${sceneReady ? ' is-layout-ready' : ' is-layout-pending'}`}
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
          onNodeClick={(event, node) => {
            if (editorMode || event.defaultPrevented || node.type === 'locked' || node.type === 'course') return;
            openAssignment(node.entityId);
          }}
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
