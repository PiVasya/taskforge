export const FRONTEND_LOG_LIMIT_CHOICES_MB = Object.freeze([25, 50, 100, 250, 500]);
export const DEFAULT_FRONTEND_LOG_LIMIT_MB = 100;
export const MAX_FRONTEND_LOG_LIMIT_MB = 500;
export const MAX_DIAGNOSTIC_STRING = 4096;
export const MAX_DIAGNOSTIC_STACK = 16000;

const SENSITIVE_KEY_RE = /(authorization|access[_-]?token|refresh[_-]?token|password|passwd|cookie|set-cookie|secret|credential|csrf|private[_-]?key|api[_-]?key|phone|email|source[_-]?code|starter[_-]?code|reference[_-]?solution|solution[_-]?code|answer(?:s)?$)/i;
const URL_SECRET_KEY_RE = /(token|code|password|secret|key|auth|email|phone)/i;

export function clampFrontendLogLimitMb(value) {
  const n = Number(value);
  if (!Number.isFinite(n)) return DEFAULT_FRONTEND_LOG_LIMIT_MB;
  return Math.max(25, Math.min(MAX_FRONTEND_LOG_LIMIT_MB, Math.round(n)));
}

export function sanitizeDiagnosticUrl(value) {
  try {
    const parsed = new URL(String(value || ''), 'https://taskforge.invalid');
    const safe = new URLSearchParams();
    for (const [key, raw] of parsed.searchParams.entries()) {
      safe.append(key, URL_SECRET_KEY_RE.test(key) ? '[redacted]' : String(raw).slice(0, 160));
    }
    const query = safe.toString();
    return `${parsed.pathname}${query ? `?${query}` : ''}${parsed.hash ? '#…' : ''}`;
  } catch {
    return String(value || '').slice(0, MAX_DIAGNOSTIC_STRING);
  }
}

function sanitizeString(value, key = '') {
  if (SENSITIVE_KEY_RE.test(key)) return '[redacted]';
  let text = String(value);
  text = text
    .replace(/Bearer\s+[A-Za-z0-9._~+\/-]+=*/gi, 'Bearer [redacted]')
    .replace(/\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b/g, '[redacted-jwt]')
    .replace(/([?&](?:token|password|secret|key|auth)=)[^&#\s]*/gi, '$1[redacted]')
    .replace(/\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b/gi, '[redacted-email]');
  const max = /stack/i.test(key) ? MAX_DIAGNOSTIC_STACK : MAX_DIAGNOSTIC_STRING;
  return text.length > max ? `${text.slice(0, max)}…` : text;
}

export function sanitizeDiagnosticValue(value, options = {}, depth = 0, seen = new WeakSet(), key = '') {
  const maxDepth = Number.isFinite(options.maxDepth) ? options.maxDepth : 5;
  if (depth > maxDepth) return '[max-depth]';
  if (value == null || typeof value === 'boolean' || typeof value === 'number') return value;
  if (typeof value === 'string') return sanitizeString(value, key);
  if (typeof value === 'bigint') return String(value);
  if (typeof value === 'function') return `[function ${value.name || 'anonymous'}]`;
  if (value instanceof Date) return value.toISOString();
  if (typeof URL !== 'undefined' && value instanceof URL) return sanitizeDiagnosticUrl(value.toString());
  if (value instanceof Error) {
    return {
      name: sanitizeString(value.name || 'Error', 'name'),
      message: sanitizeString(value.message || '', 'message'),
      stack: sanitizeString(value.stack || '', 'stack'),
      code: sanitizeString(value.code || '', 'errorCode'),
    };
  }
  if (typeof value !== 'object') return sanitizeString(value, key);
  if (seen.has(value)) return '[circular]';
  seen.add(value);
  if (Array.isArray(value)) {
    const out = value.slice(0, 50).map((item, index) => sanitizeDiagnosticValue(item, options, depth + 1, seen, String(index)));
    if (value.length > 50) out.push(`[+${value.length - 50} more]`);
    return out;
  }
  const out = {};
  for (const [childKey, childValue] of Object.entries(value).slice(0, 80)) {
    out[childKey] = SENSITIVE_KEY_RE.test(childKey)
      ? '[redacted]'
      : sanitizeDiagnosticValue(childValue, options, depth + 1, seen, childKey);
  }
  return out;
}

export function describeDiagnosticTarget(target) {
  if (!target || typeof target !== 'object') return null;
  const element = target.closest?.('button,a,input,select,textarea,[role],[data-testid]') || target;
  const tag = String(element.tagName || '').toLowerCase() || 'unknown';
  const role = element.getAttribute?.('role') || undefined;
  const id = element.id || undefined;
  const name = element.getAttribute?.('name') || undefined;
  const testId = element.getAttribute?.('data-testid') || undefined;
  const ariaLabel = element.getAttribute?.('aria-label') || undefined;
  const title = element.getAttribute?.('title') || undefined;
  const href = tag === 'a' ? sanitizeDiagnosticUrl(element.getAttribute?.('href') || '') : undefined;
  return sanitizeDiagnosticValue({ tag, role, id, name, testId, ariaLabel, title, href });
}

export function approximateDiagnosticBytes(record) {
  try {
    return new TextEncoder().encode(JSON.stringify(record)).byteLength + 1;
  } catch {
    try { return JSON.stringify(record).length * 2 + 1; } catch { return 256; }
  }
}
