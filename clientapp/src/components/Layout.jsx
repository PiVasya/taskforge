// clientapp/src/components/Layout.jsx
// Простая вёрстка для страниц приложения. Содержит шапку
// и футер. В футере отображается ссылка на страницу техподдержки.

import React from 'react';
import { Link } from 'react-router-dom';

export default function Layout({ children }) {
  return (
    <div className="min-h-screen flex flex-col">
      {/* Шапка */}
      <header className="border-b border-slate-200/70 dark:border-slate-800/70 bg-white dark:bg-slate-900 px-4 py-3">
        <div className="container-app flex items-center justify-between">
          <Link to="/" className="font-semibold text-lg">TaskForge</Link>
          <nav className="flex items-center gap-4">
            <Link to="/support" className="btn-outline">Поддержка</Link>
          </nav>
        </div>
      </header>
      {/* Контент */}
      <main className="container-app flex-1 px-4 py-6">{children}</main>
      {/* Футер */}
      <footer className="border-t border-slate-200/70 dark:border-slate-800/70 bg-white dark:bg-slate-900 px-4 py-3">
        <div className="container-app flex items-center justify-between text-sm text-slate-500 dark:text-slate-400">
          <div>© {new Date().getFullYear()} TaskForge</div>
          <Link to="/support" className="btn-outline">Мои обращения</Link>
        </div>
      </footer>
    </div>
  );
}