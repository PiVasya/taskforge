import React, { createContext, useContext, useEffect, useRef, useState, useCallback, useMemo } from 'react';
import { AuthApi } from '../api/auth';
import { AUTH_REQUIRED_EVENT } from './authEvents';
import { COOKIE_SESSION_UPDATED_EVENT, SESSION_STORAGE_EVENT_KEY, publishSessionChange, readSessionInfo, sessionExpiresAt } from './cookieSession';
import { REMEMBER_ME_STORAGE_KEY, canRememberDevice, isRememberedDevice, storeRememberedDevice } from './rememberMe';
import { getMyUiSettings } from '../api/uiSettings';
import { persistUiSettingsFromBackend } from '../utils/uiAppearance';

function isTransientRequestError(error) {
  const status = Number(error?.response?.status || 0);
  return !status || status >= 500 || status === 429;
}

async function retryTransient(action, attempts = 3) {
  for (let attempt = 0; ; attempt += 1) {
    try { return await action(); } catch (error) {
      if (!isTransientRequestError(error) || attempt >= attempts - 1) throw error;
      await new Promise(resolve => window.setTimeout(resolve, 250 * (attempt + 1)));
    }
  }
}

const Ctx = createContext(null);
Ctx.displayName = 'AuthContext';
export const useAuth = () => useContext(Ctx);

export default function AuthProvider({ children }) {
  const [session, setSession] = useState(null);
  const [ready, setReady] = useState(false);
  const [rememberMe, setRememberMe] = useState(isRememberedDevice);
  const epoch = useRef(0);
  const loggingOut = useRef(false);
  const currentUserId = useRef(null);
  const user = session?.user || null;
  const isAuthenticated = session?.authenticated === true;

  const clearSession = useCallback(() => {
    epoch.current += 1;
    currentUserId.current = null;
    setSession(null);
    setRememberMe(false);
    storeRememberedDevice(false);

  }, []);

  const applySession = useCallback((value) => {
    const next = readSessionInfo(value);
    const nextId = String(next.user.id || next.user.userId || next.user.login || '');
    if (currentUserId.current && currentUserId.current !== nextId) {

    }
    currentUserId.current = nextId;
    setSession(next);
    setRememberMe(next.rememberMe);
    storeRememberedDevice(next.rememberMe);
    return next;
  }, []);

  const doLogin = useCallback(async (login, password, remember = false) => {
    if (remember && !canRememberDevice()) throw new Error('\u0411\u0440\u0430\u0443\u0437\u0435\u0440 \u043d\u0435 \u043f\u043e\u0437\u0432\u043e\u043b\u044f\u0435\u0442 \u0437\u0430\u043f\u043e\u043c\u043d\u0438\u0442\u044c \u0432\u0445\u043e\u0434.');
    const version = ++epoch.current;
    const info = await AuthApi.login({ login, password, rememberMe: Boolean(remember) });
    if (version === epoch.current) {
      applySession(info);
      publishSessionChange('changed');
    }
    return info;
  }, [applySession]);

  const doLogout = useCallback(async () => {
    loggingOut.current = true;
    const version = ++epoch.current;
    try {
      // The server must confirm cookie removal before the UI declares logout.
      await AuthApi.logout();
      if (version === epoch.current) {
        clearSession();
        publishSessionChange('logout');
      }
    } finally {
      loggingOut.current = false;
    }
  }, [clearSession]);

  const doRefresh = useCallback(async () => {
    if (loggingOut.current) return null;
    const version = epoch.current;
    try {
      // An ordinary session may re-read its profile but may not extend its life.
      const info = await retryTransient(() => isRememberedDevice() ? AuthApi.refresh() : AuthApi.session());
      if (version === epoch.current && !loggingOut.current) applySession(info);
      return info;
    } catch (error) {
      if (version === epoch.current && [401, 403, 423].includes(Number(error?.response?.status))) clearSession();
      throw error;
    }
  }, [applySession, clearSession]);

  const changeRememberMe = useCallback(async (enabled) => {
    const next = Boolean(enabled);
    if (next && !canRememberDevice()) throw new Error('\u0411\u0440\u0430\u0443\u0437\u0435\u0440 \u043d\u0435 \u043f\u043e\u0437\u0432\u043e\u043b\u044f\u0435\u0442 \u0437\u0430\u043f\u043e\u043c\u043d\u0438\u0442\u044c \u0432\u0445\u043e\u0434.');
    const version = epoch.current;
    await AuthApi.remember(next);
    if (version !== epoch.current || loggingOut.current) return;
    storeRememberedDevice(next);
    setRememberMe(next);
    setSession(previous => previous ? { ...previous, rememberMe: next } : previous);
    publishSessionChange('changed');
  }, []);

  useEffect(() => {
    const updated = event => {
      if (!loggingOut.current) applySession(event.detail);
    };
    const required = () => clearSession();
    window.addEventListener(COOKIE_SESSION_UPDATED_EVENT, updated);
    window.addEventListener(AUTH_REQUIRED_EVENT, required);
    return () => {
      window.removeEventListener(COOKIE_SESSION_UPDATED_EVENT, updated);
      window.removeEventListener(AUTH_REQUIRED_EVENT, required);
    };
  }, [applySession, clearSession]);

  useEffect(() => {
    const version = ++epoch.current;
    let disposed = false;
    (async () => {
      try {
        let info;
        try { info = await retryTransient(() => AuthApi.session()); } catch (error) {
          if (Number(error?.response?.status) !== 401 || !isRememberedDevice()) throw error;
          info = await retryTransient(() => AuthApi.refresh());
        }
        if (!disposed && version === epoch.current) applySession(info);
      } catch (error) {
        if (!disposed && version === epoch.current && [401, 403, 423].includes(Number(error?.response?.status))) clearSession();
      } finally {
        if (!disposed) setReady(true);
      }
    })();
    return () => { disposed = true; };
  }, [applySession, clearSession]);

  useEffect(() => {
    const onStorage = event => {
      if (event.key === REMEMBER_ME_STORAGE_KEY) setRememberMe(isRememberedDevice());
      if (event.key !== SESSION_STORAGE_EVENT_KEY) return;
      let kind;
      try { kind = JSON.parse(event.newValue || '{}').kind; } catch { return; }
      if (kind === 'logout') clearSession();
      else if (kind === 'changed') doRefresh().catch(() => {});
    };
    const onVisible = () => {
      if (document.visibilityState === 'visible' && ready) doRefresh().catch(() => {});
    };
    window.addEventListener('storage', onStorage);
    document.addEventListener('visibilitychange', onVisible);
    return () => {
      window.removeEventListener('storage', onStorage);
      document.removeEventListener('visibilitychange', onVisible);
    };
  }, [clearSession, doRefresh, ready]);

  useEffect(() => {
    if (!isAuthenticated || !rememberMe) return undefined;
    const id = window.setInterval(() => { doRefresh().catch(() => {}); }, 10 * 60 * 1000);
    return () => window.clearInterval(id);
  }, [isAuthenticated, doRefresh, rememberMe]);

  useEffect(() => {
    if (!isAuthenticated || rememberMe) return undefined;
    const expiry = sessionExpiresAt(session);
    if (!expiry) return undefined;
    const id = window.setTimeout(clearSession, Math.max(0, Math.min(2147483647, expiry - Date.now())));
    return () => window.clearTimeout(id);
  }, [isAuthenticated, clearSession, rememberMe, session]);

  const appearanceUserId = user?.id || user?.userId || null;
  useEffect(() => {
    if (!appearanceUserId) return undefined;
    let disposed = false;
    getMyUiSettings().then(settings => {
      if (!disposed) persistUiSettingsFromBackend(settings);
    }).catch(() => {});
    return () => { disposed = true; };
  }, [appearanceUserId]);

  const value = useMemo(
    () => ({ ready, user, isAuthenticated, expiresAtUtc: session?.expiresAtUtc || null, rememberMe, setRememberMe: changeRememberMe, login: doLogin, logout: doLogout, refresh: doRefresh }),
    [changeRememberMe, doLogin, doLogout, doRefresh, isAuthenticated, ready, rememberMe, session, user],
  );
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}
