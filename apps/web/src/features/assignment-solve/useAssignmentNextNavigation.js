import React from 'react';
import { useNavigate } from 'react-router-dom';
import { useAuth } from '../../auth/AuthContext';
import { useQueryClient } from '../../data/QueryClientProvider';
import { getAssignmentsByCourse } from '../../api/assignments';
import { getLearningCourseMapDelta } from '../../api/courseMaps';
import { readCourseMapLocalCache, readCourseMapLocalCacheAsync, writeCourseMapLocalCache } from '../course-assignments/courseMapLocalCache';
import { buildNextNodeOptions, buildSortedFallbackNext, courseMapContainsAssignment } from '../course-assignments/courseMapNextNodes';
import {
  isAssignmentProgressionConfirmed,
  markAssignmentProgressionCompleted,
} from '../course-assignments/courseProgressionFreshness';
import { refreshCourseNextNavigationProjection } from './courseNextNavigationRefresh';

const RETRY_DELAYS_MS = [120, 220, 420, 760, 1300, 2200];

function clean(value) {
  return String(value || '').trim();
}

function mergeDeltaIntoCache(cached, delta) {
  const currentDocument = cached.mapRecord.document || { nodes: [], edges: [], viewport: { x: 0, y: 0, zoom: 1 } };
  const removeNodeIds = new Set((delta.nodeIdsRemoved || []).map(String));
  const removeEdgeIds = new Set((delta.edgeIdsRemoved || []).map(String));
  const nodeById = new Map((currentDocument.nodes || [])
    .filter((node) => !removeNodeIds.has(String(node?.id)))
    .map((node) => [String(node.id), node]));
  for (const node of delta.nodesAdded || []) {
    if (node?.id) nodeById.set(String(node.id), node);
  }
  const edgeById = new Map((currentDocument.edges || [])
    .filter((edge) => !removeEdgeIds.has(String(edge?.id)) && !removeNodeIds.has(String(edge?.source)) && !removeNodeIds.has(String(edge?.target)))
    .map((edge) => [String(edge.id), edge]));
  for (const edge of delta.edgesAdded || []) {
    if (edge?.id) edgeById.set(String(edge.id), edge);
  }

  const assignmentById = new Map((cached.assignments || []).map((item) => [String(item?.id || ''), item]));
  for (const item of delta.assignmentsChanged || []) {
    if (!item?.id) continue;
    const previous = assignmentById.get(String(item.id)) || {};
    assignmentById.set(String(item.id), {
      ...previous,
      ...item,
      isSolved: item.solvedByCurrentUser === true,
      progressStatus: item.solvedByCurrentUser === true ? 'solved' : 'not-started',
    });
  }

  const courseProgress = { ...(currentDocument.courseProgress || {}), ...(delta.courseProgress || {}) };
  const mapRecord = {
    ...cached.mapRecord,
    version: Number(delta.version || cached.mapRecord.version || 0),
    projectionToken: delta.projectionToken || cached.mapRecord.projectionToken,
    projectionRevision: Number(delta.projectionRevision || cached.mapRecord.projectionRevision || 0),
    document: {
      ...currentDocument,
      courseProgressVersion: 1,
      courseProgress,
      nodes: Array.from(nodeById.values()),
      edges: Array.from(edgeById.values()),
    },
  };
  const assignments = Array.from(assignmentById.values());
  const courseById = new Map((cached.courses || []).map((item) => [String(item?.id || ''), item]));
  for (const item of delta.coursesChanged || []) {
    if (!item?.id) continue;
    const previous = courseById.get(String(item.id)) || {};
    courseById.set(String(item.id), { ...previous, ...item });
  }

  return {
    mapRecord,
    assignments,
    courses: Array.from(courseById.values()),
  };
}

export function useAssignmentNextNavigation({ assignment, assignmentId }) {
  const nav = useNavigate();
  const { user } = useAuth();
  const queryClient = useQueryClient();
  const [nextOptions, setNextOptions] = React.useState([]);
  const [nextNavigationLoading, setNextNavigationLoading] = React.useState(false);
  const [progressionRevision, setProgressionRevision] = React.useState(0);
  const retryRef = React.useRef({ assignmentId: '', attempts: 0, timer: null });

  const currentAssignmentId = clean(assignment?.id || assignmentId);
  const currentCourseId = clean(assignment?.courseId);
  const currentUserId = clean(user?.id || user?.userId || user?.uuid);

  const clearRetryTimer = React.useCallback(() => {
    if (retryRef.current.timer && typeof window !== 'undefined') window.clearTimeout(retryRef.current.timer);
    retryRef.current.timer = null;
  }, []);

  React.useEffect(() => {
    clearRetryTimer();
    retryRef.current = { assignmentId: currentAssignmentId, attempts: 0, timer: null };
    setProgressionRevision(0);
  }, [clearRetryTimer, currentAssignmentId]);

  React.useEffect(() => () => clearRetryTimer(), [clearRetryTimer]);

  React.useEffect(() => {
    let alive = true;
    if (!currentCourseId || !currentAssignmentId) {
      setNextOptions([]);
      setNextNavigationLoading(false);
      return () => { alive = false; };
    }

    const applyNavigation = (mapRecord, rows) => {
      if (!alive) return;
      const assignments = Array.isArray(rows) ? rows : [];
      if (mapRecord?.document) {
        setNextOptions(courseMapContainsAssignment(mapRecord.document, currentAssignmentId)
          ? buildNextNodeOptions(mapRecord.document, currentAssignmentId, assignments)
          : []);
        return;
      }
      setNextOptions(buildSortedFallbackNext(assignments, currentAssignmentId));
    };

    const scheduleRetry = () => {
      if (!alive || !progressionRevision || typeof window === 'undefined') return;
      if (retryRef.current.assignmentId !== currentAssignmentId) {
        retryRef.current = { assignmentId: currentAssignmentId, attempts: 0, timer: null };
      }
      const attempt = retryRef.current.attempts;
      if (attempt >= RETRY_DELAYS_MS.length) {
        setNextNavigationLoading(false);
        return;
      }
      retryRef.current.attempts = attempt + 1;
      clearRetryTimer();
      retryRef.current.timer = window.setTimeout(() => {
        retryRef.current.timer = null;
        setProgressionRevision((value) => value + 1);
      }, RETRY_DELAYS_MS[attempt]);
    };

    const confirmProgression = (rows) => {
      if (!progressionRevision) return true;
      const confirmed = isAssignmentProgressionConfirmed(rows, currentAssignmentId);
      if (!confirmed) return false;
      clearRetryTimer();
      retryRef.current.attempts = 0;
      return true;
    };

    let cached = readCourseMapLocalCache({ courseId: currentCourseId, editorMode: false, userId: currentUserId });
    if (cached?.mapRecord) applyNavigation(cached.mapRecord, cached.assignments);
    setNextNavigationLoading(Boolean(progressionRevision));

    (async () => {
      if (!cached) {
        cached = await readCourseMapLocalCacheAsync({ courseId: currentCourseId, editorMode: false, userId: currentUserId });
        if (!alive) return;
        if (cached?.mapRecord) applyNavigation(cached.mapRecord, cached.assignments);
      }

      const forceFullProjection = Boolean(progressionRevision) && retryRef.current.attempts >= 3;
      const projectionToken = clean(cached?.mapRecord?.projectionToken);
      const projectionCourseId = clean(cached?.mapRecord?.requestedCourseId || cached?.requestedCourseId || currentCourseId);

      if (progressionRevision && (!projectionToken || forceFullProjection)) {
        const refreshed = await refreshCourseNextNavigationProjection({
          projectionCourseId,
          courseId: currentCourseId,
          currentUserId,
          cached,
        });
        if (!alive) return;
        if (refreshed.mapRecord) applyNavigation(refreshed.mapRecord, refreshed.assignments);
        if (confirmProgression(refreshed.assignments)) setNextNavigationLoading(false);
        else scheduleRetry();
        return;
      }

      if (!projectionToken) {
        if (!cached?.mapRecord) {
          try {
            const rows = await getAssignmentsByCourse(currentCourseId);
            if (alive) applyNavigation(null, rows);
          } catch {
            if (alive) setNextOptions([]);
          }
        }
        if (alive) setNextNavigationLoading(false);
        return;
      }

      try {
        const delta = await getLearningCourseMapDelta(
          projectionCourseId,
          projectionToken,
          progressionRevision ? currentAssignmentId : null,
        );
        if (!alive || !delta) return;
        if (delta.resetRequired) {
          const refreshed = await refreshCourseNextNavigationProjection({
            projectionCourseId,
            courseId: currentCourseId,
            currentUserId,
            cached,
          });
          if (!alive) return;
          if (refreshed.mapRecord) applyNavigation(refreshed.mapRecord, refreshed.assignments);
          if (confirmProgression(refreshed.assignments)) setNextNavigationLoading(false);
          else scheduleRetry();
          return;
        }

        const merged = mergeDeltaIntoCache(cached, delta);
        writeCourseMapLocalCache({
          courseId: projectionCourseId,
          rootCourseId: merged.mapRecord.rootCourseId || cached.rootCourseId || projectionCourseId,
          aliases: cached.aliases || [currentCourseId],
          editorMode: false,
          userId: currentUserId,
          mapRecord: merged.mapRecord,
          assignments: merged.assignments,
          courses: merged.courses,
          pendingRevealNodeIds: (delta.nodesAdded || []).map((node) => String(node?.id || '')).filter(Boolean),
        });
        applyNavigation(merged.mapRecord, merged.assignments);
        if (confirmProgression(merged.assignments)) setNextNavigationLoading(false);
        else scheduleRetry();
      } catch {
        if (!alive) return;
        if (progressionRevision) scheduleRetry();
        else if (!cached?.mapRecord) setNextOptions([]);
      } finally {
        if (alive && !progressionRevision) setNextNavigationLoading(false);
      }
    })();

    return () => { alive = false; };
  }, [clearRetryTimer, currentAssignmentId, currentCourseId, currentUserId, progressionRevision]);

  const refreshProgression = React.useCallback(() => {
    if (!currentAssignmentId) return;
    clearRetryTimer();
    retryRef.current = { assignmentId: currentAssignmentId, attempts: 0, timer: null };
    markAssignmentProgressionCompleted({
      queryClient,
      courseId: currentCourseId,
      assignmentId: currentAssignmentId,
      userId: currentUserId,
    });
    setNextNavigationLoading(true);
    setProgressionRevision((value) => value + 1);
  }, [clearRetryTimer, currentAssignmentId, currentCourseId, currentUserId, queryClient]);

  const goNextAssignment = React.useCallback((option = null) => {
    const target = option?.id ? option : nextOptions.find((item) => item?.id && !item.disabled);
    if (!target?.id) return;
    nav(`/assignment/${target.id}`);
  }, [nav, nextOptions]);

  return {
    nextOptions,
    nextNavigationLoading,
    refreshProgression,
    goNextAssignment,
  };
}
