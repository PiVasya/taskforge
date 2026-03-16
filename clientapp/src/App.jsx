import { Routes, Route, Navigate } from 'react-router-dom';
import { NotifyProvider } from './components/notify/NotifyProvider';

import ProtectedRoute from './auth/ProtectedRoute';
import EditorRoute from './auth/EditorRoute';
import AdminRoute from './auth/AdminRoute';
import FeatureRoute from './auth/FeatureRoute';

import LoginPage from './pages/LoginPage';
import RegisterPage from './pages/RegisterPage';
import CoursesPage from './pages/CoursesPage';
import NewsPage from './pages/NewsPage';
import UpdatePostPage from './pages/UpdatePostPage';
import CourseAssignmentsPage from './pages/CourseAssignmentsPage';
import CourseEditPage from './pages/CourseEditPage';
import AssignmentEditPage from './pages/AssignmentEditPage';
import AssignmentSolvePage from './pages/AssignmentSolvePage';
import AssignmentResultsPage from './pages/AssignmentResultsPage';
import AssignmentImageResultsPage from './pages/AssignmentImageResultsPage';
import AssignmentTopSolutionsPage from './pages/AssignmentTopSolutionsPage';

import ProfilePage from './pages/ProfilePage';
import SettingsPage from './pages/SettingsPage';
import MySolutionsPage from './pages/MySolutionsPage';
import PublicProfilePage from './pages/PublicProfilePage';

// Support pages
import SupportTicketsPage from './pages/SupportTicketsPage';
import SupportCreatePage from './pages/SupportCreatePage';
import SupportChatPage from './pages/SupportChatPage';
import AdminSupportPage from './pages/AdminSupportPage';

import SupportNotifier from './components/SupportNotifier';

// Страница политики конфиденциальности (доступна без авторизации)
import PrivacyPolicyPage from './pages/PrivacyPolicyPage';

// Admin pages
import LeaderboardPage from './pages/admin/LeaderboardPage';
import AdminSolutionsPage from './pages/admin/AdminSolutionsPage';
import AdminBadgesPage from './pages/admin/AdminBadgesPage';
import AdminGroupsPage from './pages/admin/AdminGroupsPage';
import AdminFeatureRolesPage from './pages/admin/AdminFeatureRolesPage';
import AdminSystemStatusPage from './pages/admin/AdminSystemStatusPage';
import AdminUsersPage from './pages/admin/AdminUsersPage';
import AdminMinecraftLinksPage from './pages/admin/AdminMinecraftLinksPage';
import AdminAssignmentInsightsPage from './pages/admin/AdminAssignmentInsightsPage';
import AdminAnalyticsPage from './pages/admin/AdminAnalyticsPage';
import MinecraftChatPage from './pages/minecraft/MinecraftChatPage';

function Home() {
  return <Navigate to="/news" replace />;
}

function NotFound() {
  return <div className="container-app py-10">Страница не найдена</div>;
}

export default function App() {
  return (
    <NotifyProvider>
      <SupportNotifier />
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="/register" element={<RegisterPage />} />
        {/* Политика конфиденциальности: открытая страница */}
        <Route path="/privacy" element={<PrivacyPolicyPage />} />

        <Route element={<ProtectedRoute />}>
          <Route path="/" element={<Home />} />
          {/* главная лента / новости */}
          <Route path="/news" element={<NewsPage />} />
          <Route path="/news/:postId" element={<UpdatePostPage />} />

          <Route path="/courses" element={<CoursesPage />} />
          <Route path="/course/:courseId" element={<CourseAssignmentsPage />} />

          {/* решение задания */}
          <Route path="/assignment/:assignmentId" element={<AssignmentSolvePage />} />
          {/* отдельная страница результатов; открываем после сабмита */}
          <Route path="/assignment/:assignmentId/results" element={<AssignmentResultsPage />} />
          <Route path="/assignment/:assignmentId/image-results" element={<AssignmentImageResultsPage />} />

          {/* при необходимости — остаётся, но кнопку на SolvePage не показываем */}
          <Route path="/assignment/:assignmentId/top" element={<AssignmentTopSolutionsPage />} />

          <Route path="/profile" element={<ProfilePage />} />
          <Route path="/settings" element={<SettingsPage />} />
          <Route path="/my/solutions" element={<MySolutionsPage />} />

          <Route element={<FeatureRoute requiredRole="Minecraft" fallbackTo="/courses" />}>
            <Route path="/minecraft/chat" element={<MinecraftChatPage />} />
          </Route>

          {/* общий топ */}
          <Route path="/leaderboard" element={<LeaderboardPage />} />

          {/* публичный профиль по userId */}
          <Route path="/users/:userId" element={<PublicProfilePage />} />

          {/* техподдержка */}
          <Route path="/support" element={<SupportTicketsPage />} />
          <Route path="/support/new" element={<SupportCreatePage />} />
          <Route path="/support/:ticketId" element={<SupportChatPage />} />

          {/* редактор: только при включённом editor-mode */}
          <Route element={<EditorRoute fallbackTo="courses" />}>
            <Route path="/courses/:courseId/edit" element={<CourseEditPage />} />
            <Route path="/assignment/:assignmentId/edit" element={<AssignmentEditPage />} />
          </Route>

          {/* админка: только Admin, без зависимости от editor-mode */}
          <Route element={<AdminRoute />}>
            <Route path="/admin/solutions" element={<AdminSolutionsPage />} />
            <Route path="/admin/badges" element={<AdminBadgesPage />} />
            <Route path="/admin/support" element={<AdminSupportPage />} />
            <Route path="/admin/groups" element={<AdminGroupsPage />} />
            <Route path="/admin/feature-roles" element={<AdminFeatureRolesPage />} />
            <Route path="/admin/system-status" element={<AdminSystemStatusPage />} />
            <Route path="/admin/analytics" element={<AdminAnalyticsPage />} />
            <Route path="/admin/users" element={<AdminUsersPage />} />
            <Route path="/admin/minecraft-links" element={<AdminMinecraftLinksPage />} />
            <Route path="/admin/assignments/:assignmentId/insights" element={<AdminAssignmentInsightsPage />} />
          </Route>
        </Route>

        <Route path="*" element={<NotFound />} />
      </Routes>
    </NotifyProvider>
  );
}
