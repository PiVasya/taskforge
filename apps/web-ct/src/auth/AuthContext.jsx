import React, { createContext, useContext, useEffect, useRef, useState, useCallback, useMemo } from 'react';
import { setAccessToken } from '../api/http';
import { AuthApi } from '../api/auth';
import { getMyUiSettings } from '../api/uiSettings';
import { getProfile } from '../api/profile';
import { persistUiSettingsFromBackend } from '../utils/uiAppearance';
import { REMEMBER_ME_STORAGE_KEY, accessExpiresAt, canRememberDevice, isRememberedDevice, storeRememberedDevice } from './rememberMe';

function takeBrowserInjectedAccessToken() {
  const token = typeof window !== 'undefined' ? window.__TASKFORGE_BROWSER_ACCESS_TOKEN__ : null;
  if (typeof window !== 'undefined') {
    try { delete window.__TASKFORGE_BROWSER_ACCESS_TOKEN__; } catch { window.__TASKFORGE_BROWSER_ACCESS_TOKEN__ = null; }
  }
  return typeof token === 'string' && token.trim() ? token.trim() : null;
}

const Ctx = createContext(null);
Ctx.displayName = 'AuthContext';
export const useAuth = () => useContext(Ctx);

export default function AuthProvider({ children }) {
  const [user, setUser] = useState(null);
  const [access, _setAccess] = useState(null);
  const [ready, setReady] = useState(false);
  const [rememberMe, setRememberMe] = useState(isRememberedDevice);

  
  const uiLoadedRef = useRef(false);

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

  const pullUiSettingsOnce = useCallback(async () => {
    if (uiLoadedRef.current) return;
    uiLoadedRef.current = true;
    try {
      const s = await getMyUiSettings();
      persistUiSettingsFromBackend(s);
    } catch {
      
    }
  }, []);

  const doLogin = useCallback(async (login, password, remember = false) => {
    if (remember && !canRememberDevice()) throw new Error('Браузер не позволяет запомнить вход на этом устройстве.');
    const res = await AuthApi.login({ login, password, rememberMe: Boolean(remember) });
    storeRememberedDevice(Boolean(remember));
    setRememberMe(Boolean(remember));
    applyAccess(res.accessToken || null);
    await pullProfileOnce();
    
    await pullUiSettingsOnce();
  }, [applyAccess, pullProfileOnce, pullUiSettingsOnce]);

  const doLogout = useCallback(async () => {
    storeRememberedDevice(false);
    setRememberMe(false);
    try { await AuthApi.logout(); } catch { }
    applyAccess(null);
    setUser(null);
    uiLoadedRef.current = false;
  }, [applyAccess]);

  const doRefresh = useCallback(async () => {
    if (!isRememberedDevice()) return null;
    let res;
    try {
      res = await AuthApi.refresh();
    } catch (error) {
      if ([401, 403, 423].includes(Number(error?.response?.status))) {
        storeRememberedDevice(false);
        setRememberMe(false);
      }
      throw error;
    }
    applyAccess(res.accessToken || null);
    await pullProfileOnce();
    
    await pullUiSettingsOnce();
    return res;
  }, [applyAccess, pullProfileOnce, pullUiSettingsOnce]);

  
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
          await pullUiSettingsOnce();
        } else {
          try {
            const session = await AuthApi.session();
            if (!session?.accessToken) throw new Error('Сессия недоступна.');
            applyAccess(session.accessToken);
            await pullProfileOnce();
            await pullUiSettingsOnce();
          } catch (error) {
            if (!isRememberedDevice()) throw error;
            await doRefresh();
          }
        }
      } catch {
        applyAccess(null);
      } finally {
        setReady(true);
      }
    })();
  }, [applyAccess, doRefresh, pullProfileOnce, pullUiSettingsOnce]);

  
  useEffect(() => {
    if (!access || !rememberMe) return;
    const id = setInterval(() => {
      doRefresh().catch(() => {});
    }, 10 * 60 * 1000);
    return () => clearInterval(id);
  }, [access, doRefresh, rememberMe]);

  useEffect(() => {
    if (!access || rememberMe) return;
    const expiresAt = accessExpiresAt(access);
    if (!expiresAt) return;
    const id = window.setTimeout(() => {
      applyAccess(null);
      setUser(null);
      uiLoadedRef.current = false;
    }, Math.max(0, expiresAt - Date.now()));
    return () => window.clearTimeout(id);
  }, [access, applyAccess, rememberMe]);

  const value = useMemo(
    () => ({ ready, user, access, login: doLogin, logout: doLogout, refresh: doRefresh }),
    [access, doLogin, doLogout, doRefresh, ready, user],
  );

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}
