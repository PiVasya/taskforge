import { useEffect } from 'react';
import AuthProvider from '../auth/AuthContext';
import EditorModeProvider from '../contexts/EditorModeContext';
import { applyStoredUiAppearance } from '../utils/uiAppearance';

function UiAppearanceSync() {
  useEffect(() => {
    const sync = () => applyStoredUiAppearance();
    window.addEventListener('storage', sync);
    window.addEventListener('tf-ui-settings-changed', sync);
    return () => {
      window.removeEventListener('storage', sync);
      window.removeEventListener('tf-ui-settings-changed', sync);
    };
  }, []);

  return null;
}

export default function AppProviders({ children }) {
  return (
    <AuthProvider>
      <UiAppearanceSync />
      <EditorModeProvider>{children}</EditorModeProvider>
    </AuthProvider>
  );
}
