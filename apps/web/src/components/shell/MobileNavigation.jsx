import React, { useCallback, useEffect, useRef } from 'react';
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

function MobileNavigation({ open, onClose }) {
  const { pathname } = useLocation();
  const navigate = useNavigate();
  const { access, logout } = useAuth();
  const { canEdit, isEditorMode, toggle, isAdmin } = useEditorMode();
  const { currentViewTitle, primaryNav, adminNav } = useShellNavigation();

  const previousPathRef = useRef(pathname);

  useEffect(() => {
    if (previousPathRef.current === pathname) return;
    previousPathRef.current = pathname;
    onClose();
  }, [onClose, pathname]);

  useEffect(() => {
    if (!open) return undefined;
    const previousOverflow = document.body.style.overflow;
    const onKeyDown = (event) => {
      if (event.key === 'Escape') onClose();
    };
    document.body.style.overflow = 'hidden';
    window.addEventListener('keydown', onKeyDown);
    return () => {
      document.body.style.overflow = previousOverflow;
      window.removeEventListener('keydown', onKeyDown);
    };
  }, [onClose, open]);

  const handleLogout = useCallback(async () => {
    onClose();
    try {
      await logout();
    } catch {}
    navigate('/', { replace: true });
  }, [logout, navigate, onClose]);

  if (!open) return null;

  return (
    <div className="mobile-nav-overlay xl:hidden" role="dialog" aria-modal="true" aria-label="Меню TaskForge">
      <button
        type="button"
        className="mobile-nav-backdrop"
        onClick={onClose}
        aria-label="Закрыть меню"
      />
      <div className="mobile-nav-sheet">
        <div className="mobile-nav-sheet__header">
          <div className="min-w-0">
            <div className="text-lg font-semibold truncate">TaskForge</div>
            <div className="text-xs text-neutral-500 dark:text-neutral-400 truncate">
              {currentViewTitle}
            </div>
          </div>
          <button type="button" className="mobile-nav-close" onClick={onClose} aria-label="Закрыть меню">
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
    </div>
  );
}

export default React.memo(MobileNavigation);
