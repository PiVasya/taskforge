import React, { createContext, useContext, useEffect, useState, useCallback } from 'react';
import { setAccessToken } from '../api/http';
import { AuthApi } from '../api/auth';

const Ctx = createContext(null);
export const useAuth = () => useContext(Ctx);

export default function AuthProvider({ children }) {
  const [user] = useState(null);
  const [access, _setAccess] = useState(null);
  const [ready, setReady] = useState(false);

  const applyAccess = (token) => {
    _setAccess(token || null);
    setAccessToken(token || null);
  };

  const doLogin = useCallback(async (email, password) => {
    const res = await AuthApi.login({ email, password });
    applyAccess(res.accessToken || null);
  }, []);

  const doLogout = useCallback(async () => {
    try { await AuthApi.logout(); } catch { }
    applyAccess(null);
  }, []);

  const doRefresh = useCallback(async () => {
    const res = await AuthApi.refresh();
    applyAccess(res.accessToken || null);
    return res;
  }, []);

  // On app load: try to refresh using HttpOnly refresh cookie.
  useEffect(() => {
    (async () => {
      try {
        await doRefresh();
      } catch {
        applyAccess(null);
      } finally {
        setReady(true);
      }
    })();
  }, [doRefresh]);

  // Auto refresh every ~10 minutes while logged in.
  useEffect(() => {
    if (!access) return;
    const id = setInterval(() => {
      doRefresh().catch(() => {});
    }, 10 * 60 * 1000);
    return () => clearInterval(id);
  }, [access, doRefresh]);

  return (
    <Ctx.Provider value={{ ready, user, access, login: doLogin, logout: doLogout, refresh: doRefresh }}>
      {children}
    </Ctx.Provider>
  );
}
