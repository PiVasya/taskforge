import axios from 'axios';

const API_TELEMETRY_SLOW_MS = 750;
let accessToken = null;

export function setAccessToken(token) {
  accessToken = token || null;
}

export function getAccessToken() {
  return accessToken;
}

const api = axios.create({
  withCredentials: true,
});

function telemetryPath(url) {
  try {
    const raw = String(url || '');
    const parsed = new URL(raw, window.location.origin);
    return `${parsed.pathname}${parsed.search || ''}`;
  } catch {
    return String(url || '');
  }
}

function shouldSkipApiTelemetry(config = {}) {
  const path = telemetryPath(config.url || '').toLowerCase();
  if (!path.startsWith('/api/')) return true;
  if (path.startsWith('/api/activity/page-view')) return true;
  if (path.startsWith('/api/admin/analytics')) return true;
  if (path.startsWith('/api/admin/activity')) return true;
  if (path.startsWith('/api/auth/refresh')) return true;
  return false;
}

function safeErrorMessage(error) {
  const data = error?.response?.data;
  const raw = data?.userMessage || data?.message || data?.error || data?.detail || data?.title || error?.userMessage || error?.message;
  if (typeof raw !== 'string') return undefined;
  const trimmed = raw.trim();
  if (!trimmed || isTechnicalMessage(trimmed)) return undefined;
  return trimmed.slice(0, 1000);
}

function trackApiTelemetry(config = {}, statusCode, error) {
  try {
    if (typeof window === 'undefined') return;
    if (shouldSkipApiTelemetry(config)) return;
    const startedAt = Number(config?.metadata?.startedAt || Date.now());
    const durationMs = Math.max(0, Math.round(Date.now() - startedAt));
    const path = telemetryPath(config.url || '');
    const method = String(config.method || 'GET').toUpperCase();
    const normalizedStatus = Number(statusCode || 0) || null;
    const isError = normalizedStatus != null && normalizedStatus >= 400;
    if (!isError && durationMs < API_TELEMETRY_SLOW_MS) return;
    const payload = {
      path,
      method,
      action: isError ? 'api-error' : 'api-slow',
      source: 'api-client',
      statusCode: normalizedStatus,
      durationMs,
      traceId: error?.response?.data?.traceId || error?.response?.data?.trace || error?.response?.headers?.['x-trace-id'],
      errorCode: error?.response?.data?.code,
      errorMessage: isError ? safeErrorMessage(error) : undefined,
    };
    const body = JSON.stringify(payload);
    const headers = { 'Content-Type': 'application/json' };
    const token = accessToken;
    if (token) headers.Authorization = `Bearer ${token}`;
    fetch('/api/activity/page-view', {
      method: 'POST',
      credentials: 'include',
      keepalive: body.length < 60000,
      headers,
      body,
    }).catch(() => {});
  } catch {
  }
}

function emitQuotaFromHeaders(headers, fallbackBucket, fallbackRetry) {
  try {
    if (typeof window === 'undefined') return;
    const h = headers || {};
    const bucket = (h['x-quota-bucket'] || h['X-Quota-Bucket'] || fallbackBucket || '').toString();
    if (!bucket) return;

    const remRaw = h['x-quota-remaining'] ?? h['X-Quota-Remaining'];
    const capRaw = h['x-quota-capacity'] ?? h['X-Quota-Capacity'];
    const retryRaw = h['x-quota-retry-after'] ?? h['X-Quota-Retry-After'] ?? h['retry-after'] ?? h['Retry-After'] ?? fallbackRetry;
    const nextRefillAtUtc = h['x-quota-next-refill-at'] ?? h['X-Quota-Next-Refill-At'] ?? undefined;

    const remaining = remRaw == null ? undefined : Number(remRaw);
    const capacity = capRaw == null ? undefined : Number(capRaw);
    const retryAfterSeconds = retryRaw == null ? undefined : Number(retryRaw);

    window.dispatchEvent(
      new CustomEvent('quota:update', {
        detail: { bucket, remaining, capacity, retryAfterSeconds, nextRefillAtUtc },
      })
    );
  } catch {
  }
}

function isHtmlLike(value) {
  if (typeof value !== 'string') return false;
  const s = value.trim().toLowerCase();
  return s.startsWith('<!doctype') || s.startsWith('<html') || s.includes('<body') || s.includes('<script');
}

function isTechnicalMessage(value) {
  if (typeof value !== 'string') return false;
  const s = value.trim();
  if (!s) return true;
  return (
    /^request failed with status code \d+/i.test(s) ||
    /^network error$/i.test(s) ||
    /^timeout of \d+ms exceeded$/i.test(s) ||
    /^failed to fetch$/i.test(s) ||
    /\baxioserror\b/i.test(s) ||
    /\bxmlhttprequest\b/i.test(s) ||
    /\bnginx\b/i.test(s) ||
    /\bstack trace\b/i.test(s) ||
    isHtmlLike(s)
  );
}

function statusMessage(status, url = '') {
  const path = String(url || '').toLowerCase();

  if (status === 400) return 'Проверьте введённые данные и попробуйте ещё раз.';
  if (status === 401) {
    if (path.includes('/api/auth/login')) {
      return 'Неверный логин или пароль. Проверьте данные или зарегистрируйтесь.';
    }
    if (path.includes('/api/auth/refresh')) {
      return 'Сессия истекла. Войдите заново.';
    }
    return 'Нужно войти в систему, чтобы выполнить это действие.';
  }
  if (status === 403) return 'У вас нет доступа к этому действию.';
  if (status === 404) return 'Не удалось найти нужный раздел или объект. Обновите страницу и попробуйте ещё раз.';
  if (status === 409) return 'Данные уже изменились или конфликтуют. Обновите страницу и повторите действие.';
  if (status === 413) return 'Файл или запрос слишком большой.';
  if (status === 415) return 'Неподдерживаемый формат данных.';
  if (status === 422) return 'Проверьте заполнение формы и исправьте отмеченные поля.';
  if (status === 429) return 'Слишком много запросов. Подождите немного и попробуйте ещё раз.';
  if (status >= 500) return 'Сервис временно недоступен. Попробуйте чуть позже.';
  return 'Не удалось выполнить действие. Попробуйте ещё раз.';
}

function collectValidationErrors(data) {
  const rawErrors = data?.errors;
  if (!rawErrors || typeof rawErrors !== 'object') return [];
  return Object.entries(rawErrors)
    .flatMap(([field, value]) => {
      const items = Array.isArray(value) ? value : [value];
      return items
        .map((x) => (typeof x === 'string' ? x.trim() : ''))
        .filter(Boolean)
        .map((msg) => (field && field !== '$' && field !== 'form' ? `${field}: ${msg}` : msg));
    })
    .filter(Boolean);
}

function firstUserFacingServerMessage(data) {
  const candidates = [];
  if (typeof data === 'string') candidates.push(data);
  if (data && typeof data === 'object') {
    candidates.push(data.userMessage, data.message, data.error, data.detail, data.title);
  }
  return candidates
    .map((x) => (typeof x === 'string' ? x.trim() : ''))
    .find((x) => x && !isTechnicalMessage(x));
}

export function getApiErrorMessage(error, fallback = 'Не удалось выполнить действие') {
  const status = error?.response?.status;
  const url = error?.config?.url || error?.response?.config?.url || '';
  const data = error?.response?.data;
  const validation = collectValidationErrors(data);
  const serverMessage = firstUserFacingServerMessage(data);

  if (validation.length > 0) return [serverMessage || statusMessage(status, url), ...validation].filter(Boolean).join('\n');
  if (serverMessage) return serverMessage;
  if (status) return statusMessage(status, url);
  if (error?.code === 'ECONNABORTED') return 'Сервер слишком долго отвечает. Попробуйте ещё раз.';
  if (!error?.response) return 'Нет связи с сервером. Проверьте подключение и повторите попытку.';

  const raw = typeof error?.message === 'string' ? error.message.trim() : '';
  if (raw && !isTechnicalMessage(raw)) return raw;
  return fallback;
}

export function normalizeApiError(error, fallback = 'Не удалось выполнить действие') {
  const status = error?.response?.status ?? null;
  const data = error?.response?.data;
  const primaryMessage = getApiErrorMessage(error, fallback);
  const validation = collectValidationErrors(data);
  const messages = [...new Set([primaryMessage, ...validation].filter(Boolean))];
  const traceId = data?.traceId || data?.trace || null;
  const code = data?.code || null;
  const path = data?.path || null;
  const severity = status === 400 || status === 422 ? 'validation' : status === 401 || status === 403 || status === 404 || status === 409 || status === 429 ? 'warning' : 'error';

  error.userMessage = primaryMessage;
  error.message = primaryMessage;
  error.normalized = {
    status,
    primaryMessage,
    messages,
    userMessage: messages.join('\n'),
    trace: traceId,
    traceId,
    path,
    code,
    userHint: data?.userHint || null,
    howToFix: Array.isArray(data?.howToFix) ? data.howToFix : [],
    severity,
    fieldErrors: data?.errors || null,
  };
  return error;
}

api.interceptors.request.use((config) => {
  config.headers = config.headers || {};
  config.metadata = { ...(config.metadata || {}), startedAt: Date.now() };
  const token = accessToken;
  if (token) {
    if (!config.headers.Authorization && !config.headers.authorization) {
      config.headers.Authorization = `Bearer ${token}`;
    }
  }
  return config;
});

let isRefreshing = false;
let refreshQueue = [];

function resolveQueue(err) {
  refreshQueue.forEach(({ resolve, reject }) => {
    if (err) reject(err);
    else resolve();
  });
  refreshQueue = [];
}

api.interceptors.response.use(
  (response) => {
    emitQuotaFromHeaders(response?.headers);
    trackApiTelemetry(response?.config || {}, response?.status);
    return response;
  },
  async (error) => {
    const original = error.config || {};
    const status = error?.response?.status;
    const url = (original.url || '').toLowerCase();
    const refreshableAuthError = status === 401 && !url.includes('/api/auth/login') && !url.includes('/api/auth/refresh') && !original.__skipAuthRefresh;
    if (!refreshableAuthError) trackApiTelemetry(original, status, error);

    if (status === 429) {
      const bucket = error?.response?.data?.bucket;
      const retry =
        error?.response?.data?.retryAfterSeconds ??
        (error?.response?.headers && (error.response.headers['retry-after'] || error.response.headers['Retry-After']));

      emitQuotaFromHeaders(error?.response?.headers, bucket, retry);

      const retrySec = retry ? Number(retry) : null;
      const niceBucket = bucket === 'top' ? 'топ' : bucket === 'tasks' ? 'решения' : 'лимит';
      const msg = retrySec
        ? `Лимит исчерпан (${niceBucket}). Подождите ${retrySec} сек.`
        : `Лимит исчерпан (${niceBucket}). Попробуйте позже.`;

      error.userMessage = msg;
      error.message = msg;
      if (error.response && typeof error.response.data === 'object' && error.response.data) {
        if (!error.response.data.error) error.response.data.error = msg;
        if (error.response.data.message === 'Quota exceeded') error.response.data.message = msg;
      }
    } else {
      emitQuotaFromHeaders(error?.response?.headers);
      normalizeApiError(error);
    }

    if (original.__skipAuthRefresh) {
      return Promise.reject(error);
    }

    if (status !== 401) {
      return Promise.reject(error);
    }

    if (url.includes('/api/auth/login') || url.includes('/api/auth/refresh')) {
      return Promise.reject(error);
    }

    if (isRefreshing) {
      return new Promise((resolve, reject) => {
        refreshQueue.push({ resolve, reject });
      }).then(() => api(original));
    }

    isRefreshing = true;
    try {
      const res = await api.post('/api/auth/refresh', null, { __skipAuthRefresh: true });
      const newToken = res?.data?.accessToken;
      if (newToken) setAccessToken(newToken);

      resolveQueue(null);
      return api(original);
    } catch (e) {
      normalizeApiError(e);
      resolveQueue(e);
      return Promise.reject(e);
    } finally {
      isRefreshing = false;
    }
  }
);

export default api;
