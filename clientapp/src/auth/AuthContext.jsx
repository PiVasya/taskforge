import React, { createContext, useContext, useEffect, useRef, useState, useCallback } from 'react';
import { setAccessToken } from '../api/http';
import { AuthApi } from '../api/auth';
import { getMyUiSettings } from '../api/uiSettings';

// Persist UI settings in localStorage and notify Layout.
// Layout reads both a combined JSON (uiSettings) and individual keys.
const UI_LS_KEY = 'uiSettings';
function persistUiSettingsFromBackend(s) {
  if (!s || typeof s !== 'object') return;

  const prev = (() => {
    try {
      const raw = localStorage.getItem(UI_LS_KEY);
      return raw ? JSON.parse(raw) : null;
    } catch {
      return null;
    }
  })();

  const merged = {
    colorTheme: s.colorTheme || prev?.colorTheme || localStorage.getItem('colorTheme') || 'blue',
    mode: s.mode || prev?.mode || localStorage.getItem('mode') || 'light',
    bgFx:
      typeof s.bgFx === 'boolean'
        ? s.bgFx
        : (typeof prev?.bgFx === 'boolean' ? prev.bgFx : localStorage.getItem('bgFx') === '1'),
    fxMode: s.fxMode || prev?.fxMode || localStorage.getItem('fxMode') || 'random',
    fxVariant: String(s.fxVariant ?? prev?.fxVariant ?? localStorage.getItem('fxVariant') ?? '2'),
  };

  localStorage.setItem('colorTheme', merged.colorTheme);
  localStorage.setItem('mode', merged.mode);
  localStorage.setItem('bgFx', merged.bgFx ? '1' : '0');
  localStorage.setItem('fxMode', merged.fxMode);
  localStorage.setItem('fxVariant', merged.fxVariant);
  localStorage.setItem(UI_LS_KEY, JSON.stringify(merged));

  // In the same tab, storage-event doesn't fire — notify Layout explicitly.
  window.dispatchEvent(new Event('tf-ui-settings-changed'));
}

const Ctx = createContext(null);
export const useAuth = () => useContext(Ctx);

export default function AuthProvider({ children }) {
  const [user] = useState(null);
  const [access, _setAccess] = useState(null);
  const [ready, setReady] = useState(false);

  // Pull UI settings once per session (after first successful auth).
  const uiLoadedRef = useRef(false);

  const applyAccess = (token) => {
    _setAccess(token || null);
    setAccessToken(token || null);
  };

  const pullUiSettingsOnce = useCallback(async () => {
    if (uiLoadedRef.current) return;
    uiLoadedRef.current = true;
    try {
      const s = await getMyUiSettings();
      persistUiSettingsFromBackend(s);
    } catch {
      // ignore (endpoint may be unavailable or user not authorized)
    }
  }, []);

  const doLogin = useCallback(async (email, password) => {
    const res = await AuthApi.login({ email, password });
    applyAccess(res.accessToken || null);
    // After we have an access token, pull UI settings chosen for this user.
    await pullUiSettingsOnce();
  }, [pullUiSettingsOnce]);

  const doLogout = useCallback(async () => {
    try { await AuthApi.logout(); } catch { }
    applyAccess(null);
    uiLoadedRef.current = false;
  }, []);

  const doRefresh = useCallback(async () => {
    const res = await AuthApi.refresh();
    applyAccess(res.accessToken || null);
    // Refresh is executed on app load. If it succeeds, we are logged in.
    await pullUiSettingsOnce();
    return res;
  }, [pullUiSettingsOnce]);

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
