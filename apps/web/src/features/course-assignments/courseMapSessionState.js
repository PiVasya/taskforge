const mapState = new Map();

function clean(value) {
  return String(value || '').trim();
}

function numericRevision(value) {
  const parsed = Number(value || 0);
  return Number.isFinite(parsed) && parsed > 0 ? parsed : 0;
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

export function learnerCourseMapSessionMatchesProjection(value, mapRecord) {
  if (!value?.document || !mapRecord) return false;

  const sessionToken = clean(value.projectionToken);
  const recordToken = clean(mapRecord?.projectionToken);
  const sessionRevision = numericRevision(value.projectionRevision);
  const recordRevision = numericRevision(mapRecord?.projectionRevision);

  // Learner graph version changes only when the editor changes the authored map.
  // Progression changes use immutable projection tokens/revisions instead. Never
  // let an older in-memory document override a newer cached projection merely
  // because both documents still have the same authored map version.
  if (sessionToken || recordToken) {
    return Boolean(sessionToken)
      && Boolean(recordToken)
      && sessionToken === recordToken
      && sessionRevision === recordRevision;
  }

  return sessionRevision === recordRevision
    && Number(value.version || 0) === Number(mapRecord?.version || 0);
}

export function setCourseMapSessionState(courseId, value, options = {}) {
  const rootId = clean(options.rootCourseId || courseId);
  if (!rootId) return;
  const ids = new Set([rootId, clean(courseId), ...(options.aliases || []).map(clean)].filter(Boolean));
  const nextValue = {
    ...value,
    sourceMode: clean(options.scope) || 'editor',
    projectionToken: clean(value?.projectionToken),
    projectionRevision: numericRevision(value?.projectionRevision),
  };
  for (const id of ids) mapState.set(stateKey(id, options), nextValue);
}

export function clearCourseMapSessionState(courseId, options = {}) {
  const value = getCourseMapSessionState(courseId, options);
  const ids = new Set([clean(courseId), clean(options.rootCourseId), ...(options.aliases || []).map(clean)].filter(Boolean));
  if (value?.aliases) value.aliases.forEach((id) => ids.add(clean(id)));
  for (const id of ids) mapState.delete(stateKey(id, options));
}

export function clearLearnerCourseMapSessionStates(userId = '') {
  const prefix = `learner:${clean(userId) || 'anonymous'}:`;
  for (const key of Array.from(mapState.keys())) {
    if (key.startsWith(prefix)) mapState.delete(key);
  }
}

export function clearAllCourseMapSessionStates() {
  mapState.clear();
}
