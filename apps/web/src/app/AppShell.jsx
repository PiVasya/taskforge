import React, { useCallback, useState } from 'react';
import AppHeader from '../components/shell/AppHeader';
import MobileNavigation from '../components/shell/MobileNavigation';
import PageContent from '../components/shell/PageContent';

function AppShell() {
  const [mobileOpen, setMobileOpen] = useState(false);
  const openMobile = useCallback(() => setMobileOpen(true), []);
  const closeMobile = useCallback(() => setMobileOpen(false), []);

  return (
    <>
      <AppHeader onOpenMobile={openMobile} mobileOpen={mobileOpen} />
      <MobileNavigation open={mobileOpen} onClose={closeMobile} />
      <PageContent authenticated />
    </>
  );
}

export default React.memo(AppShell);
