import { useEffect, useLayoutEffect, useRef } from 'react';
import { useLocation } from 'react-router-dom';
import AuthProvider, { useAuth } from '../auth/AuthContext';
import { NotifyProvider } from '../components/notify/NotifyProvider';
import SupportNotifier from '../components/SupportNotifier';
import PersistentBackground from '../components/shell/PersistentBackground';
import EditorModeProvider from '../contexts/EditorModeContext';
import { QuotaProvider } from '../contexts/QuotaContext';
import { UiSettingsProvider } from '../contexts/UiSettingsContext';
import api from '../api/http';
import { QueryClientProvider } from '../data/QueryClientProvider';


function RouteScrollReset() {
  const location = useLocation();
  const previousPathRef = useRef(location.pathname);

  useLayoutEffect(() => {
    if (location.state?.courseMapOverlay) return;

    const pathChanged = previousPathRef.current !== location.pathname;
    previousPathRef.current = location.pathname;
    if (!pathChanged) return;

    window.scrollTo({ top: 0, left: 0, behavior: 'auto' });
  }, [location.pathname, location.state]);

  useEffect(() => {
    if (!('scrollRestoration' in window.history)) return undefined;
    const previous = window.history.scrollRestoration;
    window.history.scrollRestoration = 'manual';
    return () => {
      window.history.scrollRestoration = previous;
    };
  }, []);

  return null;
}

function PageViewTracker() {
  const { access } = useAuth();
  const location = useLocation();
  const lastTrackedPathRef = useRef('');

  useEffect(() => {
    if (!access) return;
    const path = `${location.pathname}${location.search || ''}`;
    if (!path || lastTrackedPathRef.current === path) return;
    lastTrackedPathRef.current = path;
    const title = typeof document !== 'undefined' ? document.title || path : path;
    api.post('/api/activity/page-view', {
      path,
      title,
      action: 'page-view',
      source: 'page',
    }).catch(() => {});
  }, [access, location.pathname, location.search]);

  return null;
}

function SessionProviders({ children }) {
  const { access } = useAuth();
  return (
    <QuotaProvider enabled={Boolean(access)}>
      <div className="min-h-screen relative isolate flex flex-col">
        <PersistentBackground />
        <RouteScrollReset />
        <PageViewTracker />
        <SupportNotifier />
        {children}
      </div>
    </QuotaProvider>
  );
}

export default function AppProviders({ children }) {
  return (
    <AuthProvider>
      <EditorModeProvider>
        <NotifyProvider>
          <QueryClientProvider>
            <UiSettingsProvider>
              <SessionProviders>{children}</SessionProviders>
            </UiSettingsProvider>
          </QueryClientProvider>
        </NotifyProvider>
      </EditorModeProvider>
    </AuthProvider>
  );
}
