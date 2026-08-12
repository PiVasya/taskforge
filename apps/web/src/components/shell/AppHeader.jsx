import React, { useCallback, useEffect, useRef, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import {
  BarChart2,
  ChevronLeft,
  ChevronRight,
  Eye,
  House,
  LifeBuoy,
  LogIn,
  LogOut,
  Menu,
  MoreHorizontal,
  PanelsTopLeft,
  PencilLine,
  Settings,
  User,
} from 'lucide-react';
import { useAuth } from '../../auth/AuthContext';
import { useEditorMode } from '../../contexts/EditorModeContext';
import { useUiNavigationSettings } from '../../contexts/UiSettingsContext';
import QuotaStatusBar from '../QuotaStatusBar';
import AdminNavigation from './AdminNavigation';
import { useShellNavigation } from './navigation';

const HeaderAction = React.memo(function HeaderAction({
  to,
  icon: Icon,
  label,
  active,
  onClick,
  asButton = false,
}) {
  const className = `header-quick-action ${active ? 'is-active' : ''}`;
  if (asButton) {
    return (
      <button type="button" className={className} onClick={onClick} title={label}>
        <Icon size={16} />
        <span>{label}</span>
      </button>
    );
  }
  return (
    <Link to={to} className={className} title={label}>
      <Icon size={16} />
      <span>{label}</span>
    </Link>
  );
});

function getDisplayName(user) {
  const first = String(user?.firstName || '').trim();
  const last = String(user?.lastName || '').trim();
  const full = [last, first].filter(Boolean).join(' ');
  return full || String(user?.login || user?.email || 'Личный кабинет').trim() || 'Личный кабинет';
}

function getInitials(user) {
  const first = String(user?.firstName || '').trim();
  const last = String(user?.lastName || '').trim();
  const initials = `${last ? last[0] : ''}${first ? first[0] : ''}`.trim();
  if (initials) return initials.toUpperCase();
  const login = String(user?.login || user?.email || '').trim();
  return login ? login[0].toUpperCase() : 'TF';
}

function AppHeader({ onOpenMobile, mobileOpen = false }) {
  const { access, logout, user } = useAuth();
  const { canEdit, isEditorMode, toggle, isAdmin } = useEditorMode();
  const {
    sidebarCollapsed,
    showSidebarToggle,
    toggleSidebarCollapsed,
  } = useUiNavigationSettings();
  const {
    pathname,
    currentViewTitle,
    supportHref,
    adminSecondaryNav,
    isActive,
  } = useShellNavigation();
  const navigate = useNavigate();
  const [quickOpen, setQuickOpen] = useState(false);
  const quickMenuRef = useRef(null);

  useEffect(() => {
    setQuickOpen(false);
  }, [pathname]);

  useEffect(() => {
    if (!quickOpen) return undefined;
    const closeOutside = (event) => {
      if (!quickMenuRef.current?.contains(event.target)) setQuickOpen(false);
    };
    window.addEventListener('mousedown', closeOutside);
    window.addEventListener('touchstart', closeOutside);
    return () => {
      window.removeEventListener('mousedown', closeOutside);
      window.removeEventListener('touchstart', closeOutside);
    };
  }, [quickOpen]);

  const handleLogout = useCallback(async () => {
    setQuickOpen(false);
    try {
      await logout();
    } catch {}
    navigate('/', { replace: true });
  }, [logout, navigate]);

  const brandHref = access ? '/news' : '/';
  const displayName = getDisplayName(user);
  const displaySubline = isAdmin
    ? 'Администратор'
    : canEdit
      ? 'Редактор'
      : user?.login || user?.email || 'Участник';
  const avatarUrl = user?.profilePictureUrl || user?.avatarUrl || '';

  return (
    <header
      className="sticky top-0 z-20 backdrop-blur bg-white/70 dark:bg-neutral-900/60"
      style={{ borderBottom: '1px solid rgba(var(--border) / 0.7)' }}
    >
      <div className="container-app flex min-h-16 items-center justify-between gap-3 py-2">
        <div className="flex min-w-0 items-center gap-3">
          {access && showSidebarToggle ? (
            <>
              <button
                type="button"
                onClick={toggleSidebarCollapsed}
                className="hidden xl:grid h-11 w-11 shrink-0 place-items-center rounded-2xl shadow-soft border border-neutral-200/60 dark:border-neutral-800/60 bg-white/60 dark:bg-neutral-900/40 text-neutral-900 dark:text-neutral-100 hover:bg-[rgba(var(--border)/0.16)] transition"
                title={sidebarCollapsed ? 'Развернуть левое меню' : 'Свернуть левое меню'}
                aria-label={sidebarCollapsed ? 'Развернуть левое меню' : 'Свернуть левое меню'}
              >
                {sidebarCollapsed ? <ChevronRight size={18} /> : <ChevronLeft size={18} />}
              </button>
              <Link
                to={brandHref}
                className="xl:hidden h-11 w-11 shrink-0 rounded-2xl grid place-items-center shadow-soft border border-neutral-200/60 dark:border-neutral-800/60 bg-white/60 dark:bg-neutral-900/40 text-neutral-900 dark:text-neutral-100"
                title="TaskForge"
              >
                <PanelsTopLeft size={18} />
              </Link>
            </>
          ) : (
            <Link
              to={brandHref}
              className="h-11 w-11 shrink-0 rounded-2xl grid place-items-center shadow-soft border border-neutral-200/60 dark:border-neutral-800/60 bg-white/60 dark:bg-neutral-900/40 text-neutral-900 dark:text-neutral-100"
              title="TaskForge"
            >
              <PanelsTopLeft size={18} />
            </Link>
          )}
          <Link to={brandHref} className="min-w-0">
            <div className="font-semibold truncate text-base">TaskForge</div>
            <div className="hidden sm:block text-xs text-neutral-500 dark:text-neutral-400 truncate">
              {currentViewTitle}
            </div>
          </Link>
        </div>

        {access && (
          <div className="header-center-cluster hidden xl:flex min-w-0 flex-1 justify-center px-4">
            <div className="header-quick-row min-w-0">
              {isAdmin && (
                <HeaderAction
                  to="/admin/analytics"
                  icon={BarChart2}
                  label="Аналитика"
                  active={isActive('/admin/analytics')}
                />
              )}
              <HeaderAction
                to={supportHref}
                icon={LifeBuoy}
                label="Поддержка"
                active={isActive(supportHref)}
              />
              <HeaderAction
                to="/settings"
                icon={Settings}
                label="Настройки"
                active={isActive('/settings')}
              />
              {canEdit && (
                <HeaderAction
                  asButton
                  icon={isEditorMode ? PencilLine : Eye}
                  label={isEditorMode ? 'Редактор' : 'Просмотр'}
                  active={isEditorMode}
                  onClick={toggle}
                />
              )}
            </div>
            <QuotaStatusBar compact />
          </div>
        )}

        <div className="flex items-center gap-2">
          {access && (
            <Link
              to="/settings?section=profile"
              className="hidden xl:flex items-center gap-3 rounded-2xl border border-neutral-200/60 dark:border-neutral-800/60 bg-[rgb(var(--card))]/80 px-3 py-2 shadow-soft max-w-[20rem]"
              title="Профиль"
            >
              <div className="h-10 w-10 rounded-2xl overflow-hidden shrink-0 grid place-items-center bg-brand-600/15 text-brand-700 dark:text-brand-300 font-semibold">
                {avatarUrl ? (
                  <img src={avatarUrl} alt={displayName} className="h-full w-full object-cover" />
                ) : (
                  <span>{getInitials(user)}</span>
                )}
              </div>
              <div className="min-w-0">
                <div className="text-sm font-semibold truncate">{displayName}</div>
                <div className="text-xs text-neutral-500 dark:text-neutral-400 truncate">
                  {displaySubline}
                </div>
              </div>
            </Link>
          )}

          {access ? (
            <div className="hidden md:block relative" ref={quickMenuRef}>
              <button
                type="button"
                className="btn-outline"
                onClick={() => setQuickOpen((value) => !value)}
                aria-haspopup="menu"
                aria-expanded={quickOpen}
                title="Быстрые действия"
              >
                <MoreHorizontal size={18} />
              </button>
              {quickOpen && (
                <div className="shell-quick-menu absolute right-0 mt-2 w-72 rounded-2xl border border-neutral-200/60 dark:border-neutral-800/60 bg-[rgb(var(--card))] shadow-soft p-1 z-50">
                  <Link to="/settings?section=profile" className="btn-ghost w-full justify-start" onClick={() => setQuickOpen(false)}>
                    <User size={18} />
                    <span className="ml-2">Профиль</span>
                  </Link>
                  <Link to="/settings" className="btn-ghost w-full justify-start xl:hidden" onClick={() => setQuickOpen(false)}>
                    <Settings size={18} />
                    <span className="ml-2">Настройки</span>
                  </Link>
                  {canEdit && (
                    <button
                      type="button"
                      className="btn-ghost w-full justify-start"
                      onClick={() => {
                        toggle();
                        setQuickOpen(false);
                      }}
                    >
                      {isEditorMode ? <PencilLine size={18} /> : <Eye size={18} />}
                      <span className="ml-2">{isEditorMode ? 'Режим редактора' : 'Режим просмотра'}</span>
                    </button>
                  )}
                  {isAdmin && adminSecondaryNav.length > 0 && (
                    <>
                      <div className="my-1 h-px bg-neutral-200/70 dark:bg-neutral-800/70" />
                      <div className="px-3 py-2 text-xs uppercase tracking-wide opacity-70">Система</div>
                      {adminSecondaryNav.map((item) => (
                        <Link
                          key={item.to}
                          to={item.to}
                          className="btn-ghost w-full justify-start"
                          onClick={() => setQuickOpen(false)}
                        >
                          <item.icon size={18} />
                          <span className="ml-2">{item.label}</span>
                        </Link>
                      ))}
                    </>
                  )}
                  <div className="my-1 h-px bg-neutral-200/70 dark:bg-neutral-800/70" />
                  <Link to={supportHref} className="btn-ghost w-full justify-start" onClick={() => setQuickOpen(false)}>
                    <LifeBuoy size={18} />
                    <span className="ml-2">Поддержка</span>
                  </Link>
                  <button type="button" className="btn-ghost w-full justify-start" onClick={handleLogout}>
                    <LogOut size={18} />
                    <span className="ml-2">Выйти</span>
                  </button>
                </div>
              )}
            </div>
          ) : (
            <div className="hidden md:flex items-center gap-2">
              <Link to="/news" className="btn-outline" title="Лента обновлений">
                <House size={18} />
                <span>Лента</span>
              </Link>
              <Link to="/login" className="btn-primary" title="Войти">
                <LogIn size={18} />
                <span>Войти</span>
              </Link>
            </div>
          )}

          <div className="xl:hidden">
            <button
              type="button"
              className="btn-outline !min-w-0 h-12 w-12 shrink-0 px-0 sm:h-auto sm:w-auto sm:px-3"
              onClick={onOpenMobile}
              title="Меню"
              aria-haspopup="dialog"
              aria-expanded={mobileOpen}
            >
              <Menu size={20} />
            </button>
          </div>
        </div>
      </div>

      <AdminNavigation />
    </header>
  );
}

export default React.memo(AppHeader);
