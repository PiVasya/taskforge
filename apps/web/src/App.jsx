import { lazy } from 'react';
import { Routes, Route, Navigate, useLocation } from 'react-router-dom';

import ProtectedRoute from './auth/ProtectedRoute';
import EditorRoute from './auth/EditorRoute';
import AdminRoute from './auth/AdminRoute';
import FeatureRoute from './auth/FeatureRoute';
import RootShell from './app/RootShell';
import PageMeta from './app/PageMeta';

const LandingPage = lazy(() => import('./pages/LandingPage'));
const LoginPage = lazy(() => import('./pages/LoginPage'));
const ForgotPasswordPage = lazy(() => import('./pages/ForgotPasswordPage'));
const RegisterPage = lazy(() => import('./pages/RegisterPage'));
const CoursesPage = lazy(() => import('./pages/CoursesPage'));
const AgentPage = lazy(() => import('./pages/AgentPage'));
const AdminAiAccountManagerPage = lazy(() => import('./pages/admin/AdminAiAccountManagerPage'));
const NewsPage = lazy(() => import('./pages/NewsPage'));
const UpdatePostPage = lazy(() => import('./pages/UpdatePostPage'));
const CourseAssignmentsPage = lazy(() => import('./pages/CourseAssignmentsPage'));
const CourseEditPage = lazy(() => import('./pages/CourseEditPage'));
const AssignmentEditPage = lazy(() => import('./pages/AssignmentEditPage'));
const AssignmentSolvePage = lazy(() => import('./pages/AssignmentSolvePage'));
const AssignmentResultsPage = lazy(() => import('./pages/AssignmentResultsPage'));
const AssignmentImageResultsPage = lazy(() => import('./pages/AssignmentImageResultsPage'));
const AssignmentTopSolutionsPage = lazy(() => import('./pages/AssignmentTopSolutionsPage'));
const ProfilePage = lazy(() => import('./pages/ProfilePage'));
const SettingsPage = lazy(() => import('./pages/SettingsPage'));
const MySolutionsPage = lazy(() => import('./pages/MySolutionsPage'));
const PublicProfilePage = lazy(() => import('./pages/PublicProfilePage'));
const SupportChatPage = lazy(() => import('./pages/SupportChatPage'));
const AdminSupportPage = lazy(() => import('./pages/AdminSupportPage'));
const PrivacyPolicyPage = lazy(() => import('./pages/PrivacyPolicyPage'));
const LeaderboardPage = lazy(() => import('./pages/admin/LeaderboardPage'));
const AdminSolutionsPage = lazy(() => import('./pages/admin/AdminSolutionsPage'));
const AdminBadgesPage = lazy(() => import('./pages/admin/AdminBadgesPage'));
const AdminGroupsPage = lazy(() => import('./pages/admin/AdminGroupsPage'));
const AdminFeatureRolesPage = lazy(() => import('./pages/admin/AdminFeatureRolesPage'));
const AdminSystemStatusPage = lazy(() => import('./pages/admin/AdminSystemStatusPage'));
const AdminUsersPage = lazy(() => import('./pages/admin/AdminUsersPage'));
const AdminUserManagementPage = lazy(() => import('./pages/admin/AdminUserManagementPage'));
const AdminMinecraftLinksPage = lazy(() => import('./pages/admin/AdminMinecraftLinksPage'));
const AdminAssignmentInsightsPage = lazy(() => import('./pages/admin/AdminAssignmentInsightsPage'));
const AdminAnalyticsPage = lazy(() => import('./pages/admin/AdminAnalyticsPage'));
const AdminUserActionsPage = lazy(() => import('./pages/admin/AdminUserActionsPage'));
const MinecraftChatPage = lazy(() => import('./pages/minecraft/MinecraftChatPage'));
const CompilerPage = lazy(() => import('./pages/CompilerPage'));

function NotFound() {
  return <div className="py-10">Страница не найдена</div>;
}

function AdminAiRedirect() {
  const location = useLocation();
  return <Navigate to={`/admin/ai/account-manager${location.search || ''}`} replace />;
}

export default function App() {
  return (
    <>
      <PageMeta />
      <Routes>
        <Route element={<RootShell />}>
          <Route path="/" element={<LandingPage />} />
          <Route path="/login" element={<LoginPage />} />
          <Route path="/forgot-password" element={<ForgotPasswordPage />} />
          <Route path="/register" element={<RegisterPage />} />
          <Route path="/privacy" element={<PrivacyPolicyPage />} />
          <Route path="/news" element={<NewsPage />} />
          <Route path="/news/:postId" element={<UpdatePostPage />} />

          <Route element={<ProtectedRoute />}>
            <Route path="/courses" element={<CoursesPage />} />
            <Route path="/compiler" element={<CompilerPage />} />
            <Route path="/course/:courseId" element={<CourseAssignmentsPage />} />
            <Route path="/assignment/:assignmentId" element={<AssignmentSolvePage />} />
            <Route path="/assignment/:assignmentId/results" element={<AssignmentResultsPage />} />
            <Route path="/assignment/:assignmentId/image-results" element={<AssignmentImageResultsPage />} />
            <Route path="/assignment/:assignmentId/top" element={<AssignmentTopSolutionsPage />} />
            <Route path="/profile" element={<ProfilePage />} />
            <Route path="/settings" element={<SettingsPage />} />
            <Route path="/my/solutions" element={<MySolutionsPage />} />
            <Route path="/leaderboard" element={<LeaderboardPage />} />
            <Route path="/users/:userId" element={<PublicProfilePage />} />
            <Route path="/support" element={<SupportChatPage />} />
            <Route path="/support/new" element={<Navigate to="/support" replace />} />
            <Route path="/support/:ticketId" element={<SupportChatPage />} />

            <Route element={<FeatureRoute requiredRole="Minecraft" fallbackTo="/courses" />}>
              <Route path="/minecraft/chat" element={<MinecraftChatPage />} />
            </Route>

            <Route element={<EditorRoute fallbackTo="courses" />}>
              <Route path="/courses/:courseId/edit" element={<CourseEditPage />} />
              <Route path="/assignment/:assignmentId/edit" element={<AssignmentEditPage />} />
            </Route>

            <Route element={<AdminRoute />}>
              <Route path="/admin/solutions" element={<AdminSolutionsPage />} />
              <Route path="/admin/badges" element={<AdminBadgesPage />} />
              <Route path="/admin/support" element={<AdminSupportPage />} />
              <Route path="/admin/support/:ticketId" element={<SupportChatPage />} />
              <Route path="/admin/groups" element={<AdminGroupsPage />} />
              <Route path="/admin/feature-roles" element={<AdminFeatureRolesPage />} />
              <Route path="/admin/system-status" element={<AdminSystemStatusPage />} />
              <Route path="/admin/ai" element={<Navigate to="/admin/ai/account-manager" replace />} />
              <Route path="/admin/ai/account-manager" element={<AdminAiAccountManagerPage />} />
              <Route path="/admin/ai/assistant" element={<AgentPage />} />
              <Route path="/agent" element={<AdminAiRedirect />} />
              <Route path="/ai" element={<AdminAiRedirect />} />
              <Route path="/admin/analytics" element={<AdminAnalyticsPage />} />
              <Route path="/admin/activity" element={<AdminUserActionsPage />} />
              <Route path="/admin/users" element={<AdminUsersPage />} />
              <Route path="/admin/users/:userId" element={<AdminUserManagementPage />} />
              <Route path="/admin/minecraft-links" element={<AdminMinecraftLinksPage />} />
              <Route path="/admin/assignments/:assignmentId/insights" element={<AdminAssignmentInsightsPage />} />
            </Route>
          </Route>

          <Route path="*" element={<NotFound />} />
        </Route>
      </Routes>
    </>
  );
}
