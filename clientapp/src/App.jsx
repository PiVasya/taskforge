import { Routes, Route, Navigate } from 'react-router-dom';
import ProtectedRoute from './auth/ProtectedRoute';
import EditorRoute from './auth/EditorRoute';

import LoginPage from './pages/LoginPage';
import RegisterPage from './pages/RegisterPage';
import CoursesPage from './pages/CoursesPage';
import CourseAssignmentsPage from './pages/CourseAssignmentsPage';
import CourseEditPage from './pages/CourseEditPage';
import AssignmentEditPage from './pages/AssignmentEditPage';
import AssignmentSolvePage from './pages/AssignmentSolvePage';

// Newly added pages
import ProfilePage from './pages/ProfilePage';
import AssignmentTopSolutionsPage from './pages/AssignmentTopSolutionsPage';

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
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route path="/register" element={<RegisterPage />} />
      <Route element={<ProtectedRoute />}>
        <Route path="/" element={<Home />} />
        <Route path="/courses" element={<CoursesPage />} />
        <Route path="/course/:courseId" element={<CourseAssignmentsPage />} />
        {/* viewer-страница решения */}
        <Route path="/assignment/:assignmentId" element={<AssignmentSolvePage />} />
        {/* leaderboard for assignment */}
        <Route path="/assignment/:assignmentId/top" element={<AssignmentTopSolutionsPage />} />
        {/* user profile */}
        <Route path="/profile" element={<ProfilePage />} />
        {/* редакторские страницы — только в режиме редактора */}
        <Route element={<EditorRoute fallbackTo="course" />}>
          <Route path="/courses/:courseId/edit" element={<CourseEditPage />} />
          <Route path="/assignment/:assignmentId/edit" element={<AssignmentEditPage />} />
        </Route>
      </Route>
      <Route path="*" element={<NotFound />} />
    </Routes>
  );
}
