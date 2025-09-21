// clientapp/src/api/http.js
import axios from "axios";

const API_URL = process.env.REACT_APP_API_URL ?? "";

export const api = axios.create({
    baseURL: API_URL,
    withCredentials: false, // ← оставляем выключенным
});

let _accessToken = null;
export const setAccessToken = (t) => { _accessToken = t || null; };

api.interceptors.request.use((config) => {
    if (_accessToken) config.headers.Authorization = `Bearer ${_accessToken}`;
    return config;
});

// ↓↓↓ УБИРАЕМ всю логику response-интерцептора с auto-refresh.
// Если хочешь оставить — делай его no-op (ничего не делает):
