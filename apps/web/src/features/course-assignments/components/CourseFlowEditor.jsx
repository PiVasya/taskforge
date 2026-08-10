import React from 'react';
import ReactFlow, {
  ReactFlowProvider,
  Background,
  Controls,
  MiniMap,
  MarkerType,
  addEdge,
  applyEdgeChanges,
  applyNodeChanges,
  useEdgesState,
  useNodesState,
  useReactFlow,
} from 'reactflow';
import 'reactflow/dist/style.css';
import '../course-map.css';
import { ArrowLeft, Download, Eye, FileCode2, FileJson, FolderTree, Image as ImageIcon, LayoutGrid, Pencil, Save, Search, Sigma, Trash2, X, ListChecks, RotateCcw, Unlink2 } from 'lucide-react';
import { useNavigate } from 'react-router-dom';

import { ContextMenu, ContextMenuItem, ContextMenuLabel, ContextMenuSeparator } from '../../../components/ui/ContextMenu';
import { Button } from '../../../components/ui';
import { useNotify } from '../../../components/notify/NotifyProvider';
import { useAuth } from '../../../auth/AuthContext';
import { createAssignment, deleteAssignment, getAssignmentsByCourseTree } from '../../../api/assignments';
import { createCourse, deleteCourse } from '../../../api/courses';
import { getCourseMap, saveCourseMap } from '../../../api/courseMaps';
import { createCourseMapPresenceConnection, disposeCourseMapPresenceConnection } from '../../../realtime/courseMapHub';
import { getApiErrorMessage } from '../../../api/http';
import { buildDefaultAssignmentPayload, previewAssignmentDescription } from '../courseAssignmentsModel';
import {
  assignmentNodeId,
  assignmentNodeType,
  buildDefaultCourseMap,
  buildEntityIndex,
  computeCourseProgress,
  courseNodeId,
  findUnplacedEntities,
  normalizeStoredMap,
  serializeCourseMap,
  wouldCreateCycle,
} from '../courseMapModel';
import { clearCourseMapSessionState, getCourseMapSessionState, setCourseMapSessionState } from '../courseMapSessionState';
import CourseNode from '../nodes/CourseNode';
import CodeTestNode from '../nodes/CodeTestNode';
import TestNode from '../nodes/TestNode';
import ImageCodeNode from '../nodes/ImageCodeNode';
import MathNode from '../nodes/MathNode';

const NODE_TYPES = {
  course: CourseNode,
  'code-test': CodeTestNode,
  test: TestNode,
  'image-code': ImageCodeNode,
  math: MathNode,
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

function edgeStyle(editorMode) {
  return {
    markerEnd: { type: MarkerType.ArrowClosed, width: 18, height: 18 },
    className: `course-map-edge${editorMode ? ' is-editable' : ''}`,
    type: 'smoothstep',
    interactionWidth: editorMode ? 28 : 18,
  };
}

function CourseMapInner({ course, allCourses, courseCanEdit, editorMode, query = '', focusCourseId = '', dataRevision = 0, onRefreshCourseData, onQueryChange, onShowGrid, onExportJson, onImportJson, exportBusy = false }) {
  const nav = useNavigate();
  const notify = useNotify();
  const { access, user } = useAuth();
  const flow = useReactFlow();
  const shellRef = React.useRef(null);
  const [nodes, setNodes] = useNodesState([]);
  const [edges, setEdges] = useEdgesState([]);
  const [record, setRecord] = React.useState({ version: 0, document: null });
  const [assignments, setAssignments] = React.useState([]);
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
  const viewportRef = React.useRef({ x: 0, y: 0, zoom: 1 });
  const nodesRef = React.useRef([]);
  const edgesRef = React.useRef([]);
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

  const rootId = String(course?.id || '');
  const visibleCourses = React.useMemo(() => subtreeCourses(rootId, allCourses, course), [allCourses, course, rootId]);
  const entityIndex = React.useMemo(() => buildEntityIndex(visibleCourses, assignments), [assignments, visibleCourses]);
  const currentUserId = String(user?.id || user?.userId || user?.uuid || '');

  React.useEffect(() => { recordRef.current = record; }, [record]);
  React.useEffect(() => { dirtyRef.current = dirty; }, [dirty]);
  React.useEffect(() => { nodesRef.current = nodes; }, [nodes]);
  React.useEffect(() => { edgesRef.current = edges; }, [edges]);
  React.useEffect(() => { if (query) setSearchOpen(true); }, [query]);
  React.useEffect(() => {
    if (!searchOpen) return undefined;
    const frame = window.requestAnimationFrame(() => searchInputRef.current?.focus());
    return () => window.cancelAnimationFrame(frame);
  }, [searchOpen]);

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
    setCourseMapSessionState(rootId, {
      version: recordRef.current.version || 0,
      dirty: Boolean(nextDirty),
      document: serializeCourseMap(nodesRef.current, edgesRef.current, viewportRef.current),
    });
  }, [rootId]);

  const rememberBeforeNavigate = React.useCallback(() => persistSession(dirtyRef.current), [persistSession]);

  const openAssignment = React.useCallback((assignmentId) => {
    rememberBeforeNavigate();
    nav(`/assignment/${assignmentId}`);
  }, [nav, rememberBeforeNavigate]);

  const editAssignment = React.useCallback((assignmentId) => {
    rememberBeforeNavigate();
    const returnTo = `/course/${rootId}`;
    nav(`/assignment/${assignmentId}/edit?returnTo=${encodeURIComponent(returnTo)}`);
  }, [nav, rememberBeforeNavigate, rootId]);

  const editCourse = React.useCallback((courseId) => {
    rememberBeforeNavigate();
    const returnTo = `/course/${rootId}`;
    nav(`/courses/${courseId}/edit?returnTo=${encodeURIComponent(returnTo)}`);
  }, [nav, rememberBeforeNavigate, rootId]);

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

  const decorateNodes = React.useCallback((rawNodes) => {
    const q = String(query || '').trim().toLowerCase();
    const progressByCourse = computeCourseProgress(rawNodes, edgesRef.current, assignments, rootId);
    return rawNodes.map((node) => {
      const isCourse = node.type === 'course';
      const entity = isCourse ? entityIndex.courseById.get(String(node.entityId)) : entityIndex.assignmentById.get(String(node.entityId));
      const progress = isCourse ? (progressByCourse.get(String(node.id)) || { total: 0, solved: 0, percent: 0 }) : null;
      const searchText = `${entity?.title || ''} ${previewAssignmentDescription(entity?.description || '')} ${entity?.tags || ''}`.toLowerCase();
      const searchMatch = !q || searchText.includes(q);
      const visualType = isCourse ? 'course' : assignmentNodeType(entity?.type || node.type);
      return {
        ...node,
        type: visualType,
        className: searchMatch ? '' : 'course-map-search-dimmed',
        data: {
          entityId: node.entityId,
          entity,
          progress,
          editorMode,
          searchMatch,
          onOpen: isCourse ? () => focusBranch(node.id) : () => openAssignment(node.entityId),
          onFocus: isCourse ? () => focusBranch(node.id) : () => focusNode(node.id),
          onEdit: isCourse ? () => editCourse(node.entityId) : () => editAssignment(node.entityId),
        },
      };
    });
  }, [assignments, editAssignment, editCourse, editorMode, entityIndex, focusBranch, focusNode, openAssignment, query, rootId]);

  const decorateEdges = React.useCallback((rawEdges) => rawEdges.map((edge) => ({ ...edge, ...edgeStyle(editorMode) })), [editorMode]);

  const loadMap = React.useCallback(async ({ preferSession = true, quiet = false } = {}) => {
    if (!rootId) return;
    const requestId = ++loadRequestRef.current;
    const initialForRoot = loadedRootRef.current !== rootId;
    if (!quiet && initialForRoot) setLoading(true);
    try {
      const [mapRecord, treeAssignments] = await Promise.all([getCourseMap(rootId), getAssignmentsByCourseTree(rootId)]);
      if (requestId !== loadRequestRef.current) return;
      const nextAssignments = Array.isArray(treeAssignments) ? treeAssignments : [];
      const nextCourses = subtreeCourses(rootId, allCourses, course);
      const session = preferSession ? getCourseMapSessionState(rootId) : null;
      const serverVersion = Number(mapRecord?.version || 0);
      const sessionVersion = Number(session?.version || 0);
      const sessionIsDirty = Boolean(session?.dirty);
      const sessionMatchesServer = Boolean(session?.document) && sessionVersion === serverVersion;
      const keepDirtySessionAcrossConflict = Boolean(session?.document) && sessionIsDirty && sessionVersion !== serverVersion;
      let document = sessionMatchesServer || keepDirtySessionAcrossConflict ? session.document : null;
      let isDirty = document ? sessionIsDirty : false;
      let expectedRecord = mapRecord || { version: 0, document: null };
      let changedOnServer = false;
      if (keepDirtySessionAcrossConflict) {
        expectedRecord = { ...expectedRecord, version: sessionVersion };
        changedOnServer = true;
      }
      if (!document) {
        document = normalizeStoredMap(mapRecord?.document, nextCourses, nextAssignments);
        if (!document) {
          document = buildDefaultCourseMap(rootId, nextCourses, nextAssignments);
          isDirty = Boolean(editorMode && (document.nodes.length || document.edges.length));
        }
      } else {
        document = normalizeStoredMap(document, nextCourses, nextAssignments) || buildDefaultCourseMap(rootId, nextCourses, nextAssignments);
      }

      setAssignments(nextAssignments);
      setRecord(expectedRecord);
      recordRef.current = expectedRecord;
      setDirty(isDirty);
      dirtyRef.current = isDirty;
      setServerChanged(changedOnServer);
      viewportRef.current = document.viewport || { x: 0, y: 0, zoom: 1 };

      const idx = buildEntityIndex(nextCourses, nextAssignments);
      const progress = computeCourseProgress(document.nodes || [], document.edges || [], nextAssignments, rootId);
      const nextNodes = (document.nodes || []).map((node) => {
        const isCourse = node.type === 'course';
        const entity = isCourse ? idx.courseById.get(String(node.entityId)) : idx.assignmentById.get(String(node.entityId));
        return {
          ...node,
          type: isCourse ? 'course' : assignmentNodeType(entity?.type || node.type),
          className: '',
          data: {
            entityId: node.entityId,
            entity,
            progress: isCourse ? (progress.get(String(node.id)) || { total: 0, solved: 0, percent: 0 }) : null,
            editorMode,
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
      requestAnimationFrame(() => {
        try { flow.setViewport(document.viewport || { x: 0, y: 0, zoom: 1 }, { duration: 0 }); } catch {}
      });
    } catch (error) {
      if (requestId === loadRequestRef.current) notify.error(getApiErrorMessage(error, 'Не удалось загрузить карту курса'));
    } finally {
      if (requestId === loadRequestRef.current && initialForRoot) setLoading(false);
    }
  }, [allCourses, course, decorateEdges, editAssignment, editCourse, editorMode, flow, focusBranch, focusNode, notify, openAssignment, rootId, setEdges, setNodes]);

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
    if (dataRevisionRef.current === dataRevision) return;
    dataRevisionRef.current = dataRevision;
    if (!rootId) return;
    let disposed = false;
    getAssignmentsByCourseTree(rootId).then((rows) => { if (!disposed) setAssignments(Array.isArray(rows) ? rows : []); }).catch(() => {});
    return () => { disposed = true; };
  }, [dataRevision, rootId]);

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
      const next = addEdge({
        ...connection,
        sourceHandle: connection.sourceHandle || 'out',
        targetHandle: connection.targetHandle || 'in',
        id: `edge:${Date.now()}:${Math.random().toString(36).slice(2, 8)}`,
        ...edgeStyle(true),
      }, current);
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
    if (!editorMode || !courseCanEdit) return;
    event.preventDefault();
    event.stopPropagation();
    setContext({ open: true, x: event.clientX, y: event.clientY, flowPosition: null, node, edge: null });
  }, [courseCanEdit, editorMode]);

  const closeContext = React.useCallback(() => setContext((current) => ({ ...current, open: false })), []);

  const appendNode = React.useCallback((node) => {
    setNodes((current) => {
      const next = [...current, node];
      nodesRef.current = next;
      if (rootId) {
        setCourseMapSessionState(rootId, {
          version: recordRef.current.version || 0,
          dirty: true,
          document: serializeCourseMap(next, edgesRef.current, viewportRef.current),
        });
      }
      return next;
    });
    markDirty();
    setGraphRevision((value) => value + 1);
  }, [markDirty, rootId, setNodes]);

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

  const save = React.useCallback(async () => {
    if (!editorMode || !dirtyRef.current || !rootId) return;
    try {
      const document = serializeCourseMap(nodesRef.current, edgesRef.current, viewportRef.current);
      const saved = await saveCourseMap(rootId, Number(recordRef.current.version || 0), document);
      setRecord(saved);
      recordRef.current = saved;
      setDirty(false);
      dirtyRef.current = false;
      setServerChanged(false);
      setCourseMapSessionState(rootId, { version: saved.version || 0, dirty: false, document });
      notify.success('Карта сохранена');
    } catch (error) {
      if (error?.response?.status === 409) {
        setServerChanged(true);
        notify.warn('Карту уже изменил другой редактор. Ваши локальные изменения сохранены на экране.');
        return;
      }
      notify.error(getApiErrorMessage(error, 'Не удалось сохранить карту'));
    }
  }, [editorMode, notify, rootId]);

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
    const pastedEdges = buffer.edges.map((edge, index) => ({
      id: `edge:copy:${stamp}:${serial}:${index}:${Math.random().toString(36).slice(2, 7)}`,
      source: idMap.get(String(edge.source)),
      target: idMap.get(String(edge.target)),
      sourceHandle: edge.sourceHandle || 'out',
      targetHandle: edge.targetHandle || 'in',
      ...edgeStyle(true),
    })).filter((edge) => edge.source && edge.target);
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

  React.useEffect(() => {
    if (!editorMode) return undefined;
    const onKeyDown = (event) => {
      if (isEditableShortcutTarget(event.target)) return;
      const mod = event.ctrlKey || event.metaKey;
      const key = String(event.key || '').toLowerCase();

      if (mod && key === 's') {
        event.preventDefault();
        void save();
        return;
      }
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
    clearCourseMapSessionState(rootId);
    await loadMap({ preferSession: false });
  }, [loadMap, notify, rootId]);

  const documentNow = React.useMemo(() => serializeCourseMap(nodes, edges, viewportRef.current), [edges, nodes]);
  const unplaced = React.useMemo(() => findUnplacedEntities(documentNow, visibleCourses, assignments), [assignments, documentNow, visibleCourses]);

  const placeUnplaced = React.useCallback((entry) => {
    const rect = shellRef.current?.getBoundingClientRect();
    const screenPoint = rect ? { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 } : { x: window.innerWidth / 2, y: window.innerHeight / 2 };
    const position = flow.screenToFlowPosition(screenPoint);
    const entity = entry.entity;
    const id = entry.kind === 'course' ? courseNodeId(entity.id) : assignmentNodeId(entity.id);
    appendNode({
      id,
      type: entry.type,
      entityId: String(entity.id),
      position,
      data: {
        entityId: String(entity.id),
        entity,
        progress: entry.kind === 'course' ? { total: 0, solved: 0, percent: 0 } : null,
        editorMode,
        onOpen: entry.kind === 'course' ? () => focusBranch(id) : () => openAssignment(entity.id),
        onFocus: entry.kind === 'course' ? () => focusBranch(id) : () => focusNode(id),
        onEdit: entry.kind === 'course' ? () => editCourse(entity.id) : () => editAssignment(entity.id),
      },
    });
    setUnplacedOpen(false);
  }, [appendNode, editAssignment, editCourse, editorMode, flow, focusBranch, focusNode, openAssignment]);

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
            <div className="course-map-unplaced" onMouseLeave={() => setUnplacedOpen(false)}>
              <button type="button" className="course-map-unplaced-trigger" onMouseEnter={() => setUnplacedOpen(true)} onClick={() => setUnplacedOpen((v) => !v)}>Неразмещённые · {unplaced.length}</button>
              {unplacedOpen ? (
                <div className="course-map-unplaced-popover">
                  <div className="course-map-popover-title">Не размещены на карте</div>
                  {unplaced.slice(0, 10).map((entry) => (
                    <button key={`${entry.kind}:${entry.entity.id}`} type="button" onClick={() => placeUnplaced(entry)}>
                      <span>{entry.kind === 'course' ? '◆' : '●'}</span><span>{entry.entity.title || 'Без названия'}</span>
                    </button>
                  ))}
                  {unplaced.length > 10 ? <div className="course-map-popover-more">+ ещё {unplaced.length - 10}</div> : null}
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
          {editorMode ? <Button className="course-map-save-button" onClick={save} disabled={!dirty}><Save size={15} /> {dirty ? 'Сохранить' : 'Сохранено'}</Button> : null}
        </div>
      </div>

      <div className="course-map-canvas">
        <ReactFlow
          nodes={nodes}
          edges={edges}
          nodeTypes={NODE_TYPES}
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
          onNodeContextMenu={onNodeContextMenu}
          onNodeDoubleClick={(event, node) => {
            event.preventDefault();
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
          multiSelectionKeyCode="Shift"
          selectionKeyCode="Shift"
          proOptions={{ hideAttribution: true }}
        >
          <Background gap={18} size={1.15} className="course-map-background" />
          <Controls showInteractive={false} className="course-map-controls" />
          <MiniMap pannable zoomable className="course-map-minimap" nodeStrokeWidth={2} />
        </ReactFlow>
      </div>

      <ContextMenu open={context.open} x={context.x} y={context.y} onClose={closeContext} ariaLabel="Действия карты курса">
        {context.edge ? (
          <>
            <ContextMenuLabel>Связь</ContextMenuLabel>
            <ContextMenuItem icon={Unlink2} danger onClick={() => { const edge = context.edge; closeContext(); removeEdges([String(edge.id)]); }}>Разорвать связь</ContextMenuItem>
          </>
        ) : context.node ? (
          <>
            <ContextMenuLabel>{context.node.type === 'course' ? 'Курс' : 'Задание'}</ContextMenuLabel>
            <ContextMenuItem icon={Eye} onClick={() => { const node = context.node; closeContext(); if (node.type === 'course') focusBranch(node.id); else openAssignment(node.entityId); }}>Открыть</ContextMenuItem>
            <ContextMenuItem icon={Pencil} onClick={() => { const node = context.node; closeContext(); if (node.type === 'course') editCourse(node.entityId); else editAssignment(node.entityId); }}>Редактировать</ContextMenuItem>
            <ContextMenuSeparator />
            <ContextMenuItem icon={X} disabled={context.node.type === 'course' && String(context.node.entityId) === rootId} onClick={() => { const node = context.node; closeContext(); removeNodeFromMap(node); }}>Убрать с карты</ContextMenuItem>
            <ContextMenuItem icon={Trash2} disabled={context.node.type === 'course' && String(context.node.entityId) === rootId} danger onClick={() => { const node = context.node; closeContext(); void deleteEntity(node); }}>Удалить полностью</ContextMenuItem>
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
