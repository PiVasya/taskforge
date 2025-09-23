import axios from "axios";

const API_URL = process.env.REACT_APP_API_URL; // без ?? ""

export const api = axios.create({
    baseURL: API_URL,        // ← абсолютный базовый адрес
    withCredentials: false,  // ← никаких кук
});

let _accessToken = null;
export const setAccessToken = (t) => { _accessToken = t || null; };

api.interceptors.request.use((config) => {
    if (_accessToken) config.headers.Authorization = `Bearer ${_accessToken}`;
    return config;
});
