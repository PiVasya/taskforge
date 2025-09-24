export async function login({ email, password }) {
    const res = await fetch('api/auth/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ email, password }),
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(data?.message || 'Ошибка авторизации');
    const token = data?.token ?? data?.accessToken;
    if (!token) throw new Error('Токен не получен');
    return { token };
}

export async function registerUser(dto) {
    const res = await fetch('api/auth/register', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(dto),
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(data?.message || 'Ошибка регистрации');
    return data;
}

// Совместимость со старыми импортами
export const loginUser = login;
// refresh/logout в этой схеме не нужны
export async function refresh() { throw new Error('refresh недоступен'); }
export async function logout() {}
