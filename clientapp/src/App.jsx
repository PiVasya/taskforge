import { Routes, Route, Navigate } from 'react-router-dom';
// Import the notification provider to supply `useNotify` context.
// NOTE: NotifyProvider lives in components/notify; contexts/NotifyProvider не существует.
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

// Страницы пользовательского функционала
import ProfilePage from './pages/ProfilePage';
import AssignmentTopSolutionsPage from './pages/AssignmentTopSolutionsPage';

// Административные страницы
import LeaderboardPage from './pages/admin/LeaderboardPage';
import AdminSolutionsPage from './pages/admin/AdminSolutionsPage';

function Home() {
  return <Navigate to="/login" replace />;
}

function NotFound() {
  return (
    <div className="container-app py-10">Страница не найдена</div>
  );
}

export default function App() {
  return (
    // NotifyProvider нужен, чтобы хук useNotify работал без ошибки
    <NotifyProvider>
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="/register" element={<RegisterPage />} />
        {/* все остальные страницы доступны только авторизованным пользователям */}
        <Route element={<ProtectedRoute />}>
          <Route path="/" element={<Home />} />
          <Route path="/courses" element={<CoursesPage />} />
          <Route path="/course/:courseId" element={<CourseAssignmentsPage />} />
          {/* страница решения задания */}
          <Route path="/assignment/:assignmentId" element={<AssignmentSolvePage />} />
          {/* таблица лидеров по конкретному заданию */}
          <Route path="/assignment/:assignmentId/top" element={<AssignmentTopSolutionsPage />} />
          {/* страница профиля пользователя */}
          <Route path="/profile" element={<ProfilePage />} />
          {/* редакторские и админские страницы */}
          <Route element={<EditorRoute fallbackTo="courses" />}>
            {/* редактирование курсов и заданий */}
            <Route path="/courses/:courseId/edit" element={<CourseEditPage />} />
            <Route path="/assignment/:assignmentId/edit" element={<AssignmentEditPage />} />
            {/* административные страницы */}
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
