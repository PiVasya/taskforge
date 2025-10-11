import { Routes, Route, Navigate } from "react-router-dom";
import ProtectedRoute from "./auth/ProtectedRoute";
import EditorRoute from "./auth/EditorRoute";

import LoginPage from "./pages/LoginPage";
import RegisterPage from "./pages/RegisterPage";
import CoursesPage from "./pages/CoursesPage";
import CourseAssignmentsPage from "./pages/CourseAssignmentsPage";
import CourseEditPage from "./pages/CourseEditPage";
import AssignmentEditPage from "./pages/AssignmentEditPage";
import AssignmentSolvePage from "./pages/AssignmentSolvePage";

import AdminSolutionsPage from "./pages/admin/AdminSolutionsPage";
import LeaderboardPage from "./pages/admin/LeaderboardPage";

import { NotifyProvider } from "./components/notify/NotifyProvider";

function Home() { return <Navigate to="/login" replace />; }
function NotFound() { return <div className="container-app py-10">Страница не найдена</div>; }

export default function App() {
  return (
    <NotifyProvider>
      <Routes>
        <Route path="/" element={<Home />} />

        <Route path="/login" element={<LoginPage />} />
        <Route path="/register" element={<RegisterPage />} />

        <Route element={<ProtectedRoute />}>
          {/* Основные страницы */}
          <Route path="/courses" element={<CoursesPage />} />
          <Route path="/course/:courseId" element={<CourseAssignmentsPage />} />
          <Route path="/assignment/:assignmentId" element={<AssignmentSolvePage />} />

          {/* Только в режиме редактора */}
          <Route element={<EditorRoute fallbackTo="course" />}>
            <Route path="/courses/:courseId/edit" element={<CourseEditPage />} />
            <Route path="/assignment/:assignmentId/edit" element={<AssignmentEditPage />} />
          </Route>

          {/* Админ-страницы */}
          <Route path="/admin/solutions" element={<AdminSolutionsPage />} />
          <Route path="/admin/leaderboard" element={<LeaderboardPage />} />
        </Route>

        <Route path="*" element={<NotFound />} />
      </Routes>
    </NotifyProvider>
  );
}
