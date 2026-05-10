import { Routes, Route, Navigate } from 'react-router-dom';

import ProtectedRoute from './auth/ProtectedRoute';
import LoginPage from './pages/LoginPage';
import RegisterPage from './pages/RegisterPage';
import PrivacyPolicyPage from './pages/PrivacyPolicyPage';
import CoursesHomePage from './pages/CoursesHomePage';
import MainCoursePage from './pages/MainCoursePage';
import LearningCoursePage from './pages/LearningCoursePage';
import ConspectPage from './pages/ConspectPage';
import QuizTasksPage from './pages/QuizTasksPage';
import AdminConspectsPage from './pages/AdminConspectsPage';

export default function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route path="/register" element={<RegisterPage />} />
      <Route path="/privacy" element={<PrivacyPolicyPage />} />
      <Route element={<ProtectedRoute />}>
        <Route path="/" element={<CoursesHomePage />} />
        <Route path="/courses/:courseId" element={<MainCoursePage />} />
        <Route path="/learning/:slug" element={<LearningCoursePage />} />
        <Route path="/learning/:courseSlug/conspects/:slug" element={<ConspectPage />} />
        <Route path="/conspects/:slug" element={<ConspectPage />} />
        <Route path="/tasks" element={<QuizTasksPage />} />
        <Route path="/admin/conspects" element={<AdminConspectsPage />} />
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}
