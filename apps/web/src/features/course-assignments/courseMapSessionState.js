const mapState = new Map();

export function getCourseMapSessionState(courseId) {
  return mapState.get(String(courseId || '')) || null;
}

export function setCourseMapSessionState(courseId, value) {
  if (!courseId) return;
  mapState.set(String(courseId), value);
}

export function clearCourseMapSessionState(courseId) {
  mapState.delete(String(courseId || ''));
}
