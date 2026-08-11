import React, { createContext, useContext, useEffect, useState, useCallback, useMemo } from 'react';
import { setAccessToken } from '../api/http';
import { AuthApi } from '../api/auth';
import { getProfile } from '../api/profile';
import { clearAllCourseMapLocalCaches } from '../features/course-assignments/courseMapLocalCache';
import { clearAllCourseMapSessionStates } from '../features/course-assignments/courseMapSessionState';

function takeBrowserInjectedAccessToken() {
  const token = typeof window !== 'undefined' ? window.__TASKFORGE_BROWSER_ACCESS_TOKEN__ : null;
  if (typeof window !== 'undefined') {
    try { delete window.__TASKFORGE_BROWSER_ACCESS_TOKEN__; } catch { window.__TASKFORGE_BROWSER_ACCESS_TOKEN__ = null; }
  }
  return typeof token === 'string' && token.trim() ? token.trim() : null;
}

function isTransientRequestError(error) {
  const status = Number(error?.response?.status || 0);
  return !status || status >= 500 || status === 429;
}

async function retryTransient(action, attempts = 3) {
  let lastError = null;
  for (let attempt = 0; attempt < attempts; attempt += 1) {
    try {
      return await action();
    } catch (error) {
      lastError = error;
      if (!isTransientRequestError(error) || attempt === attempts - 1) throw error;
      await new Promise((resolve) => window.setTimeout(resolve, 250 * (attempt + 1)));
    }
  }
  throw lastError;
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
      const profile = await retryTransient(() => getProfile());
      setUser(profile || null);
      return profile || null;
    } catch (error) {
      const status = Number(error?.response?.status || 0);
      if (status === 401 || status === 403) setUser(null);
      throw error;
    }
  }, []);

  const doLogin = useCallback(async (login, password) => {
    const res = await AuthApi.login({ login, password });
    applyAccess(res.accessToken || null);
    try {
      await pullProfileOnce();
    } catch (error) {
      const status = Number(error?.response?.status || 0);
      if (status === 401 || status === 403) throw error;
    }
    return res;
  }, [applyAccess, pullProfileOnce]);

  const doLogout = useCallback(async () => {
    try { await AuthApi.logout(); } catch { }
    clearAllCourseMapLocalCaches();
    clearAllCourseMapSessionStates();
    applyAccess(null);
    setUser(null);
  }, [applyAccess]);

  const doRefresh = useCallback(async () => {
    const res = await retryTransient(() => AuthApi.refresh());
    applyAccess(res.accessToken || null);
    try {
      await pullProfileOnce();
    } catch (error) {
      const status = Number(error?.response?.status || 0);
      if (status === 401 || status === 403) {
        applyAccess(null);
        throw error;
      }
    }
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
      } catch (error) {
        const status = Number(error?.response?.status || 0);
        if (!injectedAccess || status === 401 || status === 403) applyAccess(null);
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
