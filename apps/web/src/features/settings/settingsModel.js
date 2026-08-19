export const UI_SETTINGS_STORAGE_KEY = 'uiSettings';
export const LOGIN_RE = /^[a-zA-Z0-9_.-]{3,64}$/;

export const SETTINGS_SECTIONS = Object.freeze([
  { key: 'appearance', title: 'Внешний вид' },
  { key: 'solve', title: 'Решение задач' },
  { key: 'fx', title: 'Фоновые эффекты' },
  { key: 'profile', title: 'Профиль' },
  { key: 'preview', title: 'Предпросмотр' },
  { key: 'integrations', title: 'Связи' },
  { key: 'security', title: 'Безопасность' },
]);

export const SETTINGS_SECTION_KEYS = new Set(SETTINGS_SECTIONS.map((section) => section.key));

export const FX_OPTIONS = Object.freeze([
  { key: 'random', title: 'Случайный эффект при включении', desc: 'Каждый раз выбирается новый фон.' },
  { key: '0', title: 'Туман', desc: 'Мягкий туман и блёстки.' },
  { key: '1', title: 'Пыль + кометы', desc: 'Пыль, искры и редкие кометы.' },
  { key: '2', title: 'Нейронные связи', desc: 'Движущиеся точки и линии.' },
  { key: '3', title: 'Аврора', desc: 'Большие мягкие световые блики.' },
  { key: '4', title: 'Сердечки', desc: 'Плавающие сердечки на фоне.' },
  { key: '5', title: 'Matrix', desc: 'Падающие символы как в Матрице.' },
  { key: '6', title: 'Соты (мёд)', desc: 'Живые соты с мягкими волнами и искрами.' },
  { key: '7', title: 'Дым (вихри)', desc: 'Интерактивный дым с вихревыми завихрениями.' },
  { key: '8', title: 'Солнечная система', desc: 'Планеты, орбиты и быстрые кометы в цветах выбранной палитры.' },
]);

export function readLocalUiSettings() {
  try {
    const raw = localStorage.getItem(UI_SETTINGS_STORAGE_KEY);
    return raw ? JSON.parse(raw) : null;
  } catch {
    return null;
  }
}

export function defaultUiSettings() {
  return readLocalUiSettings() || {
    colorTheme: localStorage.getItem('colorTheme') || 'blue',
    mode: localStorage.getItem('mode') || 'light',
    uiStyle: localStorage.getItem('uiStyle') === 'default' ? 'default' : 'neobrutal',
    bgFx: localStorage.getItem('bgFx') === '1',
    fxMode: localStorage.getItem('fxMode') || 'random',
    fxVariant: localStorage.getItem('fxVariant') || '2',
    codeSolveLayout: localStorage.getItem('codeSolveLayout') || 'split',
    codeEditorStyle: localStorage.getItem('codeEditorStyle') === 'mono' ? 'mono' : 'color',
    showSidebarToggle: localStorage.getItem('showSidebarToggle') !== '0',
  };
}
