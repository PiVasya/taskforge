import React, { createContext, useContext, useEffect, useState, useCallback, useMemo } from 'react';
import { setAccessToken } from '../api/http';
import { AuthApi } from '../api/auth';
import { getProfile } from '../api/profile';

function takeBrowserInjectedAccessToken() {
  const token = typeof window !== 'undefined' ? window.__TASKFORGE_BROWSER_ACCESS_TOKEN__ : null;
  if (typeof window !== 'undefined') {
    try { delete window.__TASKFORGE_BROWSER_ACCESS_TOKEN__; } catch { window.__TASKFORGE_BROWSER_ACCESS_TOKEN__ = null; }
  }
  return typeof token === 'string' && token.trim() ? token.trim() : null;
}

const Ctx = createContext(null);
export const useAuth = () => useContext(Ctx);

export default function AuthProvider({ children }) {
  const [user, setUser] = useState(null);
  const [access, _setAccess] = useState(null);
  const [ready, setReady] = useState(false);

  const applyAccess = useCallback((token) => {
    _setAccess(token || null);
    setAccessToken(token || null);
  }, []);

  const pullProfileOnce = useCallback(async () => {
    try {
      const profile = await getProfile();
      setUser(profile || null);
    } catch {
      setUser(null);
    }
  }, []);

  const doLogin = useCallback(async (login, password) => {
    const res = await AuthApi.login({ login, password });
    applyAccess(res.accessToken || null);
    await pullProfileOnce();
  }, [applyAccess, pullProfileOnce]);

  const doLogout = useCallback(async () => {
    try { await AuthApi.logout(); } catch { }
    applyAccess(null);
    setUser(null);
  }, [applyAccess]);

  const doRefresh = useCallback(async () => {
    const res = await AuthApi.refresh();
    applyAccess(res.accessToken || null);
    await pullProfileOnce();
    return res;
  }, [applyAccess, pullProfileOnce]);

  useEffect(() => {
    (async () => {
      const injectedAccess = takeBrowserInjectedAccessToken();
      try {
        if (injectedAccess) {
          applyAccess(injectedAccess);
          await pullProfileOnce();
        } else {
          await doRefresh();
        }
      } catch {
        applyAccess(null);
      } finally {
        setReady(true);
      }
    })();
  }, [applyAccess, doRefresh, pullProfileOnce]);

  useEffect(() => {
    if (!access) return;
    const id = setInterval(() => {
      doRefresh().catch(() => {});
    }, 10 * 60 * 1000);
    return () => clearInterval(id);
  }, [access, doRefresh]);

  const value = useMemo(
    () => ({ ready, user, access, login: doLogin, logout: doLogout, refresh: doRefresh }),
    [access, doLogin, doLogout, doRefresh, ready, user],
  );

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}
