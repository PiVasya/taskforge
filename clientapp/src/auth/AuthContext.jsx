import React, { createContext, useContext, useEffect, useState, useCallback } from "react";
import { setAccessToken } from "../api/http";
import * as Auth from "../api/auth";

const Ctx = createContext(null);
export const useAuth = () => useContext(Ctx);

export default function AuthProvider({ children }) {
    const [user] = useState(null);
    const [access, _setAccess] = useState(null);
    const [ready, setReady] = useState(false);

    const applyAccess = (token) => { _setAccess(token); setAccessToken(token); };

    const doLogin = useCallback(async (email, password) => {
        const { token } = await Auth.login({ email, password });
        try { localStorage.setItem("token", token); } catch { }
        applyAccess(token);
    }, []);

    const doLogout = useCallback(async () => {
        try { localStorage.removeItem("token"); } catch { }
        applyAccess(null);
    }, []);

    useEffect(() => {
        let t = null;
        try { t = localStorage.getItem("token"); } catch { }
        if (t) applyAccess(t);
        setReady(true);
    }, []);

    return (
        <Ctx.Provider value={{ ready, user, access, login: doLogin, logout: doLogout }}>
            {children}
        </Ctx.Provider>
    );
}
