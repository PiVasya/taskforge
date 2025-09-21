// clientapp/src/api/auth.js
const API_URL = process.env.REACT_APP_API_URL ?? "";

/** ЛОГИН — как в старой версии: без credentials, ждём { token } */
export async function login({ email, password }) {
    const res = await fetch(`/api/auth/login`, {
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


/** РЕГИСТРАЦИЯ — как раньше, без credentials */
export async function registerUser(dto) {
    const res = await fetch(`${API_URL}/api/auth/register`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(dto),
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(data?.message || "Ошибка регистрации");
    return data;
}

/** В старой схеме refresh/logout не нужны — заглушки */
export async function refresh() { throw new Error("refresh недоступен в этой конфигурации"); }
export async function logout() { /* ничего не делаем */ }

/** Совместимость со старым импортом */
export const loginUser = login;
