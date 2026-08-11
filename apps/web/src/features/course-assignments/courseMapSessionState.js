const mapState = new Map();

function clean(value) {
  return String(value || '').trim();
}

function stateKey(courseId, options = {}) {
  const scope = clean(options.scope) || 'editor';
  const userId = clean(options.userId) || 'anonymous';
  return `${scope}:${userId}:${clean(courseId)}`;
}

export function getCourseMapSessionState(courseId, options = {}) {
  const value = mapState.get(stateKey(courseId, options)) || null;
  const expectedMode = clean(options.scope) || 'editor';
  if (value?.sourceMode && clean(value.sourceMode) !== expectedMode) return null;
  return value;
}

export function setCourseMapSessionState(courseId, value, options = {}) {
  const rootId = clean(options.rootCourseId || courseId);
  if (!rootId) return;
  const ids = new Set([rootId, clean(courseId), ...(options.aliases || []).map(clean)].filter(Boolean));
  const nextValue = { ...value, sourceMode: clean(options.scope) || 'editor' };
  for (const id of ids) mapState.set(stateKey(id, options), nextValue);
}

export function clearCourseMapSessionState(courseId, options = {}) {
  const value = getCourseMapSessionState(courseId, options);
  const ids = new Set([clean(courseId), clean(options.rootCourseId), ...(options.aliases || []).map(clean)].filter(Boolean));
  if (value?.aliases) value.aliases.forEach((id) => ids.add(clean(id)));
  for (const id of ids) mapState.delete(stateKey(id, options));
}

export function clearAllCourseMapSessionStates() {
  mapState.clear();
}
