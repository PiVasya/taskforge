// clientapp/src/auth/AuthContext.jsx
import React, { createContext, useContext, useEffect, useState, useCallback } from "react";
import { setAccessToken } from "../api/http";
import * as Auth from "../api/auth";

const Ctx = createContext(null);
export const useAuth = () => useContext(Ctx);

export default function AuthProvider({ children }) {
    const [user] = useState(null); // если профиля нет — оставляем null
    const [access, _setAccess] = useState(null);
    const [ready, setReady] = useState(false);

    const applyAccess = (token) => { _setAccess(token); setAccessToken(token); };

    const doLogin = useCallback(async (email, password) => {
        const { token } = await Auth.login({ email, password }); // ← ждём { token }
        applyAccess(token);
        try { localStorage.setItem("tf_access", token); } catch { }
    }, []);

    const doLogout = useCallback(async () => {
        applyAccess(null);
        try { localStorage.removeItem("tf_access"); } catch { }
        // вызывать Auth.logout() смысла нет — у нас нет refresh-куки
    }, []);

    // ВОССТАНОВЛЕНИЕ ИЗ localStorage при первой загрузке
    useEffect(() => {
        let t = null;
        try { t = localStorage.getItem("tf_access"); } catch { }
        if (t) applyAccess(t);
        setReady(true);
    }, []);

    return (
        <Ctx.Provider value={{ ready, user, access, login: doLogin, logout: doLogout }}>
            {children}
        </Ctx.Provider>
    );
}
