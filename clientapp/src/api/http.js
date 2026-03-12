import axios from 'axios';

// Access token is kept only in memory (not localStorage).
// Requests use cookies (HttpOnly) for auth.
let accessToken = null;

export function setAccessToken(token) {
  accessToken = token || null;
}

export function getAccessToken() {
  return accessToken;
}

const api = axios.create({
  // For same-origin deployment cookies are sent automatically.
  // If you run FE/BE on different origins locally, this must be true.
  withCredentials: true,
});

// ===== Quota tunnel (без лишних запросов) =====
// Бэк возвращает заголовки X-Quota-* и Retry-After — пробрасываем их в UI через событие.
function emitQuotaFromHeaders(headers, fallbackBucket, fallbackRetry) {
  try {
    if (typeof window === 'undefined') return;
    const h = headers || {};
    const bucket = (h['x-quota-bucket'] || h['X-Quota-Bucket'] || fallbackBucket || '').toString();
    if (!bucket) return;

    const remRaw = h['x-quota-remaining'] ?? h['X-Quota-Remaining'];
    const capRaw = h['x-quota-capacity'] ?? h['X-Quota-Capacity'];
    const retryRaw = h['retry-after'] ?? h['Retry-After'] ?? fallbackRetry;

    const remaining = remRaw == null ? undefined : Number(remRaw);
    const capacity = capRaw == null ? undefined : Number(capRaw);
    const retryAfterSeconds = retryRaw == null ? undefined : Number(retryRaw);

    window.dispatchEvent(
      new CustomEvent('quota:update', {
        detail: { bucket, remaining, capacity, retryAfterSeconds },
      })
    );
  } catch {
    // ignore
  }
}

// Attach Authorization header when we have an in-memory access token.
// This makes auth robust even if cookies are blocked by browser policy.
api.interceptors.request.use((config) => {
  const token = accessToken;
  if (token) {
    config.headers = config.headers || {};
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
    // обновляем квоты из заголовков (если есть)
    emitQuotaFromHeaders(response?.headers);
    return response;
  },
  async (error) => {
    const original = error.config || {};

    // quota: не даём пользователю видеть "Request failed with status code 429"
    const status = error?.response?.status;
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

      // чтобы в UI никогда не показывалось "status code 429"
      error.message = msg;
      if (error.response && typeof error.response.data === 'object' && error.response.data) {
        // часть страниц читает data.error
        if (!error.response.data.error) error.response.data.error = msg;
        // часть страниц читает data.message
        if (error.response.data.message === 'Quota exceeded') error.response.data.message = msg;
      }
    } else {
      // даже на обычных ответах можем обновить квоты (если бэк их прислал)
      emitQuotaFromHeaders(error?.response?.headers);

      const data = error?.response?.data;
      const normalizedMsg =
        (typeof data === 'string' ? data : null) ||
        data?.message ||
        data?.error ||
        error?.message ||
        'Произошла ошибка';

      error.message = normalizedMsg;
      error.userMessage = normalizedMsg;
    }

    // prevent infinite loops
    if (original.__skipAuthRefresh) {
      return Promise.reject(error);
    }

    if (status !== 401) {
      return Promise.reject(error);
    }

    // do not try to refresh on auth endpoints
    const url = (original.url || '').toLowerCase();
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
      resolveQueue(e);
      return Promise.reject(e);
    } finally {
      isRefreshing = false;
    }
  }
);

export default api;
