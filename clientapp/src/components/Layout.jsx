// clientapp/src/components/Layout.jsx
//
// Компонент-шаблон для всей страницы. Содержит шапку с навигацией,
// переключатель темы, кнопку режима редактора и меню администраторов.
// В мобильной версии используется выпадающее меню «…», в которое также
// помещены ссылки на страницы и действия. На широких экранах админские
// ссылки прячутся за отдельной кнопкой с тремя точками.

import React, { useEffect, useLayoutEffect, useRef, useState } from 'react';
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
  Users,
  BarChart2,
  ListOrdered,
  Palette,
  MoreHorizontal,
  Sparkles,
  Award,
  LifeBuoy,
  ChevronDown,
} from 'lucide-react';
import { motion } from 'framer-motion';
import { useAuth } from '../auth/AuthContext';
import { useEditorMode } from '../contexts/EditorModeContext';
import { getMyQuotas } from '../api/quotas';

export default function Layout({ children, fullWidth = false }) {
  // Темы = цвет (blue|pink|apple) + режим (light|dark)
  const [colorTheme, setColorTheme] = useState(() => localStorage.getItem('colorTheme') || 'blue');
  const [mode, setMode] = useState(() => localStorage.getItem('mode') || 'light');
  const isDark = mode === 'dark';
  const [bgFx, setBgFx] = useState(() => localStorage.getItem('bgFx') === '1');

  const { access, logout } = useAuth();
  const { canEdit, isEditorMode, toggle, isAdmin } = useEditorMode();
  const nav = useNavigate();

  // ===== Квоты (5 отправок решений и 5 загрузок топа) =====
  const [quotas, setQuotas] = useState(null);

  // "туннель" как у саппорта: квоты обновляются событиями (без спама запросами)
  const [quotaOpen, setQuotaOpen] = useState(false);
  const quotaRef = useRef(null);

  useEffect(() => {
    if (!access) {
      setQuotas(null);
      setQuotaOpen(false);
      return;
    }

    let alive = true;

    // 1) разово подгружаем с сервера (чтобы было видно сразу после входа)
    (async () => {
      try {
        const q = await getMyQuotas();
        if (alive) setQuotas(q);
      } catch {
        // не шумим: квоты — вспомогательная инфа
      }
    })();

    // 2) дальше обновляемся через события из axios-интерцептора ("туннель")
    const onQuotaUpdate = (ev) => {
      const d = ev?.detail;
      if (!d?.bucket) return;
      setQuotas((prev) => {
        const next = { ...(prev || {}) };
        const key = d.bucket === 'tasks' ? 'tasks' : d.bucket === 'top' ? 'top' : null;
        if (!key) return prev;
        next[key] = {
          remaining: Number.isFinite(d.remaining) ? d.remaining : prev?.[key]?.remaining,
          capacity: Number.isFinite(d.capacity) ? d.capacity : prev?.[key]?.capacity,
          retryAfterSeconds: Number.isFinite(d.retryAfterSeconds)
            ? d.retryAfterSeconds
            : prev?.[key]?.retryAfterSeconds,
        };
        return next;
      });
    };
    window.addEventListener('quota:update', onQuotaUpdate);

    return () => {
      alive = false;
      window.removeEventListener('quota:update', onQuotaUpdate);
    };
  }, [access]);

  const QuotaPill = ({ compact = false } = {}) => {
    if (!access) return null;
    const t = quotas?.tasks;
    const top = quotas?.top;

    // если API ещё не вернуло данные — показываем нейтрально
    const tasksText = t ? `${t.remaining}/${t.capacity}` : '—/—';
    const topText = top ? `${top.remaining}/${top.capacity}` : '—/—';

    const title = [
      t
        ? `Решения: ${t.remaining}/${t.capacity}${t.retryAfterSeconds ? ` • ждать ${t.retryAfterSeconds}с` : ''}`
        : 'Решения: —',
      top
        ? `Топ: ${top.remaining}/${top.capacity}${top.retryAfterSeconds ? ` • ждать ${top.retryAfterSeconds}с` : ''}`
        : 'Топ: —',
    ].join('\n');

    return (
      <div className="relative" ref={quotaRef}>
        <button
          type="button"
          className={
            `${compact ? '' : 'hidden md:inline-flex '}items-center gap-2 rounded-xl border ` +
            'border-neutral-200/70 dark:border-neutral-800/70 bg-white/60 dark:bg-neutral-900/40 ' +
            'px-3 py-1.5 text-xs text-neutral-700 dark:text-neutral-200 hover:bg-white/80 dark:hover:bg-neutral-900/60'
          }
          title={title}
          onClick={() => setQuotaOpen((v) => !v)}
        >
          <span className="opacity-70">Квоты</span>
          <span className="font-medium">реш: {tasksText}</span>
          <span className="opacity-40">•</span>
          <span className="font-medium">топ: {topText}</span>
          <ChevronDown size={14} className={`opacity-60 transition ${quotaOpen ? 'rotate-180' : ''}`} />
        </button>

        {quotaOpen && (
          <div className="absolute right-0 mt-2 w-64 rounded-2xl border border-neutral-200/60 dark:border-neutral-800/60 bg-[rgb(var(--card))] shadow-soft p-3 z-50 text-xs">
            <div className="font-semibold mb-2">Квоты</div>
            <div className="space-y-1 text-neutral-700 dark:text-neutral-200">
              <div className="flex items-center justify-between">
                <span className="opacity-75">Решения</span>
                <span className="font-medium">{tasksText}</span>
              </div>
              {t?.retryAfterSeconds ? (
                <div className="text-neutral-500 dark:text-neutral-400">Ждать: {t.retryAfterSeconds} сек.</div>
              ) : null}

              <div className="h-px bg-neutral-200/60 dark:bg-neutral-800/60 my-2" />

              <div className="flex items-center justify-between">
                <span className="opacity-75">Топ</span>
                <span className="font-medium">{topText}</span>
              </div>
              {top?.retryAfterSeconds ? (
                <div className="text-neutral-500 dark:text-neutral-400">Ждать: {top.retryAfterSeconds} сек.</div>
              ) : null}
            </div>
          </div>
        )}
      </div>
    );
  };

  const toggleMode = () => setMode((m) => (m === 'dark' ? 'light' : 'dark'));
  const cycleColor = () =>
    setColorTheme((c) => (c === 'blue' ? 'pink' : c === 'pink' ? 'apple' : 'blue'));
  const toggleBgFx = () =>
    setBgFx((v) => {
      const nv = !v;
      return nv;
    });

  // применяем классы для темы и сохраняем в localStorage
  useEffect(() => {
    const el = document.documentElement;
    const cls = el.classList;
    cls.remove('blue', 'pink', 'apple');
    cls.remove('dark');

    if (colorTheme) cls.add(colorTheme);
    if (mode === 'dark') cls.add('dark');

    el.dataset.colorTheme = colorTheme;
    localStorage.setItem('colorTheme', colorTheme);
    localStorage.setItem('mode', mode);
  }, [colorTheme, mode]);

  useEffect(() => {
    localStorage.setItem('bgFx', bgFx ? '1' : '0');
    const root = document.documentElement;
    root.classList.toggle('bgfx', bgFx);
  }, [bgFx]);

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
      if (quotaRef.current && !quotaRef.current.contains(e.target)) setQuotaOpen(false);
    };
    const onEsc = (e) => {
      if (e.key === 'Escape') {
        setMoreOpen(false);
        setAdminOpen(false);
        setQuotaOpen(false);
      }
    };
    document.addEventListener('mousedown', onDocClick);
    document.addEventListener('keydown', onEsc);
    return () => {
      document.removeEventListener('mousedown', onDocClick);
      document.removeEventListener('keydown', onEsc);
    };
  }, []);

  // ===== Автосворачивание навигации в "..." при переполнении =====
  const headerRowRef = useRef(null);
  const [forceCompact, setForceCompact] = useState(false);

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
  }, [access, isAdmin, canEdit, isEditorMode, mode, colorTheme, quotas]);

  return (
    // isolate + z-слои: чтобы фиксированный фон не "проваливался" под body (иначе эффекты не видны)
    <div className="min-h-screen relative isolate">
      {/* фон: (по переключателю) мягкий градиент + размытые блики */}
      <div className="pointer-events-none fixed inset-0 z-0 overflow-hidden">
        {bgFx && (
          <>
            <div className="absolute inset-0 bg-gradient-to-b from-brand-600/12 via-transparent to-transparent blur-2xl" />
            <div className="absolute inset-0">
            {/*
              ВАЖНО: эффекты реализованы чистым CSS (см. index.css: `.bg-fx ...`).
              Это защищает от Tailwind purge и гарантирует, что блики видны на всех темах.
            */}
            <div className="bg-fx">
              <div className="bg-fx__blob" />
              <div className="bg-fx__blob" />
              <div className="bg-fx__blob" />
            </div>
            </div>
          </>
        )}
      </div>
      <header className="sticky top-0 z-20 border-b border-neutral-200/70 dark:border-neutral-800/70 backdrop-blur bg-white/70 dark:bg-neutral-900/60">
        <div ref={headerRowRef} className="container-app flex h-16 items-center justify-between gap-2">
          {/* Логотип и название */}
          <Link to="/courses" className="flex min-w-0 items-center gap-3">
            <div className="h-9 w-9 rounded-xl bg-brand-600 text-white grid place-items-center shadow-soft">
              <PanelsTopLeft size={18} />
            </div>
            <div className="font-semibold truncate">TaskForge</div>
            {/* подпись "Платформа задач" убрали — она съедает место и ломает хедер */}
          </Link>

          {/* Правая панель — крупные экраны */}
          <div className={`hidden xl:flex items-center gap-2 ${forceCompact ? 'xl:hidden' : ''}`}
          >
            {/* квоты (видны только авторизованным) */}
            <QuotaPill />

            {/* цвет + режим */}
            <button
              className="btn-outline"
              onClick={cycleColor}
              aria-label="Switch color theme"
              title={`Цвет: ${colorTheme}`}
            >
              <Palette size={18} />
              {/* текст скрываем, чтобы шапка не переполнялась; информация есть в title */}
              <span className="hidden 2xl:inline">{colorTheme}</span>
            </button>

            <button
              className="btn-outline"
              onClick={toggleMode}
              aria-label="Toggle dark mode"
              title={isDark ? 'Тёмная' : 'Светлая'}
            >
              {isDark ? <Sun size={18} /> : <Moon size={18} />}
              {/* без текста — только иконка */}
            </button>

            <button
              className={`btn-outline transition ${
                bgFx
                  ? 'border-brand-600/70 bg-brand-600/10 text-brand-700 dark:text-brand-200 shadow-soft'
                  : 'hover:border-neutral-300/70 dark:hover:border-neutral-700/70'
              }`}
              onClick={toggleBgFx}
              aria-pressed={bgFx}
              aria-label="Toggle background effects"
              title={bgFx ? 'Фоновые эффекты: вкл' : 'Фоновые эффекты: выкл'}
            >
              <Sparkles size={18} />
            </button>

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

            {/* профиль */}
            {access && (
              <Link to="/profile" className="btn-outline" title="Профиль">
                <User size={18} />
                <span className="hidden 2xl:inline">Профиль</span>
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

          {/* Мобильное меню — одна кнопка "..." */}
          <div className={`relative ${forceCompact ? '' : 'xl:hidden'}`} ref={moreRef}>
            {/* квоты в компактном режиме тоже показываем */}
            {forceCompact ? <QuotaPill compact /> : null}
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
                className="absolute right-0 mt-2 w-56 rounded-2xl border border-neutral-200/60 dark:border-neutral-800/60 bg-[rgb(var(--card))] shadow-soft p-1 z-50"
              >
                {/* Цвет */}
                <button
                  role="menuitem"
                  className="btn-ghost w-full justify-start"
                  onClick={() => {
                    setMoreOpen(false);
                    cycleColor();
                  }}
                >
                  <Palette size={18} />
                  <span>Цвет: {colorTheme}</span>
                </button>

                {/* Свет/тёмная */}
                <button
                  role="menuitem"
                  className="btn-ghost w-full justify-start"
                  onClick={() => {
                    setMoreOpen(false);
                    toggleMode();
                  }}
                >
                  {isDark ? <Sun size={18} /> : <Moon size={18} />}
                  <span>{isDark ? 'Светлая' : 'Тёмная'}</span>
                </button>

                {/* Эффекты фона */}
                <button
                  role="menuitem"
                  className="btn-ghost w-full justify-start"
                  onClick={() => {
                    setMoreOpen(false);
                    toggleBgFx();
                  }}
                >
                  <Sparkles size={18} />
                  <span>{bgFx ? 'Эффекты: вкл' : 'Эффекты: выкл'}</span>
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
                {access && isAdmin && (
                  <>
                    <Link
                      role="menuitem"
                      to="/admin/solutions"
                      className="btn-ghost w-full justify-start"
                      onClick={() => setMoreOpen(false)}
                      title="Управление пользователями"
                    >
                      <ListOrdered size={18} />
                      <span>Управление пользователями</span>
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
                      to="/admin/groups"
                      className="btn-ghost w-full justify-start"
                      onClick={() => setMoreOpen(false)}
                      title="Группы пользователей"
                    >
                      <Users size={18} />
                      <span>Группы</span>
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