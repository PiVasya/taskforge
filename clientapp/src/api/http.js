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
  (response) => response,
  async (error) => {
    const original = error.config || {};

    // prevent infinite loops
    if (original.__skipAuthRefresh) {
      return Promise.reject(error);
    }

    const status = error?.response?.status;
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
