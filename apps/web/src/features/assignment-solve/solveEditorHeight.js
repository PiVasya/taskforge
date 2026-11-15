export const MIN_SOLVE_EDITOR_HEIGHT = 200;
export const MAX_SOLVE_EDITOR_HEIGHT = 1600;

const KEY_PREFIX = 'taskforge:solve-editor-height:v1:';

export function normalizeSolveEditorHeight(value, fallback = 380) {
  const candidate = typeof value === 'number' || (typeof value === 'string' && value.trim())
    ? Number(value)
    : NaN;
  const fallbackNumber = Number(fallback);
  const safeValue = Number.isFinite(candidate)
    ? candidate
    : (Number.isFinite(fallbackNumber) ? fallbackNumber : 380);
  return Math.round(Math.min(MAX_SOLVE_EDITOR_HEIGHT, Math.max(MIN_SOLVE_EDITOR_HEIGHT, safeValue)));
}

function getEditorHeightKey(assignmentId) {
  const id = String(assignmentId || '').trim().toLowerCase();
  return id ? `${KEY_PREFIX}${id}` : null;
}

export function readSolveEditorHeight(assignmentId, fallback = 380, storage = null) {
  const defaultHeight = normalizeSolveEditorHeight(fallback);
  const key = getEditorHeightKey(assignmentId);
  if (!key) return defaultHeight;
  try {
    const target = storage || (typeof window !== 'undefined' ? window.localStorage : null);
    const raw = target?.getItem(key);
    return raw == null ? defaultHeight : normalizeSolveEditorHeight(raw, defaultHeight);
  } catch {
    return defaultHeight;
  }
}

export function writeSolveEditorHeight(assignmentId, height, storage = null) {
  const key = getEditorHeightKey(assignmentId);
  if (!key) return false;
  try {
    const target = storage || (typeof window !== 'undefined' ? window.localStorage : null);
    if (!target) return false;
    target.setItem(key, String(normalizeSolveEditorHeight(height)));
    return true;
  } catch {
    return false;
  }
}
