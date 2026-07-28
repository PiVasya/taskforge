import { lazy } from 'react';
import { Navigate, Route, Routes } from 'react-router-dom';
import ProtectedRoute from './auth/ProtectedRoute';
import AppShell from './app/AppShell';

const LoginPage = lazy(() => import('./pages/LoginPage'));
const RegisterPage = lazy(() => import('./pages/RegisterPage'));
const PrivacyPolicyPage = lazy(() => import('./pages/PrivacyPolicyPage'));
const SimpleHomePage = lazy(() => import('./pages/SimpleHomePage'));
const SimpleSectionPage = lazy(() => import('./pages/SimpleSectionPage'));
const LearningHomePage = lazy(() => import('./pages/LearningHomePage'));
const LearningCoursePage = lazy(() => import('./pages/LearningCoursePage'));
const LearningConspectPage = lazy(() => import('./pages/LearningConspectPage'));
const QuizTasksPage = lazy(() => import('./pages/QuizTasksPage'));
const EditorRedirectPage = lazy(() => import('./pages/EditorRedirectPage'));

export default function App() {
  return (
    <Routes>
      <Route element={<AppShell />}>
        <Route path="/login" element={<LoginPage />} />
        <Route path="/register" element={<RegisterPage />} />
        <Route path="/privacy" element={<PrivacyPolicyPage />} />

        <Route element={<ProtectedRoute />}>
          <Route path="/" element={<SimpleHomePage />} />
          <Route path="/courses" element={<LearningHomePage />} />
          <Route path="/courses/:courseSlug" element={<LearningCoursePage />} />
          <Route path="/courses/:courseSlug/conspects/:slug" element={<LearningConspectPage />} />
          <Route path="/courses/:courseSlug/tasks" element={<QuizTasksPage />} />
          <Route path="/tasks" element={<Navigate to="/" replace />} />
          <Route path="/conspects/:slug" element={<LearningConspectPage />} />
          <Route path="/editor" element={<EditorRedirectPage />} />
          <Route path="/editor/:sectionCode" element={<EditorRedirectPage />} />
          <Route path="/editor/courses/:courseSlug" element={<EditorRedirectPage />} />
          <Route path="/:sectionCode" element={<SimpleSectionPage />} />
        </Route>

        <Route path="/admin/conspects" element={<Navigate to="/editor/a1" replace />} />
        <Route path="*" element={<Navigate to="/" replace />} />
      </Route>
    </Routes>
  );
}
