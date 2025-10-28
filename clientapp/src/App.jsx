import { Routes, Route, Navigate } from 'react-router-dom';
import ProtectedRoute from './auth/ProtectedRoute';
import EditorRoute from './auth/EditorRoute';

// подключаем провайдер уведомлений
import { NotifyProvider } from "./components/notify/NotifyProvider";

import LoginPage from './pages/LoginPage';
import RegisterPage from './pages/RegisterPage';
import CoursesPage from './pages/CoursesPage';
import CourseAssignmentsPage from './pages/CourseAssignmentsPage';
import CourseEditPage from './pages/CourseEditPage';
import AssignmentEditPage from './pages/AssignmentEditPage';
import AssignmentSolvePage from './pages/AssignmentSolvePage';

// новые страницы
import ProfilePage from './pages/ProfilePage';
import AssignmentTopSolutionsPage from './pages/AssignmentTopSolutionsPage';

/**
 * Главный компонент приложения. Здесь объявляем все маршруты.
 * Внутри NotifyProvider — это важно для корректной работы useNotify().
 */
function Home() {
  return <Navigate to="/login" replace />;
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
        <Route element={<ProtectedRoute />}>
          <Route path="/" element={<Home />} />
          <Route path="/courses" element={<CoursesPage />} />
          <Route path="/course/:courseId" element={<CourseAssignmentsPage />} />
          <Route path="/assignment/:assignmentId" element={<AssignmentSolvePage />} />
          <Route path="/assignment/:assignmentId/top" element={<AssignmentTopSolutionsPage />} />
          <Route path="/profile" element={<ProfilePage />} />
          {/* редакторские страницы — разрешены только в режиме редактора */}
          <Route element={<EditorRoute fallbackTo="course" />}>
            <Route path="/courses/:courseId/edit" element={<CourseEditPage />} />
            <Route path="/assignment/:assignmentId/edit" element={<AssignmentEditPage />} />
          </Route>
        </Route>
        <Route path="*" element={<NotFound />} />
      </Routes>
    </NotifyProvider>
  );
}
