// clientapp/src/api/http.js
import axios from 'axios';

// Create a shared axios instance with basic config
export const api = axios.create({
  baseURL: '',
  timeout: 15000,
  headers: { Accept: 'application/json' },
});

// Attach access token to every request if present
api.interceptors.request.use((config) => {
  const token = localStorage.getItem('token');
  if (token) {
    config.headers = config.headers ?? {};
    config.headers.Authorization = `Bearer ${token}`;
  }
  return config;
});

// Helper to extract a human-readable message from various server responses
function extractMessage(data) {
  if (!data) return null;
  if (typeof data === 'string') return data;

  // обычное поле message/title
  if (typeof data.message === 'string') return data.message;
  if (typeof data.title === 'string') return data.title;

  // ASP.NET ModelState: { errors: { Email: ["..."], Password: ["..."] } }
  if (data.errors && typeof data.errors === 'object') {
    const firstKey = Object.keys(data.errors)[0];
    if (firstKey) {
      const val = data.errors[firstKey];
      if (Array.isArray(val) && val.length) return val[0];
      if (typeof val === 'string') return val;
    }
  }

  // массив сообщений
  if (Array.isArray(data) && data.length) {
    const first = data[0];
    if (typeof first === 'string') return first;
    if (first && typeof first.message === 'string') return first.message;
  }

  // fallback—попробуем сериализовать
  try {
    return JSON.stringify(data);
  } catch {}
  return null;
}

// Intercept responses to add userMessage and handle auth errors
api.interceptors.response.use(
  (res) => res,
  (err) => {
    const r = err?.response;
    // Attach a friendly message for UI
    err.userMessage =
      extractMessage(r?.data) ||
      r?.statusText ||
      err.message ||
      'Ошибка запроса';

    // If the server reported unauthorized, clear any stored token so the app knows the user is logged out.
    if (r?.status === 401) {
      try {
        localStorage.removeItem('token');
      } catch {}
      // Also remove default auth header on this axios instance
      delete api.defaults.headers.common?.Authorization;
    }

    return Promise.reject(err); // важно: не делаем new Error(...) — сохраняем response/status
  }
);

/**
 * Utility to persist or remove the access token for future API calls.
 * When called with a non-empty token, it stores it and updates the axios defaults.
 * When called with null/undefined, it removes the token and header.
 */
export function setAccessToken(token) {
  try {
    if (token) {
      localStorage.setItem('token', token);
      api.defaults.headers.common.Authorization = `Bearer ${token}`;
    } else {
      localStorage.removeItem('token');
      delete api.defaults.headers.common.Authorization;
    }
  } catch {}
}

// Добавлено для совместимости с импортами вида `import api from './http'`
export default api;
