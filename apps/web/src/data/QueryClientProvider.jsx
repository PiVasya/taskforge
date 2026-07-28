import React, { createContext, useContext, useEffect, useMemo } from 'react';
import { useAuth } from '../auth/AuthContext';
import { QueryClient } from './queryClient';

const QueryClientContext = createContext(null);

function getSessionKey(access, user) {
  if (!access) return 'public';
  return String(user?.id || user?.userId || user?.uuid || user?.login || 'authenticated');
}

export function QueryClientProvider({ children }) {
  const { access, user } = useAuth();
  const sessionKey = getSessionKey(access, user);
  const client = useMemo(() => new QueryClient(), [sessionKey]);

  useEffect(() => () => client.clear(), [client]);

  return <QueryClientContext.Provider value={client}>{children}</QueryClientContext.Provider>;
}

export function useQueryClient() {
  const client = useContext(QueryClientContext);
  if (!client) throw new Error('useQueryClient must be used inside QueryClientProvider');
  return client;
}
