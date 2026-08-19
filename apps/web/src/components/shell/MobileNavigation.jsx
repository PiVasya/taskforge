import React, { useCallback, useEffect, useRef } from 'react';
import { createPortal } from 'react-dom';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import {
  Eye,
  LogIn,
  LogOut,
  PencilLine,
  Settings,
  User,
  X,
} from 'lucide-react';
import { useAuth } from '../../auth/AuthContext';
import { useEditorMode } from '../../contexts/EditorModeContext';
import QuotaStatusBar from '../QuotaStatusBar';
import { useShellNavigation } from './navigation';

const FOCUSABLE_SELECTOR = [
  'a[href]',
  'button:not([disabled])',
  'input:not([disabled])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  '[tabindex]:not([tabindex="-1"])',
].join(',');

function getFocusableElements(container) {
  if (!container) return [];
  return Array.from(container.querySelectorAll(FOCUSABLE_SELECTOR)).filter((element) => {
    if (element.hidden || element.getAttribute('aria-hidden') === 'true') return false;
    return element.getClientRects().length > 0;
  });
}

function MobileNavigation({ open, onClose }) {
  const { pathname } = useLocation();
  const navigate = useNavigate();
  const { access, logout } = useAuth();
  const { canEdit, isEditorMode, toggle, isAdmin } = useEditorMode();
  const { currentViewTitle, primaryNav, adminNav } = useShellNavigation();

  const previousPathRef = useRef(pathname);
  const currentPathRef = useRef(pathname);
  currentPathRef.current = pathname;
  const sheetRef = useRef(null);
  const closeButtonRef = useRef(null);
  const restoreFocusRef = useRef(null);

  useEffect(() => {
    if (previousPathRef.current === pathname) return;
    previousPathRef.current = pathname;
    onClose();
  }, [onClose, pathname]);

  useEffect(() => {
    if (!open) return undefined;

    const html = document.documentElement;
    const body = document.body;
    const appRoot = document.getElementById('root');
    const rootWasInert = appRoot?.hasAttribute('inert') || false;
    const lockedPathname = pathname;
    const scrollY = window.scrollY;
    const scrollX = window.scrollX;
    const previousHtmlOverflow = html.style.overflow;
    const previousHtmlOverscrollBehavior = html.style.overscrollBehavior;
    const previousBodyOverflow = body.style.overflow;
    const previousBodyPosition = body.style.position;
    const previousBodyTop = body.style.top;
    const previousBodyLeft = body.style.left;
    const previousBodyRight = body.style.right;
    const previousBodyWidth = body.style.width;

    const menuOpener = document.querySelector('[aria-controls="taskforge-mobile-navigation"]');
    restoreFocusRef.current = menuOpener instanceof HTMLElement
      ? menuOpener
      : document.activeElement instanceof HTMLElement
        ? document.activeElement
        : null;

    if (appRoot) appRoot.setAttribute('inert', '');

    html.style.overflow = 'hidden';
    html.style.overscrollBehavior = 'none';
    body.style.overflow = 'hidden';
    body.style.position = 'fixed';
    body.style.top = `-${scrollY}px`;
    body.style.left = `-${scrollX}px`;
    body.style.right = '0';
    body.style.width = '100%';

    const focusFrame = window.requestAnimationFrame(() => {
      closeButtonRef.current?.focus({ preventScroll: true });
    });

    const onKeyDown = (event) => {
      if (event.key === 'Escape') {
        event.preventDefault();
        onClose();
        return;
      }

      if (event.key !== 'Tab') return;
      const focusable = getFocusableElements(sheetRef.current);
      if (!focusable.length) {
        event.preventDefault();
        sheetRef.current?.focus({ preventScroll: true });
        return;
      }

      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      const active = document.activeElement;

      if (!sheetRef.current?.contains(active)) {
        event.preventDefault();
        first.focus({ preventScroll: true });
      } else if (event.shiftKey && active === first) {
        event.preventDefault();
        last.focus({ preventScroll: true });
      } else if (!event.shiftKey && active === last) {
        event.preventDefault();
        first.focus({ preventScroll: true });
      }
    };

    document.addEventListener('keydown', onKeyDown, true);

    return () => {
      window.cancelAnimationFrame(focusFrame);
      document.removeEventListener('keydown', onKeyDown, true);

      html.style.overflow = previousHtmlOverflow;
      html.style.overscrollBehavior = previousHtmlOverscrollBehavior;
      body.style.overflow = previousBodyOverflow;
      body.style.position = previousBodyPosition;
      body.style.top = previousBodyTop;
      body.style.left = previousBodyLeft;
      body.style.right = previousBodyRight;
      body.style.width = previousBodyWidth;
      if (appRoot && !rootWasInert) appRoot.removeAttribute('inert');

      const routeChanged = currentPathRef.current !== lockedPathname;
      if (!routeChanged) {
        window.scrollTo({ top: scrollY, left: scrollX, behavior: 'auto' });

        const opener = restoreFocusRef.current;
        if (opener?.isConnected) {
          window.requestAnimationFrame(() => opener.focus({ preventScroll: true }));
        }
      }
    };
  }, [onClose, open, pathname]);

  const handleLogout = useCallback(async () => {
    onClose();
    try {
      await logout();
    } catch {}
    navigate('/', { replace: true });
  }, [logout, navigate, onClose]);

  if (!open) return null;

  return createPortal(
    <div className="mobile-nav-overlay xl:hidden">
      <div
        className="mobile-nav-backdrop"
        onPointerDown={onClose}
        aria-hidden="true"
      />
      <div
        id="taskforge-mobile-navigation"
        ref={sheetRef}
        className="mobile-nav-sheet"
        role="dialog"
        aria-modal="true"
        aria-labelledby="mobile-nav-title"
        tabIndex={-1}
      >
        <div className="mobile-nav-sheet__header">
          <div className="min-w-0">
            <div id="mobile-nav-title" className="text-lg font-semibold truncate">TaskForge</div>
            <div className="text-xs text-neutral-500 dark:text-neutral-400 truncate">
              {currentViewTitle}
            </div>
          </div>
          <button
            ref={closeButtonRef}
            type="button"
            className="mobile-nav-close"
            onClick={onClose}
            aria-label="Закрыть меню"
          >
            <X size={20} />
          </button>
        </div>

        {access ? <QuotaStatusBar mobile className="mobile-nav-sheet__quota" /> : null}

        <div className="mobile-nav-sheet__content">
          <section className="mobile-nav-section">
            <div className="mobile-nav-section__title">Навигация</div>
            <div className="mobile-nav-grid">
              {primaryNav.map((item) => (
                <Link
                  key={item.to}
                  to={item.to}
                  className={`mobile-nav-card ${item.active ? 'is-active' : ''}`}
                  onClick={onClose}
                >
                  <item.icon size={19} />
                  <span>{item.label}</span>
                </Link>
              ))}
            </div>
          </section>

          {access && (
            <section className="mobile-nav-section">
              <div className="mobile-nav-section__title">Аккаунт</div>
              <div className="mobile-nav-grid">
                <Link to="/settings?section=profile" className="mobile-nav-card" onClick={onClose}>
                  <User size={19} />
                  <span>Профиль</span>
                </Link>
                <Link to="/settings" className="mobile-nav-card" onClick={onClose}>
                  <Settings size={19} />
                  <span>Настройки</span>
                </Link>
                {canEdit ? (
                  <button
                    type="button"
                    className={`mobile-nav-card ${isEditorMode ? 'is-active' : ''}`}
                    onClick={() => {
                      toggle();
                      onClose();
                    }}
                  >
                    {isEditorMode ? <PencilLine size={19} /> : <Eye size={19} />}
                    <span>{isEditorMode ? 'Редактор' : 'Просмотр'}</span>
                  </button>
                ) : null}
              </div>
            </section>
          )}

          {isAdmin && adminNav.length > 0 && (
            <section className="mobile-nav-section">
              <div className="mobile-nav-section__title">Админ-панель</div>
              <div className="mobile-nav-grid mobile-nav-grid--admin">
                {adminNav.map((item) => (
                  <Link
                    key={item.to}
                    to={item.to}
                    className={`mobile-nav-card ${item.active ? 'is-active' : ''}`}
                    onClick={onClose}
                  >
                    <item.icon size={19} />
                    <span>{item.label}</span>
                  </Link>
                ))}
              </div>
            </section>
          )}

          <section className="mobile-nav-section mobile-nav-section--last">
            {access ? (
              <button type="button" className="mobile-nav-card mobile-nav-card--danger w-full" onClick={handleLogout}>
                <LogOut size={19} />
                <span>Выйти</span>
              </button>
            ) : (
              <div className="grid grid-cols-2 gap-2">
                <Link to="/login" className="mobile-nav-card" onClick={onClose}>
                  <LogIn size={19} />
                  <span>Войти</span>
                </Link>
                <Link to="/register" className="mobile-nav-card is-active" onClick={onClose}>
                  <User size={19} />
                  <span>Регистрация</span>
                </Link>
              </div>
            )}
          </section>
        </div>
      </div>
    </div>,
    document.body,
  );
}

export default React.memo(MobileNavigation);
