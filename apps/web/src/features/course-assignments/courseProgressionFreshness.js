export const COURSE_PROGRESSION_STORAGE_KEY = 'taskforge.course-progression.freshness.v1';
const STORAGE_KEY = COURSE_PROGRESSION_STORAGE_KEY;
export const COURSE_PROGRESSION_CHANGED_EVENT = 'taskforge:course-progression-changed';
const MAX_PENDING_ENTRIES = 24;
const MAX_PENDING_AGE_MS = 24 * 60 * 60 * 1000;

function clean(value) {
  return String(value || '').trim();
}

function normalizeUserId(userId) {
  return clean(userId) || 'anonymous';
}

function storage() {
  if (typeof window === 'undefined') return null;
  try { return window.localStorage; } catch { return null; }
}

function emptyState() {
  return { schema: 1, users: {} };
}

function readState() {
  const target = storage();
  if (!target) return emptyState();
  try {
    const parsed = JSON.parse(target.getItem(STORAGE_KEY) || 'null');
    if (!parsed || parsed.schema !== 1 || typeof parsed.users !== 'object') return emptyState();
    return parsed;
  } catch {
    return emptyState();
  }
}

function writeState(state) {
  const target = storage();
  if (!target) return;
  try { target.setItem(STORAGE_KEY, JSON.stringify(state)); } catch {}
}

function normalizeUserState(value) {
  const now = Date.now();
  const entries = (Array.isArray(value?.entries) ? value.entries : [])
    .filter((entry) => entry && clean(entry.assignmentId) && now - Number(entry.at || 0) <= MAX_PENDING_AGE_MS)
    .map((entry) => ({
      courseId: clean(entry.courseId),
      assignmentId: clean(entry.assignmentId),
      at: Number(entry.at || now),
    }))
    .slice(-MAX_PENDING_ENTRIES);
  return {
    revision: Number(value?.revision || 0),
    entries,
  };
}

export function getCourseProgressionRevision(userId = '') {
  const state = readState();
  return normalizeUserState(state.users?.[normalizeUserId(userId)]).revision;
}

export function markCourseProgressionChanged({ courseId = '', assignmentId = '', userId = '' } = {}) {
  const cleanAssignmentId = clean(assignmentId);
  if (!cleanAssignmentId) return 0;

  const state = readState();
  const key = normalizeUserId(userId);
  const current = normalizeUserState(state.users?.[key]);
  const now = Date.now();
  const nextRevision = Math.max(current.revision + 1, now);
  const entries = current.entries.filter((entry) => entry.assignmentId !== cleanAssignmentId);
  entries.push({ courseId: clean(courseId), assignmentId: cleanAssignmentId, at: now });
  state.users[key] = { revision: nextRevision, entries: entries.slice(-MAX_PENDING_ENTRIES) };
  writeState(state);

  try {
    window.dispatchEvent(new CustomEvent(COURSE_PROGRESSION_CHANGED_EVENT, {
      detail: { courseId: clean(courseId), assignmentId: cleanAssignmentId, userId: clean(userId), revision: nextRevision },
    }));
  } catch {}
  return nextRevision;
}

export function markAssignmentProgressionCompleted({ queryClient, courseId = '', assignmentId = '', userId = '' } = {}) {
  markCourseProgressionChanged({ courseId, assignmentId, userId });
  queryClient?.setQueryData(['page-state', 'courses'], (previous) => previous ? ({
    ...previous,
    progressLoadedAt: 0,
    progressIdsKey: '',
  }) : previous);
  queryClient?.invalidateQueries({ queryKey: ['course-assignments'], refetch: false });
}

export function listPendingCourseProgressionChanges({ courseIds = [], userId = '' } = {}) {
  const ids = new Set((Array.isArray(courseIds) ? courseIds : [courseIds]).map(clean).filter(Boolean));
  const state = readState();
  const current = normalizeUserState(state.users?.[normalizeUserId(userId)]);
  return current.entries
    .filter((entry) => ids.size === 0 || !entry.courseId || ids.has(entry.courseId))
    .sort((left, right) => left.at - right.at);
}

export function findPendingCourseProgressionChange(options = {}) {
  const matches = listPendingCourseProgressionChanges(options);
  return matches.length ? matches[matches.length - 1] : null;
}

export function clearPendingCourseProgressionChange({ assignmentId = '', userId = '' } = {}) {
  const cleanAssignmentId = clean(assignmentId);
  if (!cleanAssignmentId) return;
  const state = readState();
  const key = normalizeUserId(userId);
  const current = normalizeUserState(state.users?.[key]);
  const entries = current.entries.filter((entry) => entry.assignmentId !== cleanAssignmentId);
  if (entries.length === current.entries.length) return;
  state.users[key] = { ...current, entries };
  writeState(state);
}

export function clearPendingCourseProgressionChangesForCourses({ courseIds = [], userId = '' } = {}) {
  const ids = new Set((Array.isArray(courseIds) ? courseIds : [courseIds]).map(clean).filter(Boolean));
  if (!ids.size) return;
  const state = readState();
  const key = normalizeUserId(userId);
  const current = normalizeUserState(state.users?.[key]);
  const entries = current.entries.filter((entry) => entry.courseId && !ids.has(entry.courseId));
  if (entries.length === current.entries.length) return;
  state.users[key] = { ...current, entries };
  writeState(state);
}

export function isAssignmentProgressionConfirmed(rows, assignmentId) {
  const id = clean(assignmentId);
  if (!id) return false;
  const row = (Array.isArray(rows) ? rows : []).find((item) => clean(item?.id) === id);
  if (!row) return false;
  return row.solvedByCurrentUser === true
    || row.isSolved === true
    || clean(row.progressStatus).toLowerCase() === 'solved'
    || row.passed === true;
}
