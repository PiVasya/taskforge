import React, { Suspense, useMemo } from 'react';
import { Outlet, matchPath, useLocation } from 'react-router-dom';
import { useUiNavigationSettings } from '../../contexts/UiSettingsContext';
import DesktopSidebar from './DesktopSidebar';
import { useShellNavigation } from './navigation';
import RouteErrorBoundary from './RouteErrorBoundary';

const FULL_WIDTH_ROUTES = [
  '/',
  '/assignment/:assignmentId/edit',
  '/admin/ai',
  '/agent',
  '/ai',
  '/compiler',
];

function isFullWidthRoute(pathname) {
  return FULL_WIDTH_ROUTES.some((path) => matchPath({ path, end: true }, pathname));
}

function RouteLoadingFallback() {
  return (
    <div className="min-h-[12rem] rounded-3xl border border-neutral-200/60 dark:border-neutral-800/60 bg-[rgb(var(--card))]/55 p-6 shadow-soft backdrop-blur" aria-live="polite">
      <div className="tf-skeleton h-5 w-44 rounded-full" />
      <div className="mt-5 space-y-3">
        <div className="tf-skeleton h-3 w-full rounded-full" />
        <div className="tf-skeleton h-3 w-5/6 rounded-full" />
        <div className="tf-skeleton h-3 w-2/3 rounded-full" />
      </div>
    </div>
  );
}

function PageContent({ authenticated = false }) {
  const { pathname } = useLocation();
  const { sidebarCollapsed } = useUiNavigationSettings();
  const { isAdminArea } = useShellNavigation();

  const fullWidth = useMemo(() => isFullWidthRoute(pathname), [pathname]);
  // Authenticated pages always use the same full-width shell gutters. Previously
  // AI routes used these gutters while the rest of the app used container-app,
  // which made the persistent left navigation jump horizontally between pages.
  const mainClassName = authenticated || fullWidth
    ? 'w-full max-w-none px-3 sm:px-5 lg:px-6 xl:px-8 2xl:px-10 py-4 sm:py-8 relative z-10'
    : 'container-app py-4 sm:py-8 relative z-10';

  const gridClassName = authenticated
    ? sidebarCollapsed
      ? 'items-start gap-4 2xl:gap-6 xl:grid xl:grid-cols-[4.75rem,minmax(0,1fr)]'
      : 'items-start gap-4 2xl:gap-6 xl:grid xl:grid-cols-[15rem,minmax(0,1fr)] 2xl:grid-cols-[15.5rem,minmax(0,1fr)]'
    : 'items-start';

  return (
    <main className={mainClassName}>
      <div className={gridClassName}>
        {authenticated ? <DesktopSidebar /> : null}
        <section
          className={`min-w-0 overflow-x-hidden xl:px-1 2xl:px-2 ${isAdminArea ? 'admin-mobile-content' : ''}`}
        >
          <RouteErrorBoundary resetKey={pathname}>
            <Suspense fallback={<RouteLoadingFallback />}>
              <Outlet />
            </Suspense>
          </RouteErrorBoundary>
        </section>
      </div>
    </main>
  );
}

export default React.memo(PageContent);
