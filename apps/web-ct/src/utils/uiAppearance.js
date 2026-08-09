const UI_SETTINGS_KEY = 'uiSettings';
const PALETTES = new Set(['blue', 'pink', 'apple', 'red', 'honey', 'violet']);

function readJsonSettings() {
  try {
    const raw = localStorage.getItem(UI_SETTINGS_KEY);
    return raw ? JSON.parse(raw) : null;
  } catch {
    return null;
  }
}

function normalizeUiSettings(input = {}) {
  const stored = readJsonSettings() || {};
  const colorTheme = input.colorTheme || stored.colorTheme || localStorage.getItem('colorTheme') || 'blue';
  const mode = input.mode || stored.mode || localStorage.getItem('mode') || 'light';
  const uiStyle = input.uiStyle || stored.uiStyle || localStorage.getItem('uiStyle') || 'default';

  return {
    colorTheme: PALETTES.has(colorTheme) ? colorTheme : 'blue',
    mode: mode === 'dark' ? 'dark' : 'light',
    uiStyle: uiStyle === 'neobrutal' ? 'neobrutal' : 'default',
    bgFx:
      typeof input.bgFx === 'boolean'
        ? input.bgFx
        : (typeof stored.bgFx === 'boolean' ? stored.bgFx : localStorage.getItem('bgFx') === '1'),
    fxMode: input.fxMode || stored.fxMode || localStorage.getItem('fxMode') || 'random',
    fxVariant: String(input.fxVariant ?? stored.fxVariant ?? localStorage.getItem('fxVariant') ?? '2'),
    codeSolveLayout: input.codeSolveLayout || stored.codeSolveLayout || localStorage.getItem('codeSolveLayout') || 'split',
    codeEditorStyle:
      input.codeEditorStyle != null
        ? (input.codeEditorStyle === 'mono' ? 'mono' : 'color')
        : (stored.codeEditorStyle === 'mono' ? 'mono' : 'color'),
    showSidebarToggle:
      typeof input.showSidebarToggle === 'boolean'
        ? input.showSidebarToggle
        : stored.showSidebarToggle !== false,
  };
}

export function applyUiAppearance(settings) {
  if (typeof document === 'undefined' || !settings) return;
  const root = document.documentElement;
  root.classList.remove(...PALETTES);
  root.classList.add(settings.colorTheme);
  root.classList.toggle('dark', settings.mode === 'dark');
  root.classList.toggle('neo-brutal', settings.uiStyle === 'neobrutal');
}

export function applyStoredUiAppearance() {
  if (typeof window === 'undefined') return;
  applyUiAppearance(normalizeUiSettings());
}

export function persistUiSettingsFromBackend(settings) {
  if (!settings || typeof settings !== 'object') return;
  const merged = normalizeUiSettings(settings);

  try {
    localStorage.setItem('colorTheme', merged.colorTheme);
    localStorage.setItem('mode', merged.mode);
    localStorage.setItem('uiStyle', merged.uiStyle);
    localStorage.setItem('bgFx', merged.bgFx ? '1' : '0');
    localStorage.setItem('fxMode', merged.fxMode);
    localStorage.setItem('fxVariant', merged.fxVariant);
    localStorage.setItem('codeSolveLayout', merged.codeSolveLayout);
    localStorage.setItem('codeEditorStyle', merged.codeEditorStyle);
    localStorage.setItem('showSidebarToggle', merged.showSidebarToggle ? '1' : '0');
    localStorage.setItem(UI_SETTINGS_KEY, JSON.stringify(merged));
  } catch {}

  applyUiAppearance(merged);
  window.dispatchEvent(new Event('tf-ui-settings-changed'));
}
