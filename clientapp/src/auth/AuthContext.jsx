import React, { createContext, useContext, useCallback, useEffect, useState } from "react";
import { setAccessToken, setRefreshHandler } from "../api/http";
import * as Auth from "../api/auth";

const Ctx = createContext(null);
export const useAuth = () => useContext(Ctx);

export default function AuthProvider({ children }) {
    const [user, setUser] = useState(null);     // можно дополнить, когда бэк начнёт присылать профиль
    const [access, _setAccess] = useState(null);
    const [ready, setReady] = useState(false);

    const applyAccess = (token) => { _setAccess(token); setAccessToken(token); };

    const doLogin = async (email, password) => {
        const { accessToken } = await Auth.login({ email, password });
        applyAccess(accessToken);
        // опционально: запросить /api/auth/me и положить профиль в user
    };

    const doRefresh = useCallback(async () => {
        try {
            const { accessToken } = await Auth.refresh();
            applyAccess(accessToken);
            return accessToken;
        } catch {
            applyAccess(null);
            setUser(null);
            return null;
        }
    }, []);

    const doLogout = async () => {
        try { await Auth.logout(); } finally { applyAccess(null); setUser(null); }
    };

    // один общий refresh для axios
    useEffect(() => { setRefreshHandler(() => doRefresh); }, [doRefresh]);

    // авто-авторизация при первой загрузке (если жива refresh-cookie)
    useEffect(() => { (async () => { await doRefresh(); setReady(true); })(); }, [doRefresh]);

    return (
        <Ctx.Provider value={{ ready, user, access, login: doLogin, logout: doLogout, refresh: doRefresh }}>
            {children}
        </Ctx.Provider>
    );
}
