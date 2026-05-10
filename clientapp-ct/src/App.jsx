import { Routes, Route, Navigate } from 'react-router-dom';
import ProtectedRoute from './auth/ProtectedRoute';
import LoginPage from './pages/LoginPage';
import RegisterPage from './pages/RegisterPage';
import PrivacyPolicyPage from './pages/PrivacyPolicyPage';
import LearningHomePage from './pages/LearningHomePage';
import LearningCoursePage from './pages/LearningCoursePage';
import LearningConspectPage from './pages/LearningConspectPage';
import QuizTasksPage from './pages/QuizTasksPage';
import LearningEditorPage from './pages/LearningEditorPage';

export default function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route path="/register" element={<RegisterPage />} />
      <Route path="/privacy" element={<PrivacyPolicyPage />} />
      <Route element={<ProtectedRoute />}>
        <Route path="/" element={<LearningHomePage />} />
        <Route path="/courses/:courseSlug" element={<LearningCoursePage />} />
        <Route path="/courses/:courseSlug/conspects/:slug" element={<LearningConspectPage />} />
        <Route path="/courses/:courseSlug/tasks" element={<QuizTasksPage />} />
        <Route path="/tasks" element={<QuizTasksPage />} />
        <Route path="/conspects/:slug" element={<LearningConspectPage />} />
        <Route path="/editor" element={<LearningEditorPage />} />
        <Route path="/editor/courses/:courseSlug" element={<LearningEditorPage />} />
      </Route>
      <Route path="/admin/conspects" element={<Navigate to="/editor" replace />} />
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}
