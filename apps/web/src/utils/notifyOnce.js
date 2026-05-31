const _shown = new Map(); 

export function notifyOnce(key, fireFn, ttlMs = 2500) {
  if (_shown.has(key)) return;
  try { fireFn?.(); } finally {
    const t = setTimeout(() => _shown.delete(key), ttlMs);
    _shown.set(key, t);
  }
}
