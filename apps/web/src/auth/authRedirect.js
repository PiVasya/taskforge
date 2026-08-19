export function locationToInternalPath(location) {
  if (!location) return '';
  const pathname = String(location.pathname || '/');
  const search = String(location.search || '');
  const hash = String(location.hash || '');
  return `${pathname}${search}${hash}`;
}

export function safeInternalPath(value, fallback = '/courses') {
  const candidate = String(value || '').trim();
  if (!candidate.startsWith('/') || candidate.startsWith('//') || candidate.includes('\\')) return fallback;

  try {
    const base = 'https://taskforge.local';
    const parsed = new URL(candidate, base);
    if (parsed.origin !== base) return fallback;
    return `${parsed.pathname}${parsed.search}${parsed.hash}`;
  } catch {
    return fallback;
  }
}

export function loginPathForLocation(location) {
  const next = locationToInternalPath(location);
  return next && next !== '/login'
    ? `/login?next=${encodeURIComponent(next)}`
    : '/login';
}
