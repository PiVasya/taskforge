// clientapp/src/components/Layout.jsx
import React, { useEffect, useState } from 'react';
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
  Palette, // иконка розовой темы
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
  const cycleTheme = () => setTheme((t) => (t === 'light' ? 'dark' : t === 'dark' ? 'pink' : 'light'));

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

  return (
    <div className="min-h-screen">
      <div className="pointer-events-none fixed inset-0 -z-10 bg-gradient-to-b from-brand-600/10 via-transparent to-transparent blur-2xl" />
      <header className="sticky top-0 z-20 border-b border-slate-200/70 dark:border-slate-800/70 backdrop-blur bg-white/70 dark:bg-slate-900/60">
        <div className="container-app flex h-16 items-center justify-between">
          <Link to="/courses" className="flex items-center gap-3">
            <div className="h-9 w-9 rounded-xl bg-brand-600 text-white grid place-items-center shadow-soft">
              <PanelsTopLeft size={18} />
            </div>
            <div className="font-semibold">TaskForge</div>
            <span className="hidden sm:inline-flex items-center gap-2 text-sm text-slate-500 dark:text-slate-400 ml-2">
              <BookOpen size={16} /> Платформа задач
            </span>
          </Link>

          <div className="flex items-center gap-2">
            {/* переключатель темы */}
            <button
              className="btn-outline"
              onClick={cycleTheme}
              aria-label="Toggle theme"
              title={`Тема: ${theme}`}
            >
              {isDark ? <Sun size={18} /> : theme === 'pink' ? <Palette size={18} /> : <Moon size={18} />}
              <span className="hidden sm:inline">Тема</span>
            </button>

            {/* переключатель режима редактора — показываем только если аккаунт умеет редактировать */}
            {canEdit && (
              <button
                className={`btn-outline ${isEditorMode ? 'border-brand-600/60' : ''}`}
                onClick={toggle}
                title="Режим редактора"
              >
                {isEditorMode ? <PencilLine size={18} /> : <Eye size={18} />}
                <span className="hidden sm:inline">
                  {isEditorMode ? 'Редактор' : 'Просмотр'}
                </span>
              </button>
            )}

            {/* ссылка на профиль для авторизованных пользователей */}
            {access && (
              <Link to="/profile" className="btn-outline" title="Профиль">
                <User size={18} />
                <span className="hidden sm:inline">Профиль</span>
              </Link>
            )}

            {/* ссылка на общий рейтинг — доступна всем авторизованным пользователям */}
            {access && (
              <Link to="/leaderboard" className="btn-outline" title="Топ студентов">
                <BarChart2 size={18} />
                <span className="hidden sm:inline">Топ</span>
              </Link>
            )}

            {/* кнопка "Решения" доступна только пользователям с правами редактирования */}
            {access && canEdit && (
              <Link to="/admin/solutions" className="btn-outline" title="Решения студентов">
                <ListOrdered size={18} />
                <span className="hidden sm:inline">Решения</span>
              </Link>
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
        </div>
      </header>

      <main className="container-app py-8">
        <motion.div initial={{ opacity: 0, y: 6 }} animate={{ opacity: 1, y: 0 }} transition={{ duration: 0.25 }}>
          {children}
        </motion.div>
      </main>

      <footer className="mt-12 border-t border-slate-200/70 dark:border-slate-800/70">
        <div className="container-app py-6 text-sm text-slate-500 dark:text-slate-400">
          © {new Date().getFullYear()} TaskForge
        </div>
      </footer>
    </div>
  );
}
