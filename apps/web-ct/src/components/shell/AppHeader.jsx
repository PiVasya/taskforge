import React, { useCallback, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { Eye, LogOut, PencilLine } from 'lucide-react';
import { useAuth } from '../../auth/AuthContext';
import { useEditorMode } from '../../contexts/EditorModeContext';

function AppHeader() {
  const { isAuthenticated, user, logout } = useAuth();
  const { canEdit, isEditorMode, setEditorMode } = useEditorMode();
  const navigate = useNavigate();
  const [logoutError, setLogoutError] = useState(null);
  const displayName = [user?.lastName, user?.firstName].filter(Boolean).join(' ')
    || user?.login
    || user?.email
    || 'Пользователь';

  const handleLogout = useCallback(async () => {
    setLogoutError(null);
    try {
      await logout();
      navigate('/login', { replace: true });
    } catch {
      setLogoutError('\u041d\u0435 \u0443\u0434\u0430\u043b\u043e\u0441\u044c \u0432\u044b\u0439\u0442\u0438. \u041f\u0440\u043e\u0432\u0435\u0440\u044c\u0442\u0435 \u0441\u043e\u0435\u0434\u0438\u043d\u0435\u043d\u0438\u0435 \u0438 \u043f\u043e\u0432\u0442\u043e\u0440\u0438\u0442\u0435 \u0432\u044b\u0445\u043e\u0434.');
    }
  }, [logout, navigate]);

  const handleToggleEditor = useCallback(() => {
    setEditorMode(!isEditorMode);
  }, [isEditorMode, setEditorMode]);

  return (
    <header className="sticky top-0 z-30 border-b border-neutral-200/80 bg-white/90 backdrop-blur dark:border-neutral-800/80 dark:bg-neutral-950/90">
      <div className="container-app flex items-center justify-between gap-3 py-3">
        <Link
          to="/"
          className="text-lg font-black tracking-tight text-brand-700 hover:text-brand-800 dark:text-brand-200 dark:hover:text-brand-100"
        >
          CT
        </Link>

        {logoutError && <p role="alert" className="text-sm text-red-600">{logoutError}</p>}
        <nav className="flex items-center gap-2 text-sm">
          {isAuthenticated && (
            <>
              {canEdit && (
                <button
                  type="button"
                  onClick={handleToggleEditor}
                  className={`inline-flex items-center gap-2 rounded-2xl px-3 py-2 font-semibold transition ${isEditorMode ? 'bg-brand-600 text-white hover:bg-brand-700' : 'hover:bg-neutral-100 dark:hover:bg-neutral-900'}`}
                >
                  {isEditorMode ? <Eye size={16} /> : <PencilLine size={16} />}
                  {isEditorMode ? 'Выйти из редактора' : 'Редактор'}
                </button>
              )}
              <span className="hidden max-w-[220px] truncate text-neutral-500 dark:text-neutral-400 md:inline">
                {displayName}
              </span>
              <button type="button" onClick={handleLogout} className="btn-outline inline-flex items-center gap-2">
                <LogOut size={16} /> Выйти
              </button>
            </>
          )}
        </nav>
      </div>
    </header>
  );
}

export default React.memo(AppHeader);
