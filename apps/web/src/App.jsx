import { useEffect, useMemo } from 'react';
import { Routes, Route, Navigate, useLocation, matchPath } from 'react-router-dom';
import { NotifyProvider } from './components/notify/NotifyProvider';

import ProtectedRoute from './auth/ProtectedRoute';
import EditorRoute from './auth/EditorRoute';
import AdminRoute from './auth/AdminRoute';
import FeatureRoute from './auth/FeatureRoute';

import LandingPage from './pages/LandingPage';
import LoginPage from './pages/LoginPage';
import RegisterPage from './pages/RegisterPage';
import CoursesPage from './pages/CoursesPage';
import AgentPage from './pages/AgentPage';
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


import SupportChatPage from './pages/SupportChatPage';
import AdminSupportPage from './pages/AdminSupportPage';

import SupportNotifier from './components/SupportNotifier';


import PrivacyPolicyPage from './pages/PrivacyPolicyPage';


import LeaderboardPage from './pages/admin/LeaderboardPage';
import AdminSolutionsPage from './pages/admin/AdminSolutionsPage';
import AdminBadgesPage from './pages/admin/AdminBadgesPage';
import AdminGroupsPage from './pages/admin/AdminGroupsPage';
import AdminFeatureRolesPage from './pages/admin/AdminFeatureRolesPage';
import AdminSystemStatusPage from './pages/admin/AdminSystemStatusPage';
import AdminUsersPage from './pages/admin/AdminUsersPage';
import AdminUserManagementPage from './pages/admin/AdminUserManagementPage';
import AdminMinecraftLinksPage from './pages/admin/AdminMinecraftLinksPage';
import AdminAssignmentInsightsPage from './pages/admin/AdminAssignmentInsightsPage';
import AdminAnalyticsPage from './pages/admin/AdminAnalyticsPage';
import AdminUserActionsPage from './pages/admin/AdminUserActionsPage';
import MinecraftChatPage from './pages/minecraft/MinecraftChatPage';


const SITE_NAME = 'TaskForge';
const DEFAULT_DESCRIPTION =
  'TaskForge — учебная платформа для задач по программированию: курсы, задания, автопроверка решений, рейтинг и прогресс студентов.';

const pageMetaRules = [
  {
    path: '/',
    title: 'главная',
    description: 'TaskForge — учебная платформа для практики программирования: курсы, задания, автопроверка решений, прогресс и рейтинг.',
  },
  {
    path: '/login',
    title: 'вход',
    description: 'Вход в TaskForge для продолжения обучения, проверки решений и работы с курсами.',
  },
  {
    path: '/register',
    title: 'регистрация',
    description: 'Создание аккаунта TaskForge для доступа к курсам, заданиям и сохранению прогресса.',
  },
  {
    path: '/privacy',
    title: 'политика конфиденциальности',
    description: 'Политика конфиденциальности TaskForge и описание обработки данных пользователей.',
  },
  {
    path: '/news/:postId',
    title: 'обновление',
    description: 'Подробности обновления TaskForge: новые функции, исправления и изменения платформы.',
  },
  {
    path: '/news',
    title: 'лента обновлений',
    description: 'Новости TaskForge: свежие изменения платформы, новые возможности и улучшения интерфейса.',
  },
  {
    path: '/courses/:courseId/edit',
    title: 'редактор курса',
    description: 'Редактирование курса TaskForge: структура, задания, владельцы и настройки обучения.',
  },
  {
    path: '/course/:courseId',
    title: 'курс',
    description: 'Страница курса TaskForge со списком заданий, конспектами и прогрессом прохождения.',
  },
  {
    path: '/courses',
    title: 'курсы',
    description: 'Каталог курсов TaskForge: учебные маршруты, задания, конспекты и прогресс прохождения.',
  },
  {
    path: '/assignment/:assignmentId/edit',
    title: 'редактор задания',
    description: 'Редактирование задания TaskForge: условие, тип проверки, тесты, рейтинг и настройки публикации.',
  },
  {
    path: '/assignment/:assignmentId/results',
    title: 'результаты задания',
    description: 'Результаты отправок по заданию TaskForge: статусы, тесты, ошибки и проверенные решения.',
  },
  {
    path: '/assignment/:assignmentId/image-results',
    title: 'image-test результаты',
    description: 'Результаты image-test задания TaskForge с проверкой изображений и сравнением с эталоном.',
  },
  {
    path: '/assignment/:assignmentId/top',
    title: 'топ решений',
    description: 'Лучшие решения пользователей по выбранному заданию TaskForge.',
  },
  {
    path: '/assignment/:assignmentId',
    title: 'задание',
    description: 'Решение задания TaskForge: условие, редактор кода, отправка ответа и автоматическая проверка.',
  },
  {
    path: '/profile',
    title: 'профиль',
    description: 'Личный профиль TaskForge: прогресс, рейтинг, бейджи и активность пользователя.',
  },
  {
    path: '/users/:userId',
    title: 'профиль пользователя',
    description: 'Публичный профиль пользователя TaskForge с рейтингом, бейджами и учебной активностью.',
  },
  {
    path: '/settings',
    title: 'настройки',
    description: 'Настройки TaskForge: внешний вид, профиль, интеграции, безопасность и параметры решения задач.',
  },
  {
    path: '/my/solutions',
    title: 'мои решения',
    description: 'История решений пользователя в TaskForge: отправки, статусы проверки и результаты заданий.',
  },
  {
    path: '/leaderboard',
    title: 'рейтинг',
    description: 'Рейтинг студентов TaskForge по решённым задачам, баллам и учебной активности.',
  },
  {
    path: '/minecraft/chat',
    title: 'Minecraft чат',
    description: 'Minecraft-интеграция TaskForge: чат и связь учебной платформы с игровым сервером.',
  },
  {
    path: '/support',
    title: 'поддержка',
    description: 'Личный чат с поддержкой TaskForge без технических ID в интерфейсе.',
  },
  {
    path: '/admin/support/:ticketId',
    title: 'админ · чат поддержки',
    description: 'Административный чат поддержки с конкретным пользователем TaskForge.',
  },
  {
    path: '/admin/assignments/:assignmentId/insights',
    title: 'админ · аналитика задания',
    description: 'Административная аналитика TaskForge по выбранному заданию, попыткам и результатам пользователей.',
  },
  {
    path: '/admin/activity',
    title: 'админ · активность',
    description: 'Лента административной активности TaskForge и действия пользователей в системе.',
  },
  {
    path: '/admin/analytics',
    title: 'админ · аналитика',
    description: 'Административная аналитика TaskForge по пользователям, API, заданиям и нагрузке.',
  },
  {
    path: '/admin/users/:userId',
    title: 'админ · управление пользователем',
    description: 'Полное административное управление пользователем TaskForge: профиль, группы, роли, рейтинг и решения.',
  },
  {
    path: '/admin/users',
    title: 'админ · пользователи',
    description: 'Управление пользователями TaskForge, ролями и доступами.',
  },
  {
    path: '/admin/solutions',
    title: 'админ · решения',
    description: 'Административный просмотр и управление решениями пользователей TaskForge.',
  },
  {
    path: '/admin/badges',
    title: 'админ · бейджи',
    description: 'Управление бейджами и достижениями пользователей TaskForge.',
  },
  {
    path: '/admin/support',
    title: 'админ · поддержка',
    description: 'Административные чаты поддержки с пользователями TaskForge.',
  },
  {
    path: '/admin/groups',
    title: 'админ · группы',
    description: 'Управление группами пользователей TaskForge.',
  },
  {
    path: '/admin/feature-roles',
    title: 'админ · роли',
    description: 'Управление дополнительными ролями и доступом к функциям TaskForge.',
  },
  {
    path: '/admin/system-status',
    title: 'админ · статус системы',
    description: 'Статус компонентов TaskForge и техническое состояние платформы.',
  },
  {
    path: '/admin/minecraft-links',
    title: 'админ · Minecraft связи',
    description: 'Администрирование Minecraft-связей пользователей TaskForge.',
  },
  {
    path: '/admin/ai',
    title: 'AI-ассистент',
    description: 'AI-ассистент TaskForge для генерации, проверки и подготовки учебных материалов.',
  },
  {
    path: '/agent',
    title: 'AI-ассистент',
    description: 'AI-ассистент TaskForge для работы с заданиями и материалами.',
  },
  {
    path: '/ai',
    title: 'AI-ассистент',
    description: 'AI-ассистент TaskForge для работы с заданиями и материалами.',
  },
];

function formatPageTitle(pageTitle) {
  if (!pageTitle) return `${SITE_NAME} — платформа задач по программированию`;
  return `${SITE_NAME} — ${pageTitle}`;
}

function resolvePageMeta(pathname) {
  const cleanPath = pathname || '/';
  const rule = pageMetaRules.find((item) =>
    matchPath({ path: item.path, end: true }, cleanPath),
  );

  if (!rule) {
    return {
      title: formatPageTitle('страница не найдена'),
      description: 'Такой страницы в TaskForge нет или ссылка устарела.',
    };
  }

  return {
    title: formatPageTitle(rule.title),
    description: rule.description || DEFAULT_DESCRIPTION,
  };
}

function setMeta(selector, createAttrs, content) {
  if (typeof document === 'undefined') return;

  let tag = document.head.querySelector(selector);
  if (!tag) {
    tag = document.createElement('meta');
    Object.entries(createAttrs).forEach(([name, value]) => {
      tag.setAttribute(name, value);
    });
    document.head.appendChild(tag);
  }

  tag.setAttribute('content', content);
}

function PageMeta() {
  const location = useLocation();
  const meta = useMemo(
    () => resolvePageMeta(location.pathname),
    [location.pathname],
  );

  useEffect(() => {
    document.title = meta.title;

    setMeta('meta[name="description"]', { name: 'description' }, meta.description);
    setMeta('meta[property="og:title"]', { property: 'og:title' }, meta.title);
    setMeta('meta[property="og:description"]', { property: 'og:description' }, meta.description);
    setMeta('meta[name="twitter:title"]', { name: 'twitter:title' }, meta.title);
    setMeta('meta[name="twitter:description"]', { name: 'twitter:description' }, meta.description);

    if (typeof window !== 'undefined') {
      const url = `${window.location.origin}${location.pathname}`;
      setMeta('meta[property="og:url"]', { property: 'og:url' }, url);
    }
  }, [location.pathname, meta.description, meta.title]);

  return null;
}

function NotFound() {
  return <div className="container-app py-10">Страница не найдена</div>;
}

function AdminAiRedirect() {
  const location = useLocation();
  return <Navigate to={`/admin/ai${location.search || ''}`} replace />;
}

export default function App() {
  return (
    <NotifyProvider>
      <PageMeta />
      <SupportNotifier />
      <Routes>
        <Route path="/" element={<LandingPage />} />
        <Route path="/login" element={<LoginPage />} />
        <Route path="/register" element={<RegisterPage />} />
        
        <Route path="/privacy" element={<PrivacyPolicyPage />} />

        <Route element={<ProtectedRoute />}>
          <Route path="/news" element={<NewsPage />} />
          <Route path="/news/:postId" element={<UpdatePostPage />} />

          <Route path="/courses" element={<CoursesPage />} />
          <Route path="/course/:courseId" element={<CourseAssignmentsPage />} />

          
          <Route path="/assignment/:assignmentId" element={<AssignmentSolvePage />} />
          
          <Route path="/assignment/:assignmentId/results" element={<AssignmentResultsPage />} />
          <Route path="/assignment/:assignmentId/image-results" element={<AssignmentImageResultsPage />} />

          
          <Route path="/assignment/:assignmentId/top" element={<AssignmentTopSolutionsPage />} />

          <Route path="/profile" element={<ProfilePage />} />
          <Route path="/settings" element={<SettingsPage />} />
          <Route path="/my/solutions" element={<MySolutionsPage />} />

          <Route element={<FeatureRoute requiredRole="Minecraft" fallbackTo="/courses" />}>
            <Route path="/minecraft/chat" element={<MinecraftChatPage />} />
          </Route>

          
          <Route path="/leaderboard" element={<LeaderboardPage />} />

          
          <Route path="/users/:userId" element={<PublicProfilePage />} />

          
          <Route path="/support" element={<SupportChatPage />} />
          <Route path="/support/new" element={<Navigate to="/support" replace />} />
          <Route path="/support/:ticketId" element={<SupportChatPage />} />

          
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
            <Route path="/admin/ai" element={<AgentPage />} />
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
      </Routes>
    </NotifyProvider>
  );
}
