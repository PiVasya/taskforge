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
import { Eye, FileCode2, FolderTree, Image as ImageIcon, Pencil, Save, Sigma, Trash2, X, ListChecks, RotateCcw } from 'lucide-react';
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
  };
}

function CourseMapInner({ course, allCourses, courseCanEdit, editorMode, query = '', focusCourseId = '', dataRevision = 0, onRefreshCourseData }) {
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
  const [context, setContext] = React.useState({ open: false, x: 0, y: 0, flowPosition: null, node: null });
  const [unplacedOpen, setUnplacedOpen] = React.useState(false);
  const [serverChanged, setServerChanged] = React.useState(false);
  const [interacting, setInteracting] = React.useState(false);
  const [graphRevision, setGraphRevision] = React.useState(0);
  const viewportRef = React.useRef({ x: 0, y: 0, zoom: 1 });
  const nodesRef = React.useRef([]);
  const edgesRef = React.useRef([]);
  const recordRef = React.useRef(record);
  const dirtyRef = React.useRef(dirty);
  const presenceIdsRef = React.useRef(new Set());
  const focusedCourseRef = React.useRef('');
  const dataRevisionRef = React.useRef(dataRevision);
  const loadMapRef = React.useRef(null);

  const rootId = String(course?.id || '');
  const visibleCourses = React.useMemo(() => subtreeCourses(rootId, allCourses, course), [allCourses, course, rootId]);
  const entityIndex = React.useMemo(() => buildEntityIndex(visibleCourses, assignments), [assignments, visibleCourses]);
  const currentUserId = String(user?.id || user?.userId || user?.uuid || '');

  React.useEffect(() => { recordRef.current = record; }, [record]);
  React.useEffect(() => { dirtyRef.current = dirty; }, [dirty]);
  React.useEffect(() => { nodesRef.current = nodes; }, [nodes]);
  React.useEffect(() => { edgesRef.current = edges; }, [edges]);

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
      const progress = isCourse ? (progressByCourse.get(String(node.entityId)) || { total: 0, solved: 0, percent: 0 }) : null;
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
    if (!quiet) setLoading(true);
    try {
      const [mapRecord, treeAssignments] = await Promise.all([getCourseMap(rootId), getAssignmentsByCourseTree(rootId)]);
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
            progress: isCourse ? (progress.get(String(node.entityId)) || { total: 0, solved: 0, percent: 0 }) : null,
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
      requestAnimationFrame(() => {
        try { flow.setViewport(document.viewport || { x: 0, y: 0, zoom: 1 }, { duration: 0 }); } catch {}
      });
    } catch (error) {
      notify.error(getApiErrorMessage(error, 'Не удалось загрузить карту курса'));
    } finally {
      if (!quiet) setLoading(false);
    }
  }, [allCourses, course, decorateEdges, editAssignment, editCourse, editorMode, flow, focusBranch, focusNode, notify, openAssignment, rootId, setEdges, setNodes]);

  React.useEffect(() => { void loadMap({ preferSession: true }); }, [loadMap]);
  React.useEffect(() => { loadMapRef.current = loadMap; }, [loadMap]);

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
    setEdges((current) => {
      const next = current.filter((item) => item.id !== edge.id);
      edgesRef.current = next;
      return next;
    });
    markDirty();
    setGraphRevision((value) => value + 1);
  }, [editorMode, markDirty, setEdges]);

  const onPaneContextMenu = React.useCallback((event) => {
    if (!editorMode || !courseCanEdit) return;
    event.preventDefault();
    const position = flow.screenToFlowPosition({ x: event.clientX, y: event.clientY });
    setContext({ open: true, x: event.clientX, y: event.clientY, flowPosition: position, node: null });
  }, [courseCanEdit, editorMode, flow]);

  const onNodeContextMenu = React.useCallback((event, node) => {
    if (!editorMode || !courseCanEdit) return;
    event.preventDefault();
    event.stopPropagation();
    setContext({ open: true, x: event.clientX, y: event.clientY, flowPosition: null, node });
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

  const deleteEntity = React.useCallback(async (node) => {
    if (!node) return;
    const entity = node.data?.entity;
    const label = node.type === 'course' ? 'курс' : 'задание';
    const ok = await notify.confirm({ title: `Удалить ${label}?`, message: entity?.title || 'Действие необратимо.', okText: 'Удалить', cancelText: 'Отмена' });
    if (!ok) return;
    try {
      if (node.type === 'course') await deleteCourse(node.entityId);
      else await deleteAssignment(node.entityId);
      removeNodeFromMap(node);
      if (node.type !== 'course') setAssignments((current) => current.filter((item) => String(item.id) !== String(node.entityId)));
      await onRefreshCourseData?.();
      notify.success(node.type === 'course' ? 'Курс удалён' : 'Задание удалено');
    } catch (error) {
      notify.error(getApiErrorMessage(error, `Не удалось удалить ${label}`));
    }
  }, [notify, onRefreshCourseData, removeNodeFromMap, setAssignments]);

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
    <div className={`course-map-shell${editorMode ? ' is-editor' : ' is-viewer'}${interacting || context.open ? ' is-interacting' : ''}`} ref={shellRef} data-taskforge-agent-role="course-map" data-taskforge-ready="true">
      <div className="course-map-toolbar">
        <div className="course-map-toolbar-left">
          <span className="course-map-toolbar-title">Карта курса</span>
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

          {editorMode ? <Button onClick={save} disabled={!dirty}><Save size={15} /> {dirty ? 'Сохранить карту' : 'Сохранено'}</Button> : null}
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
          proOptions={{ hideAttribution: true }}
        >
          <Background gap={18} size={1.15} className="course-map-background" />
          <Controls showInteractive={false} className="course-map-controls" />
          <MiniMap pannable zoomable className="course-map-minimap" nodeStrokeWidth={2} />
        </ReactFlow>
      </div>

      <ContextMenu open={context.open} x={context.x} y={context.y} onClose={closeContext} ariaLabel="Действия карты курса">
        {context.node ? (
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
