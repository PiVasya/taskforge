import { useLayoutEffect, useMemo } from 'react';
import { matchPath, useLocation } from 'react-router-dom';

const SITE_NAME = 'TaskForge.by';
const CANONICAL_ORIGIN = 'https://taskforge.by';
const INDEXABLE_ROUTES = ['/', '/privacy', '/news', '/news/:postId'];
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
    path: '/forgot-password',
    title: 'восстановление пароля',
    description: 'Восстановление доступа к аккаунту TaskForge через привязанный Telegram.',
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
    description: 'Лента крупных обновлений TaskForge: изменения платформы, новые возможности и запуск Minecraft-сервера.',
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
    path: '/compiler',
    title: 'онлайн-компилятор',
    description: 'Онлайн-компилятор TaskForge с редактором кода и интерактивной консолью для C++, C#, Java, Python и Pascal.',
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
    description: 'Полное управление аккаунтом TaskForge: профиль, роли, группы, Telegram, Minecraft, блокировка, объединение и безопасное удаление.',
  },
  {
    path: '/admin/users',
    title: 'админ · пользователи',
    description: 'Актуальный реестр пользователей TaskForge с состояниями аккаунтов, Telegram и всеми Minecraft-профилями.',
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
    description: 'Администрирование всех Minecraft-профилей пользователей TaskForge, UUID, баланса и отвязки.',
  },
  {
    path: '/admin/ai/assistant',
    title: 'админ · AI-ассистент',
    description: 'AI-ассистент TaskForge для генерации, проверки и подготовки учебных материалов.',
  },
  {
    path: '/admin/ai/account-manager',
    title: 'админ · менеджер аккаунтов',
    description: 'Менеджер аккаунтов TaskForge: поиск дублей, твинков и подозрительных регистраций с объяснимыми уликами.',
  },
  {
    path: '/admin/ai',
    title: 'админ · ИИ',
    description: 'Инструменты ИИ TaskForge для администрирования и анализа платформы.',
  },
  {
    path: '/agent',
    title: 'админ · менеджер аккаунтов',
    description: 'Менеджер аккаунтов TaskForge для поиска дублей и подозрительных регистраций.',
  },
  {
    path: '/ai',
    title: 'админ · менеджер аккаунтов',
    description: 'Менеджер аккаунтов TaskForge для поиска дублей и подозрительных регистраций.',
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

function isIndexablePath(pathname) {
  return INDEXABLE_ROUTES.some((path) =>
    matchPath({ path, end: true }, pathname || '/'),
  );
}

function setLink(selector, rel, href) {
  if (typeof document === 'undefined') return;
  let tag = document.head.querySelector(selector);
  if (!tag) {
    tag = document.createElement('link');
    tag.setAttribute('rel', rel);
    document.head.appendChild(tag);
  }
  tag.setAttribute('href', href);
}

export default function PageMeta() {
  const location = useLocation();
  const meta = useMemo(
    () => resolvePageMeta(location.pathname),
    [location.pathname],
  );

  useLayoutEffect(() => {
    const pathname = location.pathname || '/';
    const canonicalUrl = `${CANONICAL_ORIGIN}${pathname}`;
    const indexable = isIndexablePath(pathname);
    const html = document.documentElement;

    html.lang = 'ru';
    html.dataset.taskforgeReady = 'loading';
    html.dataset.taskforgeRoute = pathname;
    document.title = meta.title;

    setMeta('meta[name="description"]', { name: 'description' }, meta.description);
    setMeta('meta[name="robots"]', { name: 'robots' }, indexable ? 'index,follow' : 'noindex,nofollow');
    setMeta('meta[property="og:site_name"]', { property: 'og:site_name' }, SITE_NAME);
    setMeta('meta[property="og:title"]', { property: 'og:title' }, meta.title);
    setMeta('meta[property="og:description"]', { property: 'og:description' }, meta.description);
    setMeta('meta[property="og:url"]', { property: 'og:url' }, canonicalUrl);
    setMeta('meta[name="twitter:title"]', { name: 'twitter:title' }, meta.title);
    setMeta('meta[name="twitter:description"]', { name: 'twitter:description' }, meta.description);
    setLink('link[rel="canonical"]', 'canonical', canonicalUrl);

  }, [location.pathname, meta.description, meta.title]);

  return null;
}
