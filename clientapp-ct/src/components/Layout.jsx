import React from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { ClipboardList, GraduationCap, LogOut, LogIn, Settings, UserPlus } from 'lucide-react';
import { useAuth } from '../auth/AuthContext';

export default function Layout({ children, fullWidth = false }) {
  const { access, user, logout } = useAuth();
  const navigate = useNavigate();

  const displayName = [user?.lastName, user?.firstName].filter(Boolean).join(' ') || user?.email || 'Пользователь';

  const handleLogout = async () => {
    await logout();
    navigate('/login', { replace: true });
  };

  return (
    <div className="min-h-screen bg-neutral-50 dark:bg-neutral-950 text-neutral-900 dark:text-neutral-100">
      <header className="sticky top-0 z-30 border-b border-neutral-200/80 dark:border-neutral-800/80 bg-white/90 dark:bg-neutral-950/90 backdrop-blur">
        <div className="container-app py-3 flex items-center justify-between gap-4">
          <Link to="/" className="font-semibold text-lg tracking-tight text-brand-600 hover:text-brand-700">
            TaskForge CT
          </Link>

          <nav className="flex items-center gap-3 text-sm">
            {access ? (
              <>
                <Link to="/" className="hidden md:inline-flex items-center gap-2 rounded-2xl px-3 py-2 hover:bg-neutral-100 dark:hover:bg-neutral-900">
                  <GraduationCap size={16} />
                  Курсы
                </Link>
                <Link to="/tasks" className="hidden md:inline-flex items-center gap-2 rounded-2xl px-3 py-2 hover:bg-neutral-100 dark:hover:bg-neutral-900">
                  <ClipboardList size={16} />
                  Задания
                </Link>
                <Link to="/admin/conspects" className="hidden lg:inline-flex items-center gap-2 rounded-2xl px-3 py-2 hover:bg-neutral-100 dark:hover:bg-neutral-900">
                  <Settings size={16} />
                  Редактор
                </Link>
                <span className="hidden sm:inline text-neutral-500 dark:text-neutral-400">{displayName}</span>
                <button type="button" onClick={handleLogout} className="btn-outline inline-flex items-center gap-2">
                  <LogOut size={16} />
                  Выйти
                </button>
              </>
            ) : (
              <>
                <Link to="/login" className="btn-outline inline-flex items-center gap-2">
                  <LogIn size={16} />
                  Вход
                </Link>
                <Link to="/register" className="btn-primary inline-flex items-center gap-2">
                  <UserPlus size={16} />
                  Регистрация
                </Link>
              </>
            )}
          </nav>
        </div>
      </header>

      <main className={fullWidth ? '' : 'container-app py-8'}>{children}</main>
    </div>
  );
}
