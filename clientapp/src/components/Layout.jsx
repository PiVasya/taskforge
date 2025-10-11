import React, { useEffect, useMemo, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { Moon, Sun, BookOpen, PanelsTopLeft, LogOut, LogIn, PencilLine, Eye, Trophy, ListChecks } from 'lucide-react';
import { motion } from 'framer-motion';
import { useAuth } from '../auth/AuthContext';
import { useEditorMode } from '../contexts/EditorModeContext';

export default function Layout({ children }) {
  const [dark, setDark] = useState(() => localStorage.getItem('theme') === 'dark');
  const { access, logout } = useAuth();
  const { canEdit, isEditorMode, toggle } = useEditorMode();
  const nav = useNavigate();

  useEffect(() => {
    const cls = document.documentElement.classList;
    dark ? cls.add('dark') : cls.remove('dark');
    localStorage.setItem('theme', dark ? 'dark' : 'light');
  }, [dark]);

  const handleLogout = async () => {
    await logout();
    nav('/login', { replace: true });
  };

  // Роль Admin из JWT
  const isAdmin = useMemo(() => {
    if (!access) return false;
    try {
      const base64 = access.split('.')[1]?.replace(/-/g, '+').replace(/_/g, '/');
      if (!base64) return false;
      const payload = JSON.parse(atob(base64));
      const raw =
        payload?.role ??
        payload?.Role ??
        payload?.roles ??
        payload?.['http://schemas.microsoft.com/ws/2008/06/identity/claims/role'];
      if (Array.isArray(raw)) return raw.includes('Admin');
      if (typeof raw === 'string') return raw === 'Admin';
      return false;
    } catch {
      return false;
    }
  }, [access]);

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
            {/* Кнопки админа */}
            {isAdmin && (
              <>
                <Link to="/admin/solutions" className="btn-outline" title="Решения студентов">
                  <ListChecks size={18} />
                  <span className="hidden sm:inline">Решения</span>
                </Link>
                <Link to="/admin/leaderboard" className="btn-outline" title="Топ">
                  <Trophy size={18} />
                  <span className="hidden sm:inline">Топ</span>
                </Link>
              </>
            )}

            {/* Тема */}
            <button className="btn-ghost" onClick={() => setDark(v => !v)} aria-label="Toggle theme">
              {dark ? <Sun size={18} /> : <Moon size={18} />}
              <span className="hidden sm:inline">Тема</span>
            </button>

            {/* Режим редактора (для Admin) */}
            {canEdit && (
              <button
                className={`btn-outline ${isEditorMode ? 'border-brand-600/60' : ''}`}
                onClick={toggle}
                title="Режим редактора"
              >
                {isEditorMode ? <PencilLine size={18} /> : <Eye size={18} />}
                <span className="hidden sm:inline">
                  {isEditorMode ? 'Режим: редактор' : 'Режим: просмотр'}
                </span>
              </button>
            )}

            {/* Вход/выход */}
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
