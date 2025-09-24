import axios from 'axios';

export const api = axios.create({
  baseURL: '',           // same-origin
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
    const r = err.response;
    const message =
      r?.data?.message ||
      r?.data?.error ||
      r?.statusText ||
      err.message ||
      'Ошибка запроса';
    return Promise.reject(new Error(message));
  }
);
