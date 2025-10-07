// clientapp/src/api/http.js
import axios from 'axios';

export const api = axios.create({
  baseURL: '',
  timeout: 15000,
  headers: { Accept: 'application/json' },
});

api.interceptors.request.use((config) => {
  const token = localStorage.getItem('token');
  if (token) {
    config.headers = config.headers ?? {};
    config.headers.Authorization = `Bearer ${token}`;
  }
  return config;
});

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
  try { return JSON.stringify(data); } catch {}
  return null;
}

api.interceptors.response.use(
  (res) => res,
  (err) => {
    const r = err?.response;
    err.userMessage =
      extractMessage(r?.data) ||
      r?.statusText ||
      err.message ||
      'Ошибка запроса';
    return Promise.reject(err); // важно: не делаем new Error(...) — сохраняем response/status
  }
);

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
