﻿import { Routes, Route, Navigate } from 'react-router-dom';
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
import AssignmentResultsPage from './pages/AssignmentResultsPage';

import ProfilePage from './pages/ProfilePage';
import MySolutionsPage from './pages/MySolutionsPage';
import AssignmentTopSolutionsPage from './pages/AssignmentTopSolutionsPage';

// Admin pages
import LeaderboardPage from './pages/admin/LeaderboardPage';
import AdminSolutionsPage from './pages/admin/AdminSolutionsPage';

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

          {/* решение задания */}
          <Route path="/assignment/:assignmentId" element={<AssignmentSolvePage />} />
          {/* отдельная страница результатов; открываем после сабмита */}
          <Route path="/assignment/:assignmentId/results" element={<AssignmentResultsPage />} />

          {/* при необходимости — остаётся, но кнопку на SolvePage не показываем */}
          <Route path="/assignment/:assignmentId/top" element={<AssignmentTopSolutionsPage />} />

          <Route path="/profile" element={<ProfilePage />} />
          <Route path="/my/solutions" element={<MySolutionsPage />} />
          <Route path="/leaderboard" element={<LeaderboardPage />} />

          <Route element={<EditorRoute fallbackTo="courses" />}>
            <Route path="/courses/:courseId/edit" element={<CourseEditPage />} />
            <Route path="/assignment/:assignmentId/edit" element={<AssignmentEditPage />} />
            <Route path="/admin/leaderboard" element={<LeaderboardPage />} />
            <Route path="/admin/solutions" element={<AdminSolutionsPage />} />
          </Route>
        </Route>

        <Route path="*" element={<NotFound />} />
      </Routes>
    </NotifyProvider>
  );
}
