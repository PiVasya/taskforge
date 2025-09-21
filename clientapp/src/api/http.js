import axios from "axios";

const API_URL = process.env.REACT_APP_API_URL ?? "";

export const api = axios.create({
    baseURL: API_URL,
    withCredentials: false, // включаем только там, где нужны cookie (login/refresh/logout)
});

// Хранилище для access-токена (упростим: модульная переменная + сеттер снаружи)
let _accessToken = null;
export const setAccessToken = (t) => { _accessToken = t || null; };

api.interceptors.request.use((config) => {
    if (_accessToken) config.headers.Authorization = `Bearer ${_accessToken}`;
    return config;
});

let refreshing = null;
export const setRefreshHandler = (fn) => { refreshing = fn; };

api.interceptors.response.use(
    r => r,
    async (error) => {
        const { response, config } = error || {};
        if (response?.status === 401 && !config._retry && refreshing) {
            config._retry = true;
            const newAccess = await refreshing();             // вызовем refresh из контекста
            if (newAccess) {
                config.headers.Authorization = `Bearer ${newAccess}`;
                return api(config);
            }
        }
        throw error;
    }
);
