import React, {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import { useAuth } from '../auth/AuthContext';
import { getMyUiSettings } from '../api/uiSettings';

const UI_SETTINGS_KEY = 'uiSettings';
const PALETTES = new Set(['blue', 'pink', 'apple', 'red', 'honey', 'violet']);

const DEFAULT_SETTINGS = Object.freeze({
  colorTheme: 'pink',
  mode: 'dark',
  uiStyle: 'default',
  bgFx: false,
  fxMode: 'random',
  fxVariant: '2',
  codeSolveLayout: 'split',
  codeEditorStyle: 'color',
  sidebarCollapsed: false,
  showSidebarToggle: true,
});

const ThemeContext = createContext(null);
const BackgroundContext = createContext(null);
const EditorUiContext = createContext(null);
const NavigationContext = createContext(null);
const SettingsActionsContext = createContext(null);

function readJsonSettings() {
  try {
    const raw = window.localStorage.getItem(UI_SETTINGS_KEY);
    return raw ? JSON.parse(raw) : null;
  } catch {
    return null;
  }
}

function readBoolean(key, fallback) {
  try {
    const raw = window.localStorage.getItem(key);
    if (raw === '1') return true;
    if (raw === '0') return false;
  } catch {}
  return fallback;
}

function normalizeSettings(input = {}) {
  const stored = typeof window !== 'undefined' ? readJsonSettings() || {} : {};
  const read = (key, fallback = null) => {
    if (input?.[key] != null) return input[key];
    if (stored?.[key] != null) return stored[key];
    if (typeof window !== 'undefined') {
      try {
        const value = window.localStorage.getItem(key);
        if (value != null) return value;
      } catch {}
    }
    return fallback;
  };

  const rawPalette = String(read('colorTheme', DEFAULT_SETTINGS.colorTheme));
  const colorTheme = PALETTES.has(rawPalette) ? rawPalette : DEFAULT_SETTINGS.colorTheme;
  const mode = read('mode', DEFAULT_SETTINGS.mode) === 'light' ? 'light' : 'dark';
  const uiStyle = read('uiStyle', DEFAULT_SETTINGS.uiStyle) === 'neobrutal' ? 'neobrutal' : 'default';
  const fxMode = read('fxMode', DEFAULT_SETTINGS.fxMode) === 'fixed' ? 'fixed' : 'random';
  const codeSolveLayout = read('codeSolveLayout', DEFAULT_SETTINGS.codeSolveLayout) === 'editorTop'
    ? 'editorTop'
    : 'split';
  const codeEditorStyle = read('codeEditorStyle', DEFAULT_SETTINGS.codeEditorStyle) === 'mono'
    ? 'mono'
    : 'color';

  const storedBgFx = typeof stored.bgFx === 'boolean'
    ? stored.bgFx
    : readBoolean('bgFx', DEFAULT_SETTINGS.bgFx);
  const storedSidebarCollapsed = typeof stored.sidebarCollapsed === 'boolean'
    ? stored.sidebarCollapsed
    : readBoolean('sidebarCollapsed', DEFAULT_SETTINGS.sidebarCollapsed);
  const storedSidebarToggle = typeof stored.showSidebarToggle === 'boolean'
    ? stored.showSidebarToggle
    : readBoolean('showSidebarToggle', DEFAULT_SETTINGS.showSidebarToggle);

  return {
    colorTheme,
    mode,
    uiStyle,
    bgFx: typeof input.bgFx === 'boolean' ? input.bgFx : storedBgFx,
    fxMode,
    fxVariant: String(read('fxVariant', DEFAULT_SETTINGS.fxVariant)),
    codeSolveLayout,
    codeEditorStyle,
    sidebarCollapsed:
      typeof input.sidebarCollapsed === 'boolean'
        ? input.sidebarCollapsed
        : storedSidebarCollapsed,
    showSidebarToggle:
      typeof input.showSidebarToggle === 'boolean'
        ? input.showSidebarToggle
        : storedSidebarToggle,
  };
}

function equalSettings(left, right) {
  return Object.keys(DEFAULT_SETTINGS).every((key) => left?.[key] === right?.[key]);
}

function persistSettingsSnapshot(settings) {
  if (typeof window === 'undefined' || !settings) return;

  try {
    window.localStorage.setItem('colorTheme', settings.colorTheme);
    window.localStorage.setItem('mode', settings.mode);
    window.localStorage.setItem('uiStyle', settings.uiStyle);
    window.localStorage.setItem('bgFx', settings.bgFx ? '1' : '0');
    window.localStorage.setItem('fxMode', settings.fxMode);
    window.localStorage.setItem('fxVariant', settings.fxVariant);
    window.localStorage.setItem('codeSolveLayout', settings.codeSolveLayout);
    window.localStorage.setItem('codeEditorStyle', settings.codeEditorStyle);
    window.localStorage.setItem(
      'showSidebarToggle',
      settings.showSidebarToggle ? '1' : '0',
    );
    window.localStorage.setItem(
      'sidebarCollapsed',
      settings.sidebarCollapsed ? '1' : '0',
    );

    // sidebarCollapsed is intentionally local to this device. The backend payload
    // controls account-wide UI preferences; local navigation state survives on
    // this browser without being uploaded to other devices.
    const remoteCompatibleSettings = {
      colorTheme: settings.colorTheme,
      mode: settings.mode,
      uiStyle: settings.uiStyle,
      bgFx: settings.bgFx,
      fxMode: settings.fxMode,
      fxVariant: settings.fxVariant,
      codeSolveLayout: settings.codeSolveLayout,
      codeEditorStyle: settings.codeEditorStyle,
      showSidebarToggle: settings.showSidebarToggle,
    };
    window.localStorage.setItem(
      UI_SETTINGS_KEY,
      JSON.stringify(remoteCompatibleSettings),
    );
  } catch {}
}

function applyThemeClasses(mode, colorTheme, uiStyle) {
  if (typeof document === 'undefined') return;
  const root = document.documentElement;
  root.classList.remove(...PALETTES);
  root.classList.add(PALETTES.has(colorTheme) ? colorTheme : DEFAULT_SETTINGS.colorTheme);
  root.classList.toggle('dark', mode === 'dark');
  root.classList.toggle('neo-brutal', uiStyle === 'neobrutal');
}

function persistSidebarCollapsed(value) {
  try {
    window.localStorage.setItem('sidebarCollapsed', value ? '1' : '0');
  } catch {}
}

export function UiSettingsProvider({ children }) {
  const { access } = useAuth();
  const authenticated = Boolean(access);
  const [settings, setSettings] = useState(() => normalizeSettings());
  const loadedRemoteSettingsRef = useRef(false);

  useEffect(() => {
    if (!authenticated) {
      loadedRemoteSettingsRef.current = false;
      return undefined;
    }
    if (loadedRemoteSettingsRef.current) return undefined;

    loadedRemoteSettingsRef.current = true;
    let active = true;

    getMyUiSettings()
      .then((remoteSettings) => {
        if (!active || !remoteSettings || typeof remoteSettings !== 'object') return;
        const next = normalizeSettings(remoteSettings);
        persistSettingsSnapshot(next);
        setSettings((previous) => (equalSettings(previous, next) ? previous : next));
      })
      .catch(() => {
        // Keep the local snapshot and avoid a request storm on every token refresh.
        // The flag is reset after logout, so a later authenticated session retries.
      });

    return () => {
      active = false;
    };
  }, [authenticated]);

  useLayoutEffect(() => {
    applyThemeClasses(settings.mode, settings.colorTheme, settings.uiStyle);
  }, [settings.mode, settings.colorTheme, settings.uiStyle]);

  useEffect(() => {
    if (typeof window === 'undefined') return undefined;

    const syncFromStorage = () => {
      const next = normalizeSettings();
      setSettings((previous) => (equalSettings(previous, next) ? previous : next));
    };

    const onStorage = (event) => {
      if (
        !event.key
        || event.key === UI_SETTINGS_KEY
        || Object.prototype.hasOwnProperty.call(DEFAULT_SETTINGS, event.key)
      ) {
        syncFromStorage();
      }
    };

    window.addEventListener('storage', onStorage);
    window.addEventListener('tf-ui-settings-changed', syncFromStorage);
    return () => {
      window.removeEventListener('storage', onStorage);
      window.removeEventListener('tf-ui-settings-changed', syncFromStorage);
    };
  }, []);

  const setSidebarCollapsed = useCallback((nextValue) => {
    setSettings((previous) => {
      const resolved = typeof nextValue === 'function'
        ? Boolean(nextValue(previous.sidebarCollapsed))
        : Boolean(nextValue);
      if (resolved === previous.sidebarCollapsed) return previous;
      persistSidebarCollapsed(resolved);
      return { ...previous, sidebarCollapsed: resolved };
    });
  }, []);

  const toggleSidebarCollapsed = useCallback(() => {
    setSidebarCollapsed((value) => !value);
  }, [setSidebarCollapsed]);

  const applyUiSettings = useCallback((nextValue) => {
    setSettings((previous) => {
      const patch = typeof nextValue === 'function' ? nextValue(previous) : nextValue;
      const next = normalizeSettings({
        ...previous,
        ...(patch && typeof patch === 'object' ? patch : {}),
      });
      persistSettingsSnapshot(next);
      return equalSettings(previous, next) ? previous : next;
    });
  }, []);

  const themeValue = useMemo(
    () => ({
      colorTheme: settings.colorTheme,
      mode: settings.mode,
      uiStyle: settings.uiStyle,
      paletteKey: `${settings.mode}:${settings.colorTheme}`,
    }),
    [settings.colorTheme, settings.mode, settings.uiStyle],
  );

  const backgroundValue = useMemo(
    () => ({
      bgFx: settings.bgFx,
      fxMode: settings.fxMode,
      fxVariant: settings.fxVariant,
      paletteKey: `${settings.mode}:${settings.colorTheme}`,
    }),
    [
      settings.bgFx,
      settings.colorTheme,
      settings.fxMode,
      settings.fxVariant,
      settings.mode,
    ],
  );

  const editorValue = useMemo(
    () => ({
      codeSolveLayout: settings.codeSolveLayout,
      codeEditorStyle: settings.codeEditorStyle,
    }),
    [settings.codeEditorStyle, settings.codeSolveLayout],
  );

  const navigationValue = useMemo(
    () => ({
      sidebarCollapsed: settings.sidebarCollapsed,
      showSidebarToggle: settings.showSidebarToggle,
      setSidebarCollapsed,
      toggleSidebarCollapsed,
    }),
    [
      settings.showSidebarToggle,
      settings.sidebarCollapsed,
      setSidebarCollapsed,
      toggleSidebarCollapsed,
    ],
  );

  const actionsValue = useMemo(
    () => ({ applyUiSettings }),
    [applyUiSettings],
  );

  return (
    <SettingsActionsContext.Provider value={actionsValue}>
      <ThemeContext.Provider value={themeValue}>
        <BackgroundContext.Provider value={backgroundValue}>
          <EditorUiContext.Provider value={editorValue}>
            <NavigationContext.Provider value={navigationValue}>
              {children}
            </NavigationContext.Provider>
          </EditorUiContext.Provider>
        </BackgroundContext.Provider>
      </ThemeContext.Provider>
    </SettingsActionsContext.Provider>
  );
}

export function useUiTheme() {
  return useContext(ThemeContext) || {
    colorTheme: DEFAULT_SETTINGS.colorTheme,
    mode: DEFAULT_SETTINGS.mode,
    uiStyle: DEFAULT_SETTINGS.uiStyle,
    paletteKey: `${DEFAULT_SETTINGS.mode}:${DEFAULT_SETTINGS.colorTheme}`,
  };
}

export function useUiBackgroundSettings() {
  return useContext(BackgroundContext) || {
    bgFx: DEFAULT_SETTINGS.bgFx,
    fxMode: DEFAULT_SETTINGS.fxMode,
    fxVariant: DEFAULT_SETTINGS.fxVariant,
    paletteKey: `${DEFAULT_SETTINGS.mode}:${DEFAULT_SETTINGS.colorTheme}`,
  };
}

export function useEditorUiSettings() {
  return useContext(EditorUiContext) || {
    codeSolveLayout: DEFAULT_SETTINGS.codeSolveLayout,
    codeEditorStyle: DEFAULT_SETTINGS.codeEditorStyle,
  };
}

export function useUiNavigationSettings() {
  return useContext(NavigationContext) || {
    sidebarCollapsed: DEFAULT_SETTINGS.sidebarCollapsed,
    showSidebarToggle: DEFAULT_SETTINGS.showSidebarToggle,
    setSidebarCollapsed: () => {},
    toggleSidebarCollapsed: () => {},
  };
}

export function useUiSettingsActions() {
  return useContext(SettingsActionsContext) || {
    applyUiSettings: () => {},
  };
}

// Compatibility hook for code outside the refactored shell. New components should
// subscribe to the smallest domain-specific hook above.
export function useUiAppearance() {
  const theme = useUiTheme();
  const background = useUiBackgroundSettings();
  const editor = useEditorUiSettings();
  return useMemo(
    () => ({ ...theme, ...background, ...editor }),
    [background, editor, theme],
  );
}
