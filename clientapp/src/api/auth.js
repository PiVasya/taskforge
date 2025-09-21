import { api } from "./http";

/**
 * Унифицированные функции, которые ждёт фронт
 */
export async function login({ email, password }) {
    const { data } = await api.post("/api/auth/login", { email, password }, { withCredentials: true });
    return data; // { accessToken }
}
export async function refresh() {
    const { data } = await api.post("/api/auth/refresh", null, { withCredentials: true });
    return data; // { accessToken }
}
export async function logout() {
    await api.post("/api/auth/logout", null, { withCredentials: true });
}

/**
 * Алиасы под твои старые импорты (чтобы не искать все использования):
 * loginUser/registerUser уже были в проекте
 */
export async function loginUser(dto) {
    return login(dto);
}
export async function registerUser(dto) {
    const { data } = await api.post("/api/auth/register", dto, { withCredentials: true });
    return data;
}
