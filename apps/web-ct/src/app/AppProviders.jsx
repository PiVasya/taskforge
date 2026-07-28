import AuthProvider from '../auth/AuthContext';
import EditorModeProvider from '../contexts/EditorModeContext';

export default function AppProviders({ children }) {
  return (
    <AuthProvider>
      <EditorModeProvider>{children}</EditorModeProvider>
    </AuthProvider>
  );
}
