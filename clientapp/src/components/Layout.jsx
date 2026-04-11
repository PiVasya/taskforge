import React, { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { Link, useLocation } from 'react-router-dom';
import {
  PanelsTopLeft,
  LogOut,
  LogIn,
  PencilLine,
  Eye,
  User,
  Users,
  BarChart2,
  ListOrdered,
  MoreHorizontal,
  Settings,
  Award,
  Shield,
  LifeBuoy,
  MessageSquare,
  Activity,
  UserCog,
  Link2,
  Menu,
  House,
  GraduationCap,
  Trophy,
  ChevronRight,
  Brain,
} from 'lucide-react';
import { motion } from 'framer-motion';
import { useAuth } from '../auth/AuthContext';
import { useEditorMode } from '../contexts/EditorModeContext';
import BgFxCanvas from './bgfx/BgFxCanvas';
import api from '../api/http';
import { QuotaProvider } from '../contexts/QuotaContext';
import QuotaStatusBar from './QuotaStatusBar';

function SideNavLink({ to, icon: Icon, label, subtitle, active, onClick, asButton = false, compact = false }) {
  const cls = `side-nav-link ${active ? 'is-active' : ''} ${compact ? 'is-compact' : ''}`;
  const title = subtitle ? `${label} — ${subtitle}` : label;

  if (asButton) {
    return (
      <button type="button" className={cls} onClick={onClick} title={title}>
        <span className="side-nav-icon"><Icon size={18} /></span>
        <span className="min-w-0 flex-1 text-left">
          <span className="side-nav-label">{label}</span>
        </span>
        <ChevronRight size={16} className="side-nav-chevron" />
      </button>
    );
  }

  return (
    <Link to={to} className={cls} title={title}>
      <span className="side-nav-icon"><Icon size={18} /></span>
      <span className="min-w-0 flex-1">
        <span className={`side-nav-label ${compact ? 'is-compact' : ''}`}>{label}</span>
      </span>
      <ChevronRight size={16} className="side-nav-chevron" />
    </Link>
  );
}

function HeaderAction({ to, icon: Icon, label, active, onClick, asButton = false }) {
  const cls = `header-quick-action ${active ? 'is-active' : ''}`;

  if (asButton) {
    return (
      <button type="button" className={cls} onClick={onClick} title={label}>
        <Icon size={16} />
        <span>{label}</span>
      </button>
    );
  }

  return (
    <Link to={to} className={cls} title={label}>
      <Icon size={16} />
      <span>{label}</span>
    </Link>
  );
}

function getDisplayName(user) {
  const first = String(user?.firstName || '').trim();
  const last = String(user?.lastName || '').trim();
  const full = [last, first].filter(Boolean).join(' ');
  if (full) return full;
  return String(user?.email || 'Личный кабинет').trim() || 'Личный кабинет';
}

function getInitials(user) {
  const first = String(user?.firstName || '').trim();
  const last = String(user?.lastName || '').trim();
  const initials = `${last ? last[0] : ''}${first ? first[0] : ''}`.trim();
  if (initials) return initials.toUpperCase();
  const email = String(user?.email || '').trim();
  return email ? email[0].toUpperCase() : 'TF';
}

export default function Layout({ children, fullWidth = false, hideFooter = false }) {
  const applyHtmlThemeClasses = (nextMode, nextColorTheme) => {
    const root = document.documentElement;
    const palettes = ['blue', 'pink', 'apple', 'red', 'honey', 'violet'];
    const palette = palettes.includes(nextColorTheme) ? nextColorTheme : 'pink';

    root.classList.remove('blue', 'pink', 'apple', 'red', 'honey');
    root.classList.add(palette);

    if (nextMode === 'dark') root.classList.add('dark');
    else root.classList.remove('dark');
  };

  const readUiSettings = () => {
    try {
      const raw = localStorage.getItem('uiSettings');
      return raw ? JSON.parse(raw) : null;
    } catch {
      return null;
    }
  };

  const initialUi = readUiSettings();
  const [colorTheme, setColorTheme] = useState(() => initialUi?.colorTheme || localStorage.getItem('colorTheme') || 'pink');
  const [mode, setMode] = useState(() => initialUi?.mode || localStorage.getItem('mode') || 'dark');
  const lastTrackedPathRef = useRef('');
  const [uiRev, setUiRev] = useState(0);

  useLayoutEffect(() => {
    applyHtmlThemeClasses(mode, colorTheme);
  }, [mode, colorTheme]);

  const [bgFx, setBgFx] = useState(() => (typeof initialUi?.bgFx === 'boolean' ? initialUi.bgFx : localStorage.getItem('bgFx') === '1'));
  const [fxMode, setFxMode] = useState(() => initialUi?.fxMode || localStorage.getItem('fxMode') || 'random');
  const [fxVariant, setFxVariant] = useState(() => {
    if (initialUi?.fxVariant != null) return String(initialUi.fxVariant);
    const stored = localStorage.getItem('fxVariant');
    return stored != null ? stored : '2';
  });

  useEffect(() => {
    const applyFromStorage = () => {
      const nextColor = localStorage.getItem('colorTheme') || 'pink';
      const nextMode = localStorage.getItem('mode') || 'dark';

      applyHtmlThemeClasses(nextMode, nextColor);
      setColorTheme(nextColor);
      setMode(nextMode);
      const ui = readUiSettings();
      setBgFx(localStorage.getItem('bgFx') === '1');
      setFxMode(ui?.fxMode || localStorage.getItem('fxMode') || 'random');
      setFxVariant(String(ui?.fxVariant ?? localStorage.getItem('fxVariant') ?? '2'));
      setUiRev((r) => r + 1);
    };

    const onStorage = (e) => {
      if (!e.key) return;
      if (['colorTheme', 'mode', 'bgFx', 'fxMode', 'fxVariant'].includes(e.key)) {
        applyFromStorage();
      }
    };

    const onLocalUiChanged = () => applyFromStorage();

    window.addEventListener('storage', onStorage);
    window.addEventListener('tf-ui-settings-changed', onLocalUiChanged);

    applyFromStorage();
    return () => {
      window.removeEventListener('storage', onStorage);
      window.removeEventListener('tf-ui-settings-changed', onLocalUiChanged);
    };
  }, []);

  const { access, logout, user } = useAuth();
  const { canEdit, isEditorMode, toggle, isAdmin, roles } = useEditorMode();
  const canUseMinecraft = isAdmin || roles.includes('Minecraft');
  const location = useLocation();

  useEffect(() => {
    if (!access) return;
    const path = `${location.pathname}${location.search || ''}`;
    if (!path || path === lastTrackedPathRef.current) return;
    lastTrackedPathRef.current = path;
    const title = document?.title || path;
    api.post('/api/activity/page-view', { path, title }).catch(() => {});
  }, [access, location.pathname, location.search]);

  const [mobileOpen, setMobileOpen] = useState(false);
  const mobileMenuRef = useRef(null);
  const [adminOpen, setAdminOpen] = useState(false);
  const adminRef = useRef(null);

  const handleLogout = () => {
    setAdminOpen(false);
    try {
      logout();
    } catch {
      // ignore
    }
  };

  useEffect(() => {
    if (!adminOpen) return;
    const onDown = (e) => {
      const el = adminRef.current;
      if (!el) return;
      if (!el.contains(e.target)) setAdminOpen(false);
    };
    window.addEventListener('mousedown', onDown);
    window.addEventListener('touchstart', onDown);
    return () => {
      window.removeEventListener('mousedown', onDown);
      window.removeEventListener('touchstart', onDown);
    };
  }, [adminOpen]);

  useEffect(() => {
    if (!mobileOpen) return;
    const onDown = (e) => {
      const el = mobileMenuRef.current;
      if (!el) return;
      if (!el.contains(e.target)) setMobileOpen(false);
    };
    window.addEventListener('mousedown', onDown);
    window.addEventListener('touchstart', onDown);
    return () => {
      window.removeEventListener('mousedown', onDown);
      window.removeEventListener('touchstart', onDown);
    };
  }, [mobileOpen]);

  useEffect(() => {
    setMobileOpen(false);
    setAdminOpen(false);
  }, [location.pathname]);

  const currentPath = location.pathname;
  const isActive = (href) => currentPath === href || currentPath.startsWith(`${href}/`);
  const supportHref = isAdmin ? '/admin/support' : '/support';
  const primaryNav = [
    { to: '/news', label: 'Лента', subtitle: 'Новости и обновления', icon: House, active: isActive('/news') },
    { to: '/courses', label: 'Курсы', subtitle: 'Каталог заданий', icon: GraduationCap, active: isActive('/courses') || isActive('/course') },
    access && { to: '/my/solutions', label: 'Мои решения', subtitle: 'История отправок', icon: ListOrdered, active: isActive('/my/solutions') },
    access && { to: '/leaderboard', label: 'Рейтинг', subtitle: 'Топ студентов', icon: Trophy, active: isActive('/leaderboard') },
    access && { to: supportHref, label: 'Поддержка', subtitle: isAdmin ? 'Тикеты пользователей' : 'Мои обращения', icon: LifeBuoy, active: isActive(supportHref) },
    access && canUseMinecraft && { to: '/minecraft/chat', label: 'Minecraft', subtitle: 'Игровой чат', icon: MessageSquare, active: isActive('/minecraft/chat') },
  ].filter(Boolean);

  const adminPrimaryNav = isAdmin
    ? [
        { to: '/admin/analytics', label: 'Аналитика', subtitle: 'Сводки и графики', icon: BarChart2, active: isActive('/admin/analytics') },
        { to: '/admin/activity', label: 'Действия', subtitle: 'Логи пользователей', icon: Activity, active: isActive('/admin/activity') },
        { to: '/admin/users', label: 'Пользователи', subtitle: 'Профили и роли', icon: UserCog, active: isActive('/admin/users') },
        { to: '/admin/groups', label: 'Группы', subtitle: 'Команды и потоки', icon: Users, active: isActive('/admin/groups') },
        { to: '/admin/solutions', label: 'Решения', subtitle: 'Проверки и статусы', icon: ListOrdered, active: isActive('/admin/solutions') },
        { to: '/admin/badges', label: 'Бейджи', subtitle: 'Награды и витрина', icon: Award, active: isActive('/admin/badges') },
      ]
    : [];

  const adminSecondaryNav = isAdmin
    ? [
        { to: '/admin/minecraft-links', label: 'Связи Minecraft', subtitle: 'Привязки игроков', icon: Link2, active: isActive('/admin/minecraft-links') },
        { to: '/admin/system-status', label: 'Статус', subtitle: 'Компоненты и раннеры', icon: Activity, active: isActive('/admin/system-status') },
        { to: '/admin/feature-roles', label: 'Доп. роли', subtitle: 'Права и фичи', icon: Shield, active: isActive('/admin/feature-roles') },
        { to: '/admin/ai', label: 'AI', subtitle: 'Очередь и драфты', icon: Brain, active: isActive('/admin/ai') && !isActive('/admin/ai/chat') },
        { to: '/admin/ai/chat', label: 'AI чат', subtitle: 'Чат и инструменты', icon: Brain, active: isActive('/admin/ai/chat') },
      ]
    : [];

  const adminNav = [...adminPrimaryNav, ...adminSecondaryNav];

  const roleBadges = roles.filter(Boolean).slice(0, 4);
  const currentViewTitle = (() => {
    if (currentPath.startsWith('/admin/')) return 'Админ-панель';
    if (currentPath.startsWith('/minecraft/')) return 'Minecraft';
    if (currentPath.startsWith('/support')) return 'Поддержка';
    if (currentPath.startsWith('/course') || currentPath.startsWith('/courses')) return 'Курсы';
    if (currentPath.startsWith('/leaderboard')) return 'Рейтинг';
    if (currentPath.startsWith('/settings')) return 'Настройки';
    if (currentPath.startsWith('/profile')) return 'Профиль';
    if (currentPath.startsWith('/my/solutions')) return 'Мои решения';
    return 'Лента';
  })();


  const displayName = getDisplayName(user);
  const displaySubline = isAdmin ? 'Администратор' : canEdit ? 'Редактор' : (user?.email || 'Участник');
  const avatarUrl = user?.profilePictureUrl || user?.avatarUrl || '';
  const avatarFallback = getInitials(user);

  const mainWrapClass = fullWidth
    ? 'w-full max-w-none px-3 sm:px-5 lg:px-6 xl:px-8 2xl:px-10 py-4 sm:py-8 relative z-10'
    : 'container-app py-4 sm:py-8 relative z-10';

  return (
    <QuotaProvider enabled={!!access}>
      <div className="min-h-screen relative isolate">
      <div className="pointer-events-none fixed inset-0 z-0 overflow-hidden">
        {bgFx && (
          <>
            <div className="absolute inset-0 bg-gradient-to-b from-brand-600/12 via-transparent to-transparent blur-2xl" />
            <div className="absolute inset-0">
              <BgFxCanvas
                enabled={bgFx}
                variant={fxMode === 'random' ? 'random' : Number(fxVariant) || 0}
                uiRev={uiRev}
              />
            </div>
          </>
        )}
      </div>

      <header className="sticky top-0 z-20 backdrop-blur bg-white/70 dark:bg-neutral-900/60" style={{ borderBottom: '1px solid rgba(var(--border) / 0.7)' }}>
        <div className="container-app flex min-h-16 items-center justify-between gap-3 py-2">
          <Link to="/news" className="flex min-w-0 items-center gap-3">
            <div className="h-11 w-11 shrink-0 rounded-2xl grid place-items-center shadow-soft border border-neutral-200/60 dark:border-neutral-800/60 bg-white/60 dark:bg-neutral-900/40 text-neutral-900 dark:text-neutral-100">
              <PanelsTopLeft size={18} />
            </div>
            <div className="min-w-0">
              <div className="font-semibold truncate text-base">TaskForge</div>
              <div className="hidden sm:block text-xs text-neutral-500 dark:text-neutral-400 truncate">{currentViewTitle}</div>
            </div>
          </Link>

          {access && (
            <div className="header-center-cluster hidden xl:flex min-w-0 flex-1 justify-center px-4">
              <div className="header-quick-row min-w-0">
                {isAdmin && (
                  <HeaderAction to="/admin/analytics" icon={BarChart2} label="Аналитика" active={isActive('/admin/analytics')} />
                )}
                <HeaderAction to={supportHref} icon={LifeBuoy} label="Поддержка" active={isActive(supportHref)} />
                <HeaderAction to="/settings" icon={Settings} label="Настройки" active={isActive('/settings')} />
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
                to="/profile"
                className="hidden xl:flex items-center gap-3 rounded-2xl border border-neutral-200/60 dark:border-neutral-800/60 bg-[rgb(var(--card))]/80 px-3 py-2 shadow-soft max-w-[20rem]"
                title="Профиль"
              >
                <div className="h-10 w-10 rounded-2xl overflow-hidden shrink-0 grid place-items-center bg-brand-600/15 text-brand-700 dark:text-brand-300 font-semibold">
                  {avatarUrl ? (
                    <img src={avatarUrl} alt={displayName} className="h-full w-full object-cover" />
                  ) : (
                    <span>{avatarFallback}</span>
                  )}
                </div>
                <div className="min-w-0">
                  <div className="text-sm font-semibold truncate">{displayName}</div>
                  <div className="text-xs text-neutral-500 dark:text-neutral-400 truncate">{displaySubline}</div>
                </div>
              </Link>
            )}

            {access ? (
              <div className="hidden md:block relative" ref={adminRef}>
                <button
                  className="btn-outline"
                  onClick={() => setAdminOpen((v) => !v)}
                  aria-haspopup="menu"
                  aria-expanded={adminOpen}
                  title="Быстрые действия"
                >
                  <MoreHorizontal size={18} />
                </button>
                {adminOpen && (
                  <div className="absolute right-0 mt-2 w-72 rounded-2xl border border-neutral-200/60 dark:border-neutral-800/60 bg-[rgb(var(--card))] shadow-soft p-1 z-50">
                    <Link to="/profile" className="btn-ghost w-full justify-start" onClick={() => setAdminOpen(false)}>
                      <User size={18} />
                      <span className="ml-2">Профиль</span>
                    </Link>
                    <Link to="/settings" className="btn-ghost w-full justify-start" onClick={() => setAdminOpen(false)}>
                      <Settings size={18} />
                      <span className="ml-2">Настройки</span>
                    </Link>
                    {canEdit && (
                      <button
                        type="button"
                        className="btn-ghost w-full justify-start"
                        onClick={() => {
                          toggle();
                          setAdminOpen(false);
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
                          <Link key={item.to} to={item.to} className="btn-ghost w-full justify-start" onClick={() => setAdminOpen(false)}>
                            <item.icon size={18} />
                            <span className="ml-2">{item.label}</span>
                          </Link>
                        ))}
                      </>
                    )}
                    <div className="my-1 h-px bg-neutral-200/70 dark:bg-neutral-800/70" />
                    <Link to={supportHref} className="btn-ghost w-full justify-start" onClick={() => setAdminOpen(false)}>
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
              <Link to="/login" className="hidden md:inline-flex btn-primary" title="Войти">
                <LogIn size={18} />
                <span>Войти</span>
              </Link>
            )}

            <div className="relative xl:hidden" ref={mobileMenuRef}>
              <button
                className="btn-outline !min-w-0 h-12 w-12 shrink-0 px-0 sm:h-auto sm:w-auto sm:px-3"
                onClick={() => setMobileOpen((v) => !v)}
                title="Меню"
                aria-haspopup="menu"
                aria-expanded={mobileOpen}
              >
                <Menu size={18} />
              </button>

              {mobileOpen && (
                <div className="absolute right-0 mt-2 w-[min(22rem,calc(100vw-1rem))] max-h-[75dvh] overflow-y-auto rounded-2xl border border-neutral-200/60 dark:border-neutral-800/60 bg-[rgb(var(--card))] shadow-soft p-1 z-50">
                  <div className="px-3 py-2 text-xs uppercase tracking-wide opacity-70">Навигация</div>
                  <div className="flex flex-col">
                    {primaryNav.map((item) => (
                      <Link key={item.to} to={item.to} className="btn-ghost w-full justify-start" onClick={() => setMobileOpen(false)}>
                        <item.icon size={18} />
                        <span className="ml-2">{item.label}</span>
                      </Link>
                    ))}

                    {access && (
                      <>
                        <div className="my-1 h-px bg-neutral-200/70 dark:bg-neutral-800/70" />
                        <div className="px-3 py-2 text-xs uppercase tracking-wide opacity-70">Аккаунт</div>

                        <Link to="/profile" className="btn-ghost w-full justify-start" onClick={() => setMobileOpen(false)}>
                          <User size={18} />
                          <span className="ml-2">Профиль</span>
                        </Link>
                        <Link to="/settings" className="btn-ghost w-full justify-start" onClick={() => setMobileOpen(false)}>
                          <Settings size={18} />
                          <span className="ml-2">Настройки</span>
                        </Link>
                        {canEdit && (
                          <button
                            type="button"
                            className="btn-ghost w-full justify-start"
                            onClick={() => {
                              toggle();
                              setMobileOpen(false);
                            }}
                          >
                            {isEditorMode ? <PencilLine size={18} /> : <Eye size={18} />}
                            <span className="ml-2">{isEditorMode ? 'Режим редактора' : 'Режим просмотра'}</span>
                          </button>
                        )}

                        {isAdmin && (
                          <>
                            <div className="my-1 h-px bg-neutral-200/70 dark:bg-neutral-800/70" />
                            <div className="px-3 py-2 text-xs uppercase tracking-wide opacity-70">Админка</div>
                            {adminNav.map((item) => (
                              <Link key={item.to} to={item.to} className="btn-ghost w-full justify-start" onClick={() => setMobileOpen(false)}>
                                <item.icon size={18} />
                                <span className="ml-2">{item.label}</span>
                              </Link>
                            ))}
                          </>
                        )}

                        <div className="my-1 h-px bg-neutral-200/70 dark:bg-neutral-800/70" />
                        <button
                          type="button"
                          className="btn-ghost w-full justify-start"
                          onClick={() => {
                            setMobileOpen(false);
                            handleLogout();
                          }}
                        >
                          <LogOut size={18} />
                          <span className="ml-2">Выйти</span>
                        </button>
                      </>
                    )}

                    {!access && (
                      <>
                        <div className="my-1 h-px bg-neutral-200/70 dark:bg-neutral-800/70" />
                        <Link to="/login" className="btn-ghost w-full justify-start" onClick={() => setMobileOpen(false)}>
                          <LogIn size={18} />
                          <span className="ml-2">Войти</span>
                        </Link>
                      </>
                    )}
                  </div>
                </div>
              )}
            </div>
          </div>
        </div>

      </header>

      <main className={mainWrapClass}>
        <div className={`items-start gap-4 2xl:gap-6 ${access ? 'xl:grid xl:grid-cols-[15rem,minmax(0,1fr)] 2xl:grid-cols-[15.5rem,minmax(0,1fr)]' : ''}`}>
          {access && (
            <aside className="dashboard-sticky-rail hidden xl:flex xl:flex-col gap-4 sticky top-24 self-start xl:max-h-[calc(100dvh-7rem)]">
              <div className="card p-3">
                <div className="side-nav-section-title">Основное</div>
                <div className="mt-2 space-y-1.5">
                  {primaryNav.map((item) => (
                    <SideNavLink key={item.to} {...item} compact />
                  ))}
                </div>
              </div>

              {isAdmin && (
                <div className="card p-3">
                  <div className="side-nav-section-title">Админка</div>
                  <div className="mt-2 space-y-1.5 max-h-[52vh] overflow-y-auto pr-1">
                    {adminPrimaryNav.map((item) => (
                      <SideNavLink key={item.to} {...item} compact />
                    ))}
                  </div>
                </div>
              )}
            </aside>
          )}

          <section className="min-w-0 overflow-x-hidden xl:px-1 2xl:px-2">
            <motion.div
              initial={{ opacity: 0, y: 6 }}
              animate={{ opacity: 1, y: 0 }}
              transition={{ duration: 0.25 }}
            >
              {children}
            </motion.div>
          </section>

        </div>
      </main>

      {!hideFooter && !access && (
        <footer className="mt-12 border-t border-neutral-200/70 dark:border-neutral-800/70 relative z-10">
          <div className="container-app py-4 sm:py-6 text-sm text-neutral-500 dark:text-neutral-400 flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
            <div>© {new Date().getFullYear()} TaskForge</div>
            {access && (
              <Link to={supportHref} className="inline-flex items-center gap-2 hover:text-neutral-700 dark:hover:text-neutral-200 transition" title="Техподдержка">
                <LifeBuoy size={16} className="opacity-70" />
                <span className="opacity-80">Техподдержка</span>
              </Link>
            )}
          </div>
        </footer>
      )}
      </div>
    </QuotaProvider>
  );
}
