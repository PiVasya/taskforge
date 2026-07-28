import React from 'react';
import { useAuth } from '../auth/AuthContext';
import AppShell from './AppShell';
import PublicShell from './PublicShell';

function RootShell() {
  const { access } = useAuth();
  return access ? <AppShell /> : <PublicShell />;
}

export default React.memo(RootShell);
