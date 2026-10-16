import React from 'react';
import { useAuth } from '../auth/AuthContext';
import AppShell from './AppShell';
import PublicShell from './PublicShell';

function RootShell() {
  const { isAuthenticated } = useAuth();
  return isAuthenticated ? <AppShell /> : <PublicShell />;
}

export default React.memo(RootShell);
