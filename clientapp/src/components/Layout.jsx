// clientapp/src/components/Layout.jsx
//
// Компонент-шаблон для всей страницы. Содержит шапку с навигацией,
// переключатель темы, кнопку режима редактора и меню администраторов.
// В мобильной версии используется выпадающее меню «…», в которое также
// помещены ссылки на страницы и действия. На широких экранах админские
// ссылки прячутся за отдельной кнопкой с тремя точками.

import React, { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { Link, useNavigate, useLocation } from 'react-router-dom';
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
  LifeBuoy,
  Newspaper,
	  Menu,
} from 'lucide-react';
import { motion } from 'framer-motion';
import { useAuth } from '../auth/AuthContext';
import { useEditorMode } from '../contexts/EditorModeContext';
import BgFxCanvas from './bgfx/BgFxCanvas';

export default function Layout({ children, fullWidth = false }) {
  // Вся тема завязана на классах у <html>: html.dark и html.(blue|pink|apple).
  // Если классов нет — CSS-переменные (например --page-bg) не задаются, и фон выглядит белым.
  const applyHtmlThemeClasses = (nextMode, nextColorTheme) => {
    const root = document.documentElement;
    const palettes = ['blue', 'pink', 'apple', 'red', 'honey'];
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

  // Темы = цвет (blue|pink|apple) + режим (light|dark)
  const initialUi = readUiSettings();
  const [colorTheme, setColorTheme] = useState(() => initialUi?.colorTheme || localStorage.getItem('colorTheme') || 'pink');
  const [mode, setMode] = useState(() => initialUi?.mode || localStorage.getItem('mode') || 'dark');
  const isDark = mode === 'dark';

  // Ревизия UI-настроек. Нужна, чтобы фоновые эффекты (Canvas) могли
  // перечитать CSS-переменные даже если тема «не изменилась» по значениям,
  // но классы у <html> применились позже (часто после auto-refresh токена).
  const [uiRev, setUiRev] = useState(0);

  // Применяем классы темы ДО первой отрисовки, чтобы не было "белого" фона
  // и чтобы Canvas-эффекты могли сразу прочитать правильные CSS-переменные.
  useLayoutEffect(() => {
    applyHtmlThemeClasses(mode, colorTheme);
  }, [mode, colorTheme]);
  const [bgFx, setBgFx] = useState(() => (typeof initialUi?.bgFx === 'boolean' ? initialUi.bgFx : localStorage.getItem('bgFx') === '1'));
  const [fxMode, setFxMode] = useState(() => initialUi?.fxMode || localStorage.getItem('fxMode') || 'random');
  // Вариант фоновых эффектов (0..4). Читаем из localStorage, а не sessionStorage.
  const [fxVariant, setFxVariant] = useState(() => {
    if (initialUi?.fxVariant != null) return String(initialUi.fxVariant);
    const stored = localStorage.getItem('fxVariant');
    return stored != null ? stored : '2';
  });

  // Тема/палитра меняются на странице «Настройки». Чтобы Layout реагировал без перезагрузки,
  // слушаем кастомное событие (в том же табе) и storage-события (между табами).
  useEffect(() => {
    const applyFromStorage = () => {
      const nextColor = localStorage.getItem('colorTheme') || 'pink';
      const nextMode = localStorage.getItem('mode') || 'dark';

      // Применяем сразу, чтобы не ждать перерендера.
      applyHtmlThemeClasses(nextMode, nextColor);
      setColorTheme(nextColor);
      setMode(nextMode);
      const ui = readUiSettings();
      setBgFx(localStorage.getItem('bgFx') === '1');
      setFxMode(ui?.fxMode || localStorage.getItem('fxMode') || 'random');
      setFxVariant(String(ui?.fxVariant ?? localStorage.getItem('fxVariant') ?? '2'));

      // даже если значения не поменялись, просим Canvas перечитать CSS vars
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

    // первичное применение при первом рендере
    applyFromStorage();
    return () => {
      window.removeEventListener('storage', onStorage);
      window.removeEventListener('tf-ui-settings-changed', onLocalUiChanged);
    };
  }, []);

  const { access, logout } = useAuth();
  const { canEdit, isEditorMode, toggle, isAdmin } = useEditorMode();
  const nav = useNavigate();
  const location = useLocation();

  // ===== Квоты (5 отправок решений и 5 загрузок топа) =====
  // применяем классы для темы и сохраняем в localStorage

  // ===== Автосворачивание навигации в "..." при переполнении =====
  const headerRowRef = useRef(null);
  const [forceCompact, setForceCompact] = useState(false);

  // мобильное меню (гамбургер)
  const [mobileOpen, setMobileOpen] = useState(false);
  const mobileMenuRef = useRef(null);

  // admin (three-dots) menu
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

  // закрытие мобильного меню по клику вне
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

  // закрывать меню при переходах
  useEffect(() => {
    setMobileOpen(false);
    setAdminOpen(false);
  }, [location.pathname]);

  useLayoutEffect(() => {
    const el = headerRowRef.current;
    if (!el) return;

    const check = () => {
      try {
        // если строка не помещается — включаем компактный режим
        const overflow = el.scrollWidth > el.clientWidth + 4;
        setForceCompact(overflow);
      } catch {
        // ignore
      }
    };

    check();
    const ro = new ResizeObserver(() => check());
    ro.observe(el);
    return () => {
      try { ro.disconnect(); } catch {}
    };
  }, [access, isAdmin, canEdit, isEditorMode, mode, colorTheme]);

  return (
    // isolate + z-слои: чтобы фиксированный фон не "проваливался" под body (иначе эффекты не видны)
    <div className="min-h-screen relative isolate">
      {/* фон: (по переключателю) мягкий градиент + размытые блики */}
      <div className="pointer-events-none fixed inset-0 z-0 overflow-hidden">
        {bgFx && (
          <>
            <div className="absolute inset-0 bg-gradient-to-b from-brand-600/12 via-transparent to-transparent blur-2xl" />
            <div className="absolute inset-0">
            {/* Canvas-эффекты (туман / пыль+кометы / нейросвязи / аврора / сердечки) */}
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
        <div ref={headerRowRef} className="container-app flex h-16 items-center justify-between gap-2">
          {/* Логотип и название */}
          <Link to="/news" className="flex min-w-0 items-center gap-3">
            <div className="h-9 w-9 rounded-xl grid place-items-center shadow-soft border border-neutral-200/60 dark:border-neutral-800/60 bg-white/60 dark:bg-neutral-900/40 text-neutral-900 dark:text-neutral-100">
              <PanelsTopLeft size={18} />
            </div>
            <div className="font-semibold truncate">TaskForge</div>
            {/* подпись "Платформа задач" убрали — она съедает место и ломает хедер */}
          </Link>

          {/* Правая панель — крупные экраны */}
          <div className={`hidden xl:flex items-center gap-2 ${forceCompact ? 'xl:hidden' : ''}`}
          >
            {/* Быстрые переключатели темы/палитры убраны из хедера — оставлены только на странице настроек */}

            {/* новости */}
            {access && (
              <Link to="/news" className="btn-outline" title="Новости">
                <Newspaper size={18} />
                <span className="hidden 2xl:inline">Новости</span>
              </Link>
            )}

            {/* курсы */}
            {access && (
              <Link to="/courses" className="btn-outline" title="Курсы">
                <PanelsTopLeft size={18} />
                <span className="hidden 2xl:inline">Курсы</span>
              </Link>
            )}

            {/* режим редактора */}
            {canEdit && (
              <button
                className={`btn-outline ${isEditorMode ? 'border-brand-600/60' : ''}`}
                onClick={toggle}
                title="Режим редактора"
              >
                {isEditorMode ? <PencilLine size={18} /> : <Eye size={18} />}
                <span className="hidden 2xl:inline">{isEditorMode ? 'Редактор' : 'Просмотр'}</span>
              </button>
            )}

            {/* профиль удалён */}

            {/* настройки */}
            {access && (
              <Link to="/settings" className="btn-outline" title="Настройки">
                <Settings size={18} />
                <span className="hidden 2xl:inline">Настройки</span>
              </Link>
            )}

            {/* мои решения */}
            {access && (
              <Link to="/my/solutions" className="btn-outline" title="Мои решения">
                <ListOrdered size={18} />
                <span className="hidden 2xl:inline">Мои решения</span>
              </Link>
            )}

            {/* топ */}
            {access && (
              <Link to="/leaderboard" className="btn-outline" title="Топ студентов">
                <BarChart2 size={18} />
                <span className="hidden sm:inline">Топ</span>
              </Link>
            )}

            {/* админские действия — отдельное меню на три точки */}
            {access && isAdmin && (
              <div className="relative" ref={adminRef}>
                <button
                  className="btn-outline"
                  onClick={() => setAdminOpen((v) => !v)}
                  aria-haspopup="menu"
                  aria-expanded={adminOpen}
                  title="Админские действия"
                >
                  <MoreHorizontal size={18} />
                </button>
                {adminOpen && (
                  <div
                    role="menu"
                    className="absolute right-0 mt-2 w-56 rounded-2xl border border-neutral-200/60 dark:border-neutral-800/60 bg-[rgb(var(--card))] shadow-soft p-1 z-50"
                  >
                    <Link
                      role="menuitem"
                      to="/admin/solutions"
                      className="btn-ghost w-full justify-start"
                      onClick={() => setAdminOpen(false)}
                      title="Управление пользователями"
                    >
                      <ListOrdered size={18} />
                      <span className="ml-2">Управление пользователями</span>
                    </Link>
                    <Link
                      role="menuitem"
                      to="/admin/badges"
                      className="btn-ghost w-full justify-start"
                      onClick={() => setAdminOpen(false)}
                      title="Бейджи"
                    >
                      <Award size={18} />
                      <span className="ml-2">Бейджи</span>
                    </Link>

                    <Link
                      role="menuitem"
                      to="/admin/groups"
                      className="btn-ghost w-full justify-start"
                      onClick={() => setAdminOpen(false)}
                      title="Группы пользователей"
                    >
                      <Users size={18} />
                      <span className="ml-2">Группы</span>
                    </Link>
                    <Link
                      role="menuitem"
                      to="/admin/support"
                      className="btn-ghost w-full justify-start"
                      onClick={() => setAdminOpen(false)}
                      title="Обращения пользователей"
                    >
                      <LifeBuoy size={18} />
                      <span className="ml-2">Обращения</span>
                    </Link>
                  </div>
                )}
              </div>
            )}

            {/* вход/выход */}
            {access ? (
              <button className="btn-outline" onClick={handleLogout} title="Выйти">
                <LogOut size={18} />
                <span className="hidden sm:inline">Выйти</span>
              </button>
            ) : (
              <Link to="/login" className="btn-primary" title="Войти">
                <LogIn size={18} />
                <span className="hidden sm:inline">Войти</span>
              </Link>
            )}
          </div>

          {/* Мобильное меню (гамбургер) — реальные кнопки/ссылки, совпадающие с десктопом */}
          <div className="relative xl:hidden" ref={mobileMenuRef}>
            <button
              className="btn-outline"
              onClick={() => setMobileOpen((v) => !v)}
              title="Меню"
              aria-haspopup="menu"
              aria-expanded={mobileOpen}
            >
              <Menu size={18} />
            </button>

            {mobileOpen && (
              <div className="absolute right-0 mt-2 w-72 rounded-2xl border border-neutral-200/60 dark:border-neutral-800/60 bg-[rgb(var(--card))] shadow-soft p-1 z-50">
                <div className="flex flex-col">
                  <Link
                    to="/courses"
                    className="btn-ghost w-full justify-start"
                    onClick={() => setMobileOpen(false)}
                  >
                    <PanelsTopLeft size={18} />
                    <span className="ml-2">Курсы</span>
                  </Link>

                  {access && (
                    <>
                      {canEdit && (
                        <button
                          type="button"
                          className="btn-ghost w-full justify-start"
                          onClick={() => {
                            toggle();
                            setMobileOpen(false);
                          }}
                          title="Режим редактора"
                        >
                          {isEditorMode ? <PencilLine size={18} /> : <Eye size={18} />}
                          <span className="ml-2">{isEditorMode ? 'Редактор' : 'Просмотр'}</span>
                        </button>
                      )}

                      <Link
                        to="/settings"
                        className="btn-ghost w-full justify-start"
                        onClick={() => setMobileOpen(false)}
                      >
                        <Settings size={18} />
                        <span className="ml-2">Настройки</span>
                      </Link>

                      <Link
                        to="/my/solutions"
                        className="btn-ghost w-full justify-start"
                        onClick={() => setMobileOpen(false)}
                      >
                        <ListOrdered size={18} />
                        <span className="ml-2">Мои решения</span>
                      </Link>

                      <Link
                        to="/leaderboard"
                        className="btn-ghost w-full justify-start"
                        onClick={() => setMobileOpen(false)}
                      >
                        <BarChart2 size={18} />
                        <span className="ml-2">Топ</span>
                      </Link>

                      {isAdmin && (
                        <>
                          <div className="my-1 h-px bg-neutral-200/70 dark:bg-neutral-800/70" />
                          <div className="px-3 py-2 text-xs uppercase tracking-wide opacity-70">Админка</div>

                          <Link
                            to="/admin/solutions"
                            className="btn-ghost w-full justify-start"
                            onClick={() => setMobileOpen(false)}
                          >
                            <ListOrdered size={18} />
                            <span className="ml-2">Управление пользователями</span>
                          </Link>
                          <Link
                            to="/admin/badges"
                            className="btn-ghost w-full justify-start"
                            onClick={() => setMobileOpen(false)}
                          >
                            <Award size={18} />
                            <span className="ml-2">Бейджи</span>
                          </Link>
                          <Link
                            to="/admin/groups"
                            className="btn-ghost w-full justify-start"
                            onClick={() => setMobileOpen(false)}
                          >
                            <Users size={18} />
                            <span className="ml-2">Группы</span>
                          </Link>
                          <Link
                            to="/admin/support"
                            className="btn-ghost w-full justify-start"
                            onClick={() => setMobileOpen(false)}
                          >
                            <LifeBuoy size={18} />
                            <span className="ml-2">Техподдержка</span>
                          </Link>
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
                      <Link
                        to="/login"
                        className="btn-ghost w-full justify-start"
                        onClick={() => setMobileOpen(false)}
                      >
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
      </header>

      <main
        className={
          fullWidth
            ? "w-full max-w-none px-4 sm:px-6 lg:px-8 py-8 relative z-10"
            : "container-app py-8 relative z-10"
        }
      >
        <motion.div
          initial={{ opacity: 0, y: 6 }}
          animate={{ opacity: 1, y: 0 }}
          transition={{ duration: 0.25 }}
        >
          {children}
        </motion.div>
      </main>

      <footer className="mt-12 border-t border-neutral-200/70 dark:border-neutral-800/70 relative z-10">
        <div className="container-app py-6 text-sm text-neutral-500 dark:text-neutral-400 flex items-center justify-between">
          {/* слева */}
          <div>© {new Date().getFullYear()} TaskForge</div>

          {/* справа */}
          {access && (
            <Link
              to={isAdmin ? "/admin/support" : "/support"}
              className="inline-flex items-center gap-2 hover:text-neutral-700 dark:hover:text-neutral-200 transition"
              title="Техподдержка"
            >
              <LifeBuoy size={16} className="opacity-70" />
              <span className="opacity-80">Техподдержка</span>
            </Link>
          )}
        </div>
      </footer>
    </div>
  );
}