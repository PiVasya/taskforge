export const COOKIE_SESSION_UPDATED_EVENT = 'taskforge:cookie-session-updated';
export const SESSION_STORAGE_EVENT_KEY = 'taskforge.auth.session-event.v1';
export const BROWSER_REQUEST_HEADERS = Object.freeze({ 'X-TaskForge-CSRF': '1' });

export function sessionExpiresAt(session) {
  const value = Date.parse(session?.expiresAtUtc || '');
  return Number.isFinite(value) && value > 0 ? value : null;
}

export function readSessionInfo(value) {
  if (value?.authenticated !== true || !value.user || !sessionExpiresAt(value)) {
    throw new Error('Invalid cookie session response.');
  }
  return {
    authenticated: true,
    user: value.user,
    rememberMe: value.rememberMe === true,
    expiresAtUtc: value.expiresAtUtc,
  };
}

export function prepareCookieRequest(config) {
  config.withCredentials = true;
  const headers = config.headers || (config.headers = {});
  // Drop even stale/replayed headers supplied by an older caller.
  if (typeof headers.delete === 'function') headers.delete('Authorization');
  for (const name of Object.keys(headers)) {
    if (name.toLowerCase() === 'authorization') delete headers[name];
  }
  const method = String(config.method || 'get').toUpperCase();
  if (!['GET', 'HEAD', 'OPTIONS'].includes(method)) {
    if (typeof headers.set === 'function') headers.set('X-TaskForge-CSRF', '1');
    else headers['X-TaskForge-CSRF'] = '1';
  }
  return config;
}

// Serialize cookie-changing operations. Web Locks additionally coordinate tabs
// when available; no credential is ever put into storage or an event.
export function createCookieSessionClient(send, onSession = () => {}) {
  let tail = Promise.resolve();
  let refreshPromise = null;
  let logoutPromise = null;
  const enqueue = (action) => {
    const result = tail.then(() => {
      const locks = typeof navigator !== 'undefined' ? navigator.locks : null;
      return locks?.request ? locks.request('taskforge-cookie-session', action) : action();
    });
    tail = result.catch(() => {});
    return result;
  };
  const receive = (data) => {
    const session = readSessionInfo(data);
    onSession(session);
    return session;
  };
  return {
    login: (payload) => enqueue(() => send('/api/auth/login', payload).then(readSessionInfo)),
    remember: (rememberMe) => enqueue(() => send('/api/auth/remember', { rememberMe })),
    refresh() {
      if (logoutPromise) return Promise.reject(new Error('Logout is in progress.'));
      if (!refreshPromise) {
        refreshPromise = enqueue(() => send('/api/auth/refresh', null).then(receive))
          .finally(() => { refreshPromise = null; });
      }
      return refreshPromise;
    },
    logout() {
      if (!logoutPromise) {
        logoutPromise = enqueue(() => send('/api/auth/logout', null))
          .finally(() => { logoutPromise = null; });
      }
      return logoutPromise;
    },
  };
}

export function publishSessionChange(kind) {
  try {
    window.localStorage.setItem(SESSION_STORAGE_EVENT_KEY, JSON.stringify({ kind, nonce: `${Date.now()}:${Math.random()}` }));
  } catch {}
}
