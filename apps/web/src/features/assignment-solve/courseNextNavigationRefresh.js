import { getAssignmentsByCourseTree } from '../../api/assignments';
import { getLearningCourseMap } from '../../api/courseMaps';
import { writeCourseMapLocalCache } from '../course-assignments/courseMapLocalCache';

export async function refreshCourseNextNavigationProjection({
  projectionCourseId,
  courseId,
  currentUserId,
  cached,
}) {
  try {
    const [freshMap, freshAssignments] = await Promise.all([
      getLearningCourseMap(projectionCourseId),
      getAssignmentsByCourseTree(projectionCourseId),
    ]);
    const assignments = Array.isArray(freshAssignments) ? freshAssignments : [];
    const now = Date.now();
    const mapRecord = {
      ...freshMap,
      projectionToken: '',
      projectionRevision: 0,
      fullSyncAt: now,
      verifiedAt: now,
    };

    writeCourseMapLocalCache({
      courseId: projectionCourseId,
      rootCourseId: mapRecord.rootCourseId || cached?.rootCourseId || projectionCourseId,
      aliases: cached?.aliases || [courseId],
      editorMode: false,
      userId: currentUserId,
      mapRecord,
      assignments,
      courses: cached?.courses || [],
    });

    return { mapRecord, assignments };
  } catch {
    const mapRecord = cached?.mapRecord
      ? { ...cached.mapRecord, projectionToken: '', projectionRevision: 0 }
      : null;
    const assignments = cached?.assignments || [];
    if (mapRecord) {
      writeCourseMapLocalCache({
        courseId: projectionCourseId,
        rootCourseId: mapRecord.rootCourseId || cached?.rootCourseId || projectionCourseId,
        aliases: cached?.aliases || [courseId],
        editorMode: false,
        userId: currentUserId,
        mapRecord,
        assignments,
        courses: cached?.courses || [],
      });
    }
    return { mapRecord, assignments };
  }
}
