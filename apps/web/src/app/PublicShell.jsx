import React, { useCallback, useMemo, useState } from 'react';
import { useLocation } from 'react-router-dom';
import AppHeader from '../components/shell/AppHeader';
import MobileNavigation from '../components/shell/MobileNavigation';
import PageContent from '../components/shell/PageContent';

function PublicShell() {
  const { pathname } = useLocation();
  const [mobileOpen, setMobileOpen] = useState(false);
  const openMobile = useCallback(() => setMobileOpen(true), []);
  const closeMobile = useCallback(() => setMobileOpen(false), []);
  const hideFooter = useMemo(() => pathname === '/', [pathname]);


  return (
    <>
      <AppHeader onOpenMobile={openMobile} mobileOpen={mobileOpen} />
      <MobileNavigation open={mobileOpen} onClose={closeMobile} />
      <PageContent />
      {!hideFooter && (
        <footer className="mt-12 border-t border-neutral-200/70 dark:border-neutral-800/70 relative z-10">
          <div className="container-app py-4 sm:py-6 text-sm text-neutral-500 dark:text-neutral-400 flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
            <div>© {new Date().getFullYear()} TaskForge</div>
          </div>
        </footer>
      )}
    </>
  );
}

export default React.memo(PublicShell);
