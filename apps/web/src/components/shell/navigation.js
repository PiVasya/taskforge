import { useMemo } from 'react';
import { useLocation } from 'react-router-dom';
import {
  Activity,
  Award,
  BarChart2,
  Bot,
  GraduationCap,
  House,
  LifeBuoy,
  Link2,
  ListOrdered,
  MessageSquare,
  Shield,
  Trophy,
  Terminal,
  UserCog,
  Users,
} from 'lucide-react';
import { useAuth } from '../../auth/AuthContext';
import { useEditorMode } from '../../contexts/EditorModeContext';

function isPathActive(currentPath, href) {
  return currentPath === href || currentPath.startsWith(`${href}/`);
}

function titleForPath(pathname) {
  if (
    pathname.startsWith('/admin/ai') ||
    pathname.startsWith('/ai') ||
    pathname.startsWith('/agent')
  ) return 'Админ AI';
  if (pathname.startsWith('/admin/')) return 'Админ-панель';
  if (pathname.startsWith('/minecraft/')) return 'Minecraft';
  if (pathname.startsWith('/support')) return 'Поддержка';
  if (pathname.startsWith('/course') || pathname.startsWith('/courses')) return 'Курсы';
  if (pathname.startsWith('/assignment/')) return 'Задание';
  if (pathname.startsWith('/leaderboard')) return 'Рейтинг';
  if (pathname.startsWith('/compiler')) return 'Компилятор';
  if (pathname.startsWith('/settings')) return 'Настройки';
  if (pathname.startsWith('/profile')) return 'Профиль';
  if (pathname.startsWith('/my/solutions')) return 'Мои решения';
  if (pathname.startsWith('/news')) return 'Лента';
  if (pathname === '/') return 'Главная';
  return 'TaskForge';
}

export function useShellNavigation() {
  const { access } = useAuth();
  const { isAdmin, hasRole } = useEditorMode();
  const { pathname } = useLocation();
  const canUseMinecraft = isAdmin || hasRole('Minecraft');
  const supportHref = isAdmin ? '/admin/support' : '/support';

  return useMemo(() => {
    const active = (href) => isPathActive(pathname, href);

    const primaryNav = [
      {
        to: '/news',
        label: 'Лента',
        subtitle: 'Главные обновления',
        icon: House,
        active: active('/news'),
      },
      {
        to: '/courses',
        label: 'Курсы',
        subtitle: 'Каталог заданий',
        icon: GraduationCap,
        active: active('/courses') || active('/course'),
      },
      access && {
        to: '/my/solutions',
        label: 'Мои решения',
        subtitle: 'История отправок',
        icon: ListOrdered,
        active: active('/my/solutions'),
      },
      access && {
        to: '/compiler',
        label: 'Компилятор',
        subtitle: 'Код и живая консоль',
        icon: Terminal,
        active: active('/compiler'),
      },
      access && {
        to: '/leaderboard',
        label: 'Рейтинг',
        subtitle: 'Топ студентов',
        icon: Trophy,
        active: active('/leaderboard'),
      },
      access && {
        to: supportHref,
        label: 'Поддержка',
        subtitle: isAdmin ? 'Тикеты пользователей' : 'Мои обращения',
        icon: LifeBuoy,
        active: active(supportHref),
      },
      access && canUseMinecraft && {
        to: '/minecraft/chat',
        label: 'Minecraft',
        subtitle: 'Игровой чат',
        icon: MessageSquare,
        active: active('/minecraft/chat'),
      },
    ].filter(Boolean);

    const adminPrimaryNav = isAdmin
      ? [
          {
            to: '/admin/ai',
            label: 'AI-ассистент',
            subtitle: 'Курсы, аудит, генерация',
            icon: Bot,
            active: active('/admin/ai') || active('/ai') || active('/agent'),
          },
          {
            to: '/admin/analytics',
            label: 'Аналитика',
            subtitle: 'Сводки и графики',
            icon: BarChart2,
            active: active('/admin/analytics'),
          },
          {
            to: '/admin/activity',
            label: 'Действия',
            subtitle: 'Логи пользователей',
            icon: Activity,
            active: active('/admin/activity'),
          },
          {
            to: '/admin/users',
            label: 'Пользователи',
            subtitle: 'Профили и роли',
            icon: UserCog,
            active: active('/admin/users'),
          },
          {
            to: '/admin/groups',
            label: 'Группы',
            subtitle: 'Команды и потоки',
            icon: Users,
            active: active('/admin/groups'),
          },
          {
            to: '/admin/solutions',
            label: 'Решения',
            subtitle: 'Проверки и статусы',
            icon: ListOrdered,
            active: active('/admin/solutions'),
          },
          {
            to: '/admin/badges',
            label: 'Бейджи',
            subtitle: 'Награды и витрина',
            icon: Award,
            active: active('/admin/badges'),
          },
        ]
      : [];

    const adminSecondaryNav = isAdmin
      ? [
          {
            to: '/admin/minecraft-links',
            label: 'Связи Minecraft',
            subtitle: 'Привязки игроков',
            icon: Link2,
            active: active('/admin/minecraft-links'),
          },
          {
            to: '/admin/system-status',
            label: 'Статус',
            subtitle: 'Компоненты и раннеры',
            icon: Activity,
            active: active('/admin/system-status'),
          },
          {
            to: '/admin/feature-roles',
            label: 'Доп. роли',
            subtitle: 'Права и фичи',
            icon: Shield,
            active: active('/admin/feature-roles'),
          },
        ]
      : [];

    return {
      pathname,
      currentViewTitle: titleForPath(pathname),
      isAdminArea: isAdmin && (pathname === '/admin' || pathname.startsWith('/admin/')),
      supportHref,
      primaryNav,
      adminPrimaryNav,
      adminSecondaryNav,
      adminNav: [...adminPrimaryNav, ...adminSecondaryNav],
      isActive: active,
    };
  }, [access, canUseMinecraft, isAdmin, pathname, supportHref]);
}
