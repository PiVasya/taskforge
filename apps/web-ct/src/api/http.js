import axios from 'axios';

let accessToken = null;

export function setAccessToken(token) {
  accessToken = token || null;
}

export function getAccessToken() {
  return accessToken;
}

const api = axios.create({ withCredentials: true });

function emitQuotaFromHeaders(headers, fallbackBucket, fallbackRetry) {
  try {
    if (typeof window === 'undefined') return;
    const h = headers || {};
    const bucket = (h['x-quota-bucket'] || h['X-Quota-Bucket'] || fallbackBucket || '').toString();
    if (!bucket) return;

    const remainingRaw = h['x-quota-remaining'] ?? h['X-Quota-Remaining'];
    const capacityRaw = h['x-quota-capacity'] ?? h['X-Quota-Capacity'];
    const retryRaw = h['x-quota-retry-after'] ?? h['X-Quota-Retry-After'] ?? h['retry-after'] ?? h['Retry-After'] ?? fallbackRetry;
    const nextRefillAtUtc = h['x-quota-next-refill-at'] ?? h['X-Quota-Next-Refill-At'] ?? undefined;

    window.dispatchEvent(new CustomEvent('quota:update', {
      detail: {
        bucket,
        remaining: remainingRaw == null ? undefined : Number(remainingRaw),
        capacity: capacityRaw == null ? undefined : Number(capacityRaw),
        retryAfterSeconds: retryRaw == null ? undefined : Number(retryRaw),
        nextRefillAtUtc,
      },
    }));
  } catch {
    
  }
}

export function getApiErrorMessage(error, fallback = 'Произошла ошибка') {
  const status = error?.response?.status;
  const data = error?.response?.data;

  if (typeof data === 'string' && data.trim()) return data.trim();

  const parts = [];
  if (data?.message) parts.push(data.message);
  if (data?.detail && data.detail !== data.message) parts.push(data.detail);
  if (data?.hint) parts.push(`Подсказка: ${data.hint}`);

  if (data?.errors && typeof data.errors === 'object') {
    Object.entries(data.errors).forEach(([field, value]) => {
      const msg = Array.isArray(value) ? value.join(', ') : String(value);
      parts.push(`${field}: ${msg}`);
    });
  }

  if (parts.length) return parts.join('\n');
  if (status === 400) return 'Некорректные данные. Проверь поля формы.';
  if (status === 401) return 'Нужно войти заново.';
  if (status === 403) return 'Недостаточно прав для этого действия.';
  if (status === 404) return 'Адрес API не найден. Проверь nginx route или выбранный объект.';
  if (status === 409) return 'Конфликт данных: такой slug или объект уже существует.';
  if (status >= 500) return 'Ошибка сервера. Открой логи backend-сервиса.';
  return error?.userMessage || error?.message || fallback;
}

api.interceptors.request.use((config) => {
  if (accessToken) {
    config.headers = config.headers || {};
    if (!config.headers.Authorization && !config.headers.authorization) {
      config.headers.Authorization = `Bearer ${accessToken}`;
    }
  }
  return config;
});

let isRefreshing = false;
let refreshQueue = [];

function resolveQueue(err) {
  refreshQueue.forEach(({ resolve, reject }) => (err ? reject(err) : resolve()));
  refreshQueue = [];
}

api.interceptors.response.use(
  (response) => {
    emitQuotaFromHeaders(response?.headers);
    return response;
  },
  async (error) => {
    emitQuotaFromHeaders(error?.response?.headers);

    const original = error.config || {};
    const status = error?.response?.status;
    const data = error?.response?.data;

    if (status === 429) {
      const bucket = data?.bucket;
      const retry = data?.retryAfterSeconds ?? error?.response?.headers?.['retry-after'] ?? error?.response?.headers?.['Retry-After'];
      emitQuotaFromHeaders(error?.response?.headers, bucket, retry);
      const retrySec = retry ? Number(retry) : null;
      const niceBucket = bucket === 'top' ? 'топ' : bucket === 'tasks' ? 'решения' : 'лимит';
      error.userMessage = retrySec
        ? `Лимит исчерпан (${niceBucket}). Подождите ${retrySec} сек.`
        : `Лимит исчерпан (${niceBucket}). Попробуйте позже.`;
      error.message = error.userMessage;
    } else {
      error.userMessage = getApiErrorMessage(error);
      error.message = error.userMessage;
    }

    if (original.__skipAuthRefresh || status !== 401) {
      return Promise.reject(error);
    }

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
      if (res?.data?.accessToken) setAccessToken(res.data.accessToken);
      resolveQueue(null);
      return api(original);
    } catch (refreshError) {
      resolveQueue(refreshError);
      return Promise.reject(refreshError);
    } finally {
      isRefreshing = false;
    }
  }
);

export default api;
