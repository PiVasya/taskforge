// clientapp/src/components/Layout.jsx
//
// Компонент-шаблон для всей страницы. Содержит шапку с навигацией,
// переключатель темы, кнопку режима редактора и меню администраторов.
// В мобильной версии используется выпадающее меню «…», в которое также
// помещены ссылки на страницы и действия. На широких экранах админские
// ссылки прячутся за отдельной кнопкой с тремя точками.

import React, { useEffect, useRef, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import {
  Moon,
  Sun,
  BookOpen,
  PanelsTopLeft,
  LogOut,
  LogIn,
  PencilLine,
  Eye,
  User,
  BarChart2,
  ListOrdered,
  Palette,
  MoreHorizontal,
  Award,
  LifeBuoy,
} from 'lucide-react';
import { motion } from 'framer-motion';
import { useAuth } from '../auth/AuthContext';
import { useEditorMode } from '../contexts/EditorModeContext';

export default function Layout({ children }) {
  // темы: light | dark | pink
  const [theme, setTheme] = useState(() => localStorage.getItem('theme') || 'light');
  const isDark = theme === 'dark';

  const { access, logout } = useAuth();
  const { canEdit, isEditorMode, toggle } = useEditorMode();
  const nav = useNavigate();

  // цикл: light → dark → pink → light
  const cycleTheme = () =>
    setTheme((t) => (t === 'light' ? 'dark' : t === 'dark' ? 'pink' : 'light'));

  // применяем классы для темы и сохраняем в localStorage
  useEffect(() => {
    const cls = document.documentElement.classList;
    cls.remove('dark', 'pink');
    if (theme === 'dark') cls.add('dark');
    if (theme === 'pink') cls.add('pink');
    localStorage.setItem('theme', theme);
  }, [theme]);

  const handleLogout = async () => {
    await logout();
    nav('/login', { replace: true });
  };

  // состояние выпадающих меню
  const [moreOpen, setMoreOpen] = useState(false);
  const [adminOpen, setAdminOpen] = useState(false);
  const moreRef = useRef(null);
  const adminRef = useRef(null);

  // закрытие меню при клике вне или нажатию Esc
  useEffect(() => {
    const onDocClick = (e) => {
      if (moreRef.current && !moreRef.current.contains(e.target)) setMoreOpen(false);
      if (adminRef.current && !adminRef.current.contains(e.target)) setAdminOpen(false);
    };
    const onEsc = (e) => {
      if (e.key === 'Escape') {
        setMoreOpen(false);
        setAdminOpen(false);
      }
    };
    document.addEventListener('mousedown', onDocClick);
    document.addEventListener('keydown', onEsc);
    return () => {
      document.removeEventListener('mousedown', onDocClick);
      document.removeEventListener('keydown', onEsc);
    };
  }, []);

  return (
    <div className="min-h-screen">
      {/* фоновой градиент */}
      <div className="pointer-events-none fixed inset-0 -z-10 bg-gradient-to-b from-brand-600/10 via-transparent to-transparent blur-2xl" />
      <header className="sticky top-0 z-20 border-b border-slate-200/70 dark:border-slate-800/70 backdrop-blur bg-white/70 dark:bg-slate-900/60">
        <div className="container-app flex h-16 items-center justify-between gap-2">
          {/* Логотип и название */}
          <Link to="/courses" className="flex min-w-0 items-center gap-3">
            <div className="h-9 w-9 rounded-xl bg-brand-600 text-white grid place-items-center shadow-soft">
              <PanelsTopLeft size={18} />
            </div>
            <div className="font-semibold truncate">TaskForge</div>
            <span className="hidden xl:inline-flex items-center gap-2 text-sm text-slate-500 dark:text-slate-400 ml-2">
              <BookOpen size={16} /> Платформа задач
            </span>
          </Link>

          {/* Правая панель — крупные экраны */}
          <div className="hidden xl:flex items-center gap-2">
            {/* переключатель темы */}
            <button
              className="btn-outline"
              onClick={cycleTheme}
              aria-label="Toggle theme"
              title={`Тема: ${theme}`}
            >
              {isDark ? (
                <Sun size={18} />
              ) : theme === 'pink' ? (
                <Palette size={18} />
              ) : (
                <Moon size={18} />
              )}
              <span className="hidden sm:inline">Тема</span>
            </button>

            {/* режим редактора */}
            {canEdit && (
              <button
                className={`btn-outline ${isEditorMode ? 'border-brand-600/60' : ''}`}
                onClick={toggle}
                title="Режим редактора"
              >
                {isEditorMode ? <PencilLine size={18} /> : <Eye size={18} />}
                <span className="hidden sm:inline">{isEditorMode ? 'Редактор' : 'Просмотр'}</span>
              </button>
            )}

            {/* профиль */}
            {access && (
              <Link to="/profile" className="btn-outline" title="Профиль">
                <User size={18} />
                <span className="hidden sm:inline">Профиль</span>
              </Link>
            )}

            {/* мои решения */}
            {access && (
              <Link to="/my/solutions" className="btn-outline" title="Мои решения">
                <ListOrdered size={18} />
                <span className="hidden sm:inline">Мои решения</span>
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
            {access && canEdit && (
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
                    className="absolute right-0 mt-2 w-56 rounded-2xl border border-slate-200/60 dark:border-slate-800/60 bg-[rgb(var(--card))] shadow-soft p-1 z-50"
                  >
                    <Link
                      role="menuitem"
                      to="/admin/solutions"
                      className="btn-ghost w-full justify-start"
                      onClick={() => setAdminOpen(false)}
                      title="Решения студентов"
                    >
                      <ListOrdered size={18} />
                      <span className="ml-2">Решения студентов</span>
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

          {/* Мобильное меню — одна кнопка "..." */}
          <div className="relative xl:hidden" ref={moreRef}>
            <button
              className="btn-outline"
              aria-haspopup="menu"
              aria-expanded={moreOpen}
              aria-label="Ещё действия"
              title="Ещё"
              onClick={() => setMoreOpen((v) => !v)}
            >
              <MoreHorizontal size={18} />
            </button>
            {moreOpen && (
              <div
                role="menu"
                className="absolute right-0 mt-2 w-56 rounded-2xl border border-slate-200/60 dark:border-slate-800/60 bg-[rgb(var(--card))] shadow-soft p-1 z-50"
              >
                {/* Тема */}
                <button
                  role="menuitem"
                  className="btn-ghost w-full justify-start"
                  onClick={() => {
                    setMoreOpen(false);
                    cycleTheme();
                  }}
                >
                  {isDark ? (
                    <Sun size={18} />
                  ) : theme === 'pink' ? (
                    <Palette size={18} />
                  ) : (
                    <Moon size={18} />
                  )}
                  <span>Тема: {theme === 'pink' ? 'Rose' : isDark ? 'Dark' : 'Light'}</span>
                </button>
                {/* Режим редактора */}
                {canEdit && (
                  <button
                    role="menuitem"
                    className="btn-ghost w-full justify-start"
                    onClick={() => {
                      setMoreOpen(false);
                      toggle();
                    }}
                  >
                    {isEditorMode ? <PencilLine size={18} /> : <Eye size={18} />}
                    <span>{isEditorMode ? 'Редактор' : 'Просмотр'}</span>
                  </button>
                )}
                {/* Профиль */}
                {access && (
                  <Link
                    role="menuitem"
                    to="/profile"
                    className="btn-ghost w-full justify-start"
                    onClick={() => setMoreOpen(false)}
                    title="Профиль"
                  >
                    <User size={18} />
                    <span>Профиль</span>
                  </Link>
                )}
                {/* Мои решения */}
                {access && (
                  <Link
                    role="menuitem"
                    to="/my/solutions"
                    className="btn-ghost w-full justify-start"
                    onClick={() => setMoreOpen(false)}
                    title="Мои решения"
                  >
                    <ListOrdered size={18} />
                    <span>Мои решения</span>
                  </Link>
                )}
                {/* Топ */}
                {access && (
                  <Link
                    role="menuitem"
                    to="/leaderboard"
                    className="btn-ghost w-full justify-start"
                    onClick={() => setMoreOpen(false)}
                    title="Топ студентов"
                  >
                    <BarChart2 size={18} />
                    <span>Топ</span>
                  </Link>
                )}
                {/* админка: решения и бейджи */}
                {access && canEdit && (
                  <>
                    <Link
                      role="menuitem"
                      to="/admin/solutions"
                      className="btn-ghost w-full justify-start"
                      onClick={() => setMoreOpen(false)}
                      title="Решения студентов"
                    >
                      <ListOrdered size={18} />
                      <span>Решения</span>
                    </Link>
                    <Link
                      role="menuitem"
                      to="/admin/badges"
                      className="btn-ghost w-full justify-start"
                      onClick={() => setMoreOpen(false)}
                      title="Бейджи"
                    >
                      <Award size={18} />
                      <span>Бейджи</span>
                    </Link>
                    <Link
                      role="menuitem"
                      to="/admin/support"
                      className="btn-ghost w-full justify-start"
                      onClick={() => setMoreOpen(false)}
                      title="Обращения пользователей"
                    >
                      <LifeBuoy size={18} />
                      <span>Обращения</span>
                    </Link>
                  </>
                )}
                {/* вход/выход */}
                {access ? (
                  <button
                    role="menuitem"
                    className="btn-ghost w-full justify-start"
                    onClick={() => {
                      setMoreOpen(false);
                      handleLogout();
                    }}
                    title="Выйти"
                  >
                    <LogOut size={18} />
                    <span>Выйти</span>
                  </button>
                ) : (
                  <Link
                    role="menuitem"
                    to="/login"
                    className="btn-ghost w-full justify-start"
                    onClick={() => setMoreOpen(false)}
                    title="Войти"
                  >
                    <LogIn size={18} />
                    <span>Войти</span>
                  </Link>
                )}
              </div>
            )}
          </div>
        </div>
      </header>

      <main className="container-app py-8">
        <motion.div
          initial={{ opacity: 0, y: 6 }}
          animate={{ opacity: 1, y: 0 }}
          transition={{ duration: 0.25 }}
        >
          {children}
        </motion.div>
      </main>

      <footer className="mt-12 border-t border-slate-200/70 dark:border-slate-800/70">
        <div className="container-app py-6 text-sm text-slate-500 dark:text-slate-400">
          © {new Date().getFullYear()} TaskForge
        </div>
      </footer>

      {/* Кнопка техподдержки (внизу экрана) */}
      {access && (
        <Link
          to="/support"
          className="fixed bottom-6 right-6 btn-primary shadow-soft z-40"
          title="Техподдержка"
        >
          <LifeBuoy size={18} />
          <span className="ml-2 hidden sm:inline">Техподдержка</span>
        </Link>
      )}
    </div>
  );
}