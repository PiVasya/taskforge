import { Routes, Route, Navigate } from 'react-router-dom';
// Провайдер уведомлений
import { NotifyProvider } from './components/notify/NotifyProvider';
import ProtectedRoute from './auth/ProtectedRoute';
import EditorRoute from './auth/EditorRoute';

import LoginPage from './pages/LoginPage';
import RegisterPage from './pages/RegisterPage';
import CoursesPage from './pages/CoursesPage';
import CourseAssignmentsPage from './pages/CourseAssignmentsPage';
import CourseEditPage from './pages/CourseEditPage';
import AssignmentEditPage from './pages/AssignmentEditPage';
import AssignmentSolvePage from './pages/AssignmentSolvePage';
import AssignmentResultsPage from './pages/AssignmentResultsPage'; // ← добавлен импорт

import ProfilePage from './pages/ProfilePage';
import AssignmentTopSolutionsPage from './pages/AssignmentTopSolutionsPage';

// Admin pages
import LeaderboardPage from './pages/admin/LeaderboardPage';
import AdminSolutionsPage from './pages/admin/AdminSolutionsPage';

function Home() {
  return <Navigate to="/courses" replace />;
}

function NotFound() {
  return <div className="container-app py-10">Страница не найдена</div>;
}

export default function App() {
  return (
    <NotifyProvider>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="/register" element={<RegisterPage />} />

        {/* Все остальные страницы доступны только авторизованным пользователям */}
        <Route element={<ProtectedRoute />}>
          <Route path="/" element={<Home />} />
          <Route path="/courses" element={<CoursesPage />} />
          <Route path="/course/:courseId" element={<CourseAssignmentsPage />} />

          {/* Страница решения задания */}
          <Route path="/assignment/:assignmentId" element={<AssignmentSolvePage />} />
          <Route path="/assignment/:assignmentId/results" element={<AssignmentResultsPage />} />
          {/* Таблица лидеров по заданию */}
          <Route path="/assignment/:assignmentId/top" element={<AssignmentTopSolutionsPage />} />

          {/* Страница профиля пользователя */}
          <Route path="/profile" element={<ProfilePage />} />

          {/* Общий рейтинг — доступен всем авторизованным пользователям */}
          <Route path="/leaderboard" element={<LeaderboardPage />} />

          {/* Редакторские и админские маршруты */}
          <Route element={<EditorRoute fallbackTo="courses" />}>
            <Route path="/courses/:courseId/edit" element={<CourseEditPage />} />
            <Route path="/assignment/:assignmentId/edit" element={<AssignmentEditPage />} />
            {/* admin pages */}
            <Route path="/admin/leaderboard" element={<LeaderboardPage />} />
            <Route path="/admin/solutions" element={<AdminSolutionsPage />} />
          </Route>
        </Route>

        {/* 404 */}
        <Route path="*" element={<NotFound />} />
      </Routes>
    </NotifyProvider>
  );
}
