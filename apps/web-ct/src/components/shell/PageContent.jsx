import React, { Suspense, useMemo } from 'react';
import { matchPath, Outlet, useLocation } from 'react-router-dom';

const FULL_WIDTH_ROUTES = [
  '/',
  '/courses',
  '/courses/:courseSlug',
  '/courses/:courseSlug/conspects/:slug',
  '/courses/:courseSlug/tasks',
  '/conspects/:slug',
  '/editor',
  '/editor/:sectionCode',
  '/editor/courses/:courseSlug',
  '/:sectionCode',
];

function isFullWidthRoute(pathname) {
  return FULL_WIDTH_ROUTES.some((path) => matchPath({ path, end: true }, pathname));
}

function LoadingFallback() {
  return (
    <div className="container-app py-8" aria-live="polite">
      <div className="animate-pulse rounded-3xl border border-neutral-200/70 bg-white/75 p-6 dark:border-neutral-800/70 dark:bg-neutral-900/65">
        <div className="h-5 w-44 rounded-full bg-neutral-200 dark:bg-neutral-800" />
        <div className="mt-5 h-3 w-full rounded-full bg-neutral-200 dark:bg-neutral-800" />
        <div className="mt-3 h-3 w-2/3 rounded-full bg-neutral-200 dark:bg-neutral-800" />
      </div>
    </div>
  );
}

function PageContent() {
  const { pathname } = useLocation();
  const fullWidth = useMemo(() => isFullWidthRoute(pathname), [pathname]);

  return (
    <main className={fullWidth ? '' : 'container-app py-8'}>
      <Suspense fallback={<LoadingFallback />}>
        <Outlet />
      </Suspense>
    </main>
  );
}

export default React.memo(PageContent);
