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

api.interceptors.response.use(
  (res) => res,
  (err) => {
    const r = err?.response;
    err.userMessage =
      r?.data?.message ||
      r?.data?.error ||
      r?.statusText ||
      err.message ||
      'Ошибка запроса';
    return Promise.reject(err); // не создаём new Error(...) — сохраняем status/response
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
