import React from 'react';
import AppHeader from '../components/shell/AppHeader';
import PageContent from '../components/shell/PageContent';

function AppShell() {
  return (
    <div className="min-h-screen bg-neutral-50 text-neutral-900 dark:bg-neutral-950 dark:text-neutral-100">
      <AppHeader />
      <PageContent />
    </div>
  );
}

export default React.memo(AppShell);
