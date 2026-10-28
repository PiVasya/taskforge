import React, { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { useLocation, useNavigate } from 'react-router-dom';
import { useAuth } from '../../auth/AuthContext';
import { useEditorMode } from '../../contexts/EditorModeContext';
import { useNotificationCenter } from '../../components/notify/NotifyProvider';
import { SHORTCUTS, navigationShortcuts, shortcutAllowed, shortcutLabel } from './shortcutCatalog';
import { createShortcutController } from './shortcutController';

const STORAGE_KEY = 'taskforge.shortcuts.v1';
const Context = createContext(null);
export const useShortcuts = () => useContext(Context);

function loadSettings() {
  try {
    const value = JSON.parse(localStorage.getItem(STORAGE_KEY) || '{}');
    return { enabled: value?.enabled !== false, hints: value?.hints !== false };
  } catch { return { enabled: true, hints: true }; }
}

const GROUPS = {
  navigation: 'Переходы и меню',
  task: 'Решение заданий',
  compiler: 'Компилятор',
  page: 'Действия страницы',
  editing: 'Редактирование',
  admin: 'Админка',
  review: 'Раскрытая пара ИИ-дублей',
  tabs: 'Вкладки и разделы',
};

export function ShortcutList({ permissions }) {
  return <>{Object.entries(GROUPS).map(([group, title]) => {
    const rows = Object.entries(SHORTCUTS).filter(([, item]) => item.group === group && shortcutAllowed(item, permissions));
    if (!rows.length) return null;
    return <section key={group}>
      <h3 className="font-semibold mt-4 text-sm">{title}</h3>
      <dl className="tf-shortcut-list">{rows.map(([id, item]) => <div key={id}><dt>{item.label}</dt><dd>{shortcutLabel(id)}</dd></div>)}</dl>
    </section>;
  })}</>;
}

function ShortcutHelp({ onClose, permissions }) {
  const panelRef = useRef(null);
  const closeRef = useRef(null);
  useEffect(() => {
    const previous = document.activeElement;
    const overflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    closeRef.current?.focus({ preventScroll: true });
    const keyboard = (event) => {
      if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); onClose(); }
      if (event.key !== 'Tab') return;
      const items = [...panelRef.current.querySelectorAll('button:not(:disabled),a[href],input:not(:disabled),[tabindex="0"]')];
      const first = items[0], last = items[items.length - 1];
      if (event.shiftKey && (document.activeElement === first || !panelRef.current.contains(document.activeElement))) {
        event.preventDefault(); last?.focus();
      } else if (!event.shiftKey && (document.activeElement === last || !panelRef.current.contains(document.activeElement))) {
        event.preventDefault(); first?.focus();
      }
    };
    document.addEventListener('keydown', keyboard, true);
    return () => {
      document.removeEventListener('keydown', keyboard, true);
      document.body.style.overflow = overflow;
      if (previous?.isConnected) previous.focus?.({ preventScroll: true });
    };
  }, [onClose]);
  return createPortal(<div className="tf-shortcuts-backdrop" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose(); }}>
    <section ref={panelRef} className="tf-shortcuts-dialog" role="dialog" aria-modal="true" aria-labelledby="tf-shortcuts-title">
      <header><h2 id="tf-shortcuts-title">{ 'Горячие клавиши' }</h2><button type="button" ref={closeRef} className="btn-outline" onClick={onClose}>{ 'Закрыть' } (Esc)</button></header>
      <ShortcutInstructions />
      <ShortcutList permissions={permissions} />
    </section>
  </div>, document.body);
}

export function ShortcutInstructions() {
  return <div className="text-sm mt-3 space-y-2 opacity-80">
    <p>{'Удерживайте левый Alt: надписи на кнопках заменятся подсказками. Отпустите Alt, чтобы вернуть текст.'}</p>
    <p>{'Ctrl+Alt+клавиша раздела открывает новую вкладку. Ctrl+Alt+Enter открывает ссылку, выбранную клавишей Tab. Это не отправляет решение повторно.'}</p>
    <p>{'Действия и цифры работают только на экране с соответствующими кнопками. В открытом диалоге команды фоновой страницы заблокированы.'}</p>
    <p>{'Например, Alt+Enter — отправить решение, Alt+E — режим редактора (при наличии прав), Alt+C — курсы. Комбинации со стрелками, Alt+Tab и Alt+` остаются за браузером и Linux.'}</p>
    <p>{'Названия клавиш показаны полностью: Shift нужно удерживать вместе с Alt, а не нажимать стрелку вверх. Буквы работают независимо от раскладки. Правый Alt (AltGr) не используется.'}</p>
  </div>;
}

export default function ShortcutProvider({ children }) {
  const { isAuthenticated } = useAuth();
  const { isAdmin, canEdit, hasRole, toggle } = useEditorMode();
  const { openCenter } = useNotificationCenter();
  const [settings, setSettings] = useState(loadSettings);
  const [pinned, setPinned] = useState(false);
  const [helpOpen, setHelpOpen] = useState(false);
  const navigate = useNavigate();
  const location = useLocation();
  const controller = useRef(null);
  const runtime = useRef(null);
  const permissions = useMemo(() => ({ authenticated: Boolean(isAuthenticated), admin: isAdmin,
    editor: canEdit, minecraft: isAdmin || hasRole('Minecraft') }), [isAuthenticated, isAdmin, canEdit, hasRole]);
  runtime.current = { ...permissions, ...settings, pinned, actions: {
    notifications: openCenter, editorMode: toggle,
  }, navigate };
  const closeHelp = useCallback(() => setHelpOpen(false), []);
  const showHelp = useCallback(() => setHelpOpen(true), []);
  const hideHints = useCallback(() => setPinned(false), []);
  useEffect(() => {
    controller.current = createShortcutController({ getState: () => runtime.current,
      navigate: (href) => runtime.current.navigate(href), openHelp: showHelp, clearPinned: hideHints });
    return () => { controller.current?.dispose(); controller.current = null; };
  }, [showHelp, hideHints]);
  useEffect(() => { controller.current?.refresh(); }, [settings, pinned, permissions, helpOpen]);
  useEffect(() => { controller.current?.reset(); setHelpOpen(false); }, [location.pathname, location.search]);
  const updateSettings = useCallback((patch) => {
    setSettings((previous) => {
      const next = { ...previous, ...patch };
      try { localStorage.setItem(STORAGE_KEY, JSON.stringify(next)); } catch { /* Session controls still work. */ }
      return next;
    });
    if (patch.enabled === false || patch.hints === false) setPinned(false);
  }, []);
  const toggleHints = useCallback(() => { updateSettings({ enabled: true, hints: true }); setPinned((value) => !value); }, [updateSettings]);
  const value = useMemo(() => ({ ...settings, pinned, permissions, updateSettings, toggleHints, showHelp,
    navigation: navigationShortcuts(permissions) }), [settings, pinned, permissions, updateSettings, toggleHints, showHelp]);
  return <Context.Provider value={value}>
    {children}
    {pinned ? createPortal(<button type="button" className="btn-outline tf-shortcuts-pinned" onClick={hideHints}>{'Скрыть комбинации'} (Esc)</button>, document.body) : null}
    {helpOpen ? <ShortcutHelp onClose={closeHelp} permissions={permissions} /> : null}
  </Context.Provider>;
}
