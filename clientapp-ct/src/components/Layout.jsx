import React from 'react';
import { Link, useLocation, useNavigate } from 'react-router-dom';
import { BookOpen, ClipboardList, Eye, LogOut, LogIn, PencilLine, Settings, UserPlus } from 'lucide-react';
import { useAuth } from '../auth/AuthContext';
import { useEditorMode } from '../contexts/EditorModeContext';

export default function Layout({ children, fullWidth = false }) {
  const { access, user, logout } = useAuth();
  const { canEdit, isEditorMode, toggle } = useEditorMode();
  const navigate = useNavigate();
  const location = useLocation();
  const displayName = [user?.lastName, user?.firstName].filter(Boolean).join(' ') || user?.email || 'Пользователь';
  const handleLogout = async () => { await logout(); navigate('/login', { replace: true }); };
  const handleToggleEditor = () => {
    const next = !isEditorMode;
    toggle();
    if (next) {
      if (location.pathname.startsWith('/courses/')) {
        const slug = location.pathname.split('/').filter(Boolean)[1];
        navigate(slug ? `/editor/courses/${slug}` : '/editor');
      } else navigate('/editor');
    } else if (location.pathname.startsWith('/editor/courses/')) {
      const slug = location.pathname.split('/').filter(Boolean)[2];
      navigate(slug ? `/courses/${slug}` : '/');
    } else if (location.pathname.startsWith('/editor')) navigate('/');
  };
  return (
    <div className="min-h-screen bg-neutral-50 dark:bg-neutral-950 text-neutral-900 dark:text-neutral-100">
      <header className="sticky top-0 z-30 border-b border-neutral-200/80 dark:border-neutral-800/80 bg-white/90 dark:bg-neutral-950/90 backdrop-blur">
        <div className="container-app py-3 flex items-center justify-between gap-4">
          <Link to={isEditorMode ? '/editor' : '/'} className="font-semibold text-lg tracking-tight text-brand-600 hover:text-brand-700">TaskForge CT</Link>
          <nav className="flex items-center gap-3 text-sm">
            {access ? (<>
              <Link to={isEditorMode ? '/editor' : '/'} className="hidden md:inline-flex items-center gap-2 rounded-2xl px-3 py-2 hover:bg-neutral-100 dark:hover:bg-neutral-900"><BookOpen size={16} />{isEditorMode ? 'Дерево редактора' : 'Курсы'}</Link>
              {!isEditorMode && <Link to="/tasks" className="hidden md:inline-flex items-center gap-2 rounded-2xl px-3 py-2 hover:bg-neutral-100 dark:hover:bg-neutral-900"><ClipboardList size={16} />Все задания</Link>}
              {canEdit && <button type="button" onClick={handleToggleEditor} className={`hidden lg:inline-flex items-center gap-2 rounded-2xl px-3 py-2 ${isEditorMode ? 'bg-brand-600 text-white hover:bg-brand-700' : 'hover:bg-neutral-100 dark:hover:bg-neutral-900'}`}>{isEditorMode ? <Eye size={16} /> : <PencilLine size={16} />}{isEditorMode ? 'Просмотр' : 'Редактор'}</button>}
              {canEdit && isEditorMode && <Link to="/editor" className="hidden lg:inline-flex items-center gap-2 rounded-2xl px-3 py-2 hover:bg-neutral-100 dark:hover:bg-neutral-900"><Settings size={16} />Управление</Link>}
              <span className="hidden sm:inline text-neutral-500 dark:text-neutral-400">{displayName}</span>
              <button type="button" onClick={handleLogout} className="btn-outline inline-flex items-center gap-2"><LogOut size={16} />Выйти</button>
            </>) : (<>
              <Link to="/login" className="btn-outline inline-flex items-center gap-2"><LogIn size={16} />Вход</Link>
              <Link to="/register" className="btn-primary inline-flex items-center gap-2"><UserPlus size={16} />Регистрация</Link>
            </>)}
          </nav>
        </div>
      </header>
      <main className={fullWidth ? '' : 'container-app py-8'}>{children}</main>
    </div>
  );
}
