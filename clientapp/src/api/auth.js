import { api } from './http';

// логин — ждём { token } или { accessToken }
export async function login({ email, password }) {
  const { data } = await api.post('/api/auth/login', { email, password });
  const token = data?.token ?? data?.accessToken;
  if (!token) throw new Error('Токен не получен');
  return { token };
}

// регистрация — возвращаем тело ответа
export async function registerUser(dto) {
  const { data } = await api.post('/api/auth/register', dto);
  return data;
}

// совместимость
export const loginUser = login;
export async function refresh() { throw new Error('refresh недоступен'); }
export async function logout() {}
