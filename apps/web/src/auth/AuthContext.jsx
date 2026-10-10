import React, { createContext, useContext, useEffect, useState, useCallback, useMemo } from 'react';
import { setAccessToken } from '../api/http';
import { AuthApi } from '../api/auth';
import { getProfile } from '../api/profile';
import { clearAllCourseMapLocalCaches } from '../features/course-assignments/courseMapLocalCache';
import { clearAllCourseMapSessionStates } from '../features/course-assignments/courseMapSessionState';
import { AUTH_REQUIRED_EVENT } from './authEvents';
import { clearPrivateBrowserState } from './privateBrowserState';
import { REMEMBER_ME_STORAGE_KEY, accessExpiresAt, canRememberDevice, isRememberedDevice, storeRememberedDevice } from './rememberMe';

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
Ctx.displayName = 'AuthContext';
export const useAuth = () => useContext(Ctx);

export default function AuthProvider({ children }) {
  const [user, setUser] = useState(null);
  const [access, _setAccess] = useState(null);
  const [ready, setReady] = useState(false);
  const [rememberMe, setRememberMe] = useState(isRememberedDevice);

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

  const doLogin = useCallback(async (login, password, remember = false) => {
    if (remember && !canRememberDevice()) throw new Error('Браузер не позволяет сохранить настройку на этом устройстве.');
    const res = await AuthApi.login({ login, password, rememberMe: Boolean(remember) });
    storeRememberedDevice(Boolean(remember));
    setRememberMe(Boolean(remember));
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
    storeRememberedDevice(false);
    setRememberMe(false);
    clearPrivateBrowserState();
    try { await AuthApi.logout(); } catch { }
    clearAllCourseMapLocalCaches();
    clearAllCourseMapSessionStates();
    applyAccess(null);
    setUser(null);
  }, [applyAccess]);

  const doRefresh = useCallback(async () => {
    if (!isRememberedDevice()) return null;
    let res;
    try {
      res = await retryTransient(() => AuthApi.refresh());
    } catch (error) {
      if ([401, 403, 423].includes(Number(error?.response?.status))) {
        storeRememberedDevice(false);
        setRememberMe(false);
      }
      throw error;
    }
    applyAccess(res.accessToken || null);
    try {
      await pullProfileOnce();
    } catch (error) {
      const status = Number(error?.response?.status || 0);
      if (status === 401 || status === 403) {
        clearPrivateBrowserState();
        applyAccess(null);
        throw error;
      }
    }
    return res;
  }, [applyAccess, pullProfileOnce]);

  const changeRememberMe = useCallback(async (enabled) => {
    const next = Boolean(enabled);
    if (next && !canRememberDevice()) throw new Error('Браузер не позволяет запомнить вход на этом устройстве.');
    await AuthApi.remember(next);
    storeRememberedDevice(next);
    setRememberMe(next);
  }, []);

  useEffect(() => {
    const onStorage = (event) => {
      if (event.key === REMEMBER_ME_STORAGE_KEY || event.key === null) setRememberMe(isRememberedDevice());
    };
    window.addEventListener('storage', onStorage);
    return () => window.removeEventListener('storage', onStorage);
  }, []);

  useEffect(() => {
    (async () => {
      const injectedAccess = takeBrowserInjectedAccessToken();
      try {
        if (injectedAccess) {
          applyAccess(injectedAccess);
          await pullProfileOnce();
        } else {
          try {
            const session = await retryTransient(() => AuthApi.session());
            if (!session?.accessToken) throw new Error('Сессия недоступна.');
            applyAccess(session.accessToken);
            await pullProfileOnce();
          } catch (error) {
            if (!isRememberedDevice()) throw error;
            await doRefresh();
          }
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
    if (!access || !rememberMe) return;
    const id = setInterval(() => {
      doRefresh().catch(() => {});
    }, 10 * 60 * 1000);
    return () => clearInterval(id);
  }, [access, doRefresh, rememberMe]);

  // Without Remember Me, expiration is final even if no API request is made.
  useEffect(() => {
    if (!access || rememberMe) return;
    const expiresAt = accessExpiresAt(access);
    if (!expiresAt) return;
    const id = window.setTimeout(() => {
      clearPrivateBrowserState();
      applyAccess(null);
      setUser(null);
    }, Math.max(0, expiresAt - Date.now()));
    return () => window.clearTimeout(id);
  }, [access, applyAccess, rememberMe]);

  useEffect(() => {
    const handleAuthRequired = () => {
      clearPrivateBrowserState();
      applyAccess(null);
      setUser(null);
    };
    window.addEventListener(AUTH_REQUIRED_EVENT, handleAuthRequired);
    return () => window.removeEventListener(AUTH_REQUIRED_EVENT, handleAuthRequired);
  }, [applyAccess]);

  const value = useMemo(
    () => ({ ready, user, access, rememberMe, setRememberMe: changeRememberMe, login: doLogin, logout: doLogout, refresh: doRefresh }),
    [access, changeRememberMe, doLogin, doLogout, doRefresh, ready, rememberMe, user],
  );

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}
