import React, { createContext, useContext, useEffect, useRef, useState, useCallback } from 'react';
import { setAccessToken } from '../api/http';
import { AuthApi } from '../api/auth';
import { getMyUiSettings } from '../api/uiSettings';
import { getProfile } from '../api/profile';



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
    codeSolveLayout: s.codeSolveLayout || prev?.codeSolveLayout || localStorage.getItem('codeSolveLayout') || 'split',
    showSidebarToggle:
      typeof s.showSidebarToggle === 'boolean'
        ? s.showSidebarToggle
        : (typeof prev?.showSidebarToggle === 'boolean' ? prev.showSidebarToggle : localStorage.getItem('showSidebarToggle') !== '0'),
  };

  localStorage.setItem('colorTheme', merged.colorTheme);
  localStorage.setItem('mode', merged.mode);
  localStorage.setItem('bgFx', merged.bgFx ? '1' : '0');
  localStorage.setItem('fxMode', merged.fxMode);
  localStorage.setItem('fxVariant', merged.fxVariant);
  localStorage.setItem('codeSolveLayout', merged.codeSolveLayout);
  localStorage.setItem('showSidebarToggle', merged.showSidebarToggle ? '1' : '0');
  localStorage.setItem(UI_LS_KEY, JSON.stringify(merged));

  
  window.dispatchEvent(new Event('tf-ui-settings-changed'));
}

const Ctx = createContext(null);
export const useAuth = () => useContext(Ctx);

export default function AuthProvider({ children }) {
  const [user, setUser] = useState(null);
  const [access, _setAccess] = useState(null);
  const [ready, setReady] = useState(false);

  
  const uiLoadedRef = useRef(false);

  const applyAccess = (token) => {
    _setAccess(token || null);
    setAccessToken(token || null);
  };


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

  const doLogin = useCallback(async (login, password) => {
    const res = await AuthApi.login({ login, password });
    applyAccess(res.accessToken || null);
    await pullProfileOnce();
    
    await pullUiSettingsOnce();
  }, [pullProfileOnce, pullUiSettingsOnce]);

  const doLogout = useCallback(async () => {
    try { await AuthApi.logout(); } catch { }
    applyAccess(null);
    setUser(null);
    uiLoadedRef.current = false;
  }, []);

  const doRefresh = useCallback(async () => {
    const res = await AuthApi.refresh();
    applyAccess(res.accessToken || null);
    await pullProfileOnce();
    
    await pullUiSettingsOnce();
    return res;
  }, [pullProfileOnce, pullUiSettingsOnce]);

  
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
