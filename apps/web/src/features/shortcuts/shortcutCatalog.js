// Physical key codes keep bindings stable when switching keyboard layouts.
const command = (code, label, group, extra = {}) => Object.freeze({ code, label, group, ...extra });
export const SHORTCUTS = Object.freeze({
  'nav.news': command('KeyN', 'Лента', 'navigation', { href: '/news', public: true }),
  'nav.courses': command('KeyC', 'Курсы', 'navigation', { href: '/courses', public: true }),
  'nav.solutions': command('KeyR', 'Мои решения', 'navigation', { href: '/my/solutions' }),
  'nav.leaderboard': command('KeyL', 'Рейтинг', 'navigation', { href: '/leaderboard' }),
  'nav.compiler': command('KeyK', 'Компилятор', 'navigation', { href: '/compiler' }),
  'nav.settings': command('KeyS', 'Настройки', 'navigation', { href: '/settings' }),
  'nav.profile': command('KeyP', 'Профиль', 'navigation', { href: '/settings?section=profile' }),
  'nav.support': command('KeyH', 'Поддержка', 'navigation', { href: '/support' }),
  'nav.minecraft': command('KeyM', 'Minecraft', 'navigation', { href: '/minecraft/chat', role: 'minecraft' }),
  'nav.ai': command('KeyI', 'ИИ: аккаунты', 'admin', { shift: true, href: '/admin/ai/account-manager', role: 'admin' }),
  'nav.analytics': command('KeyA', 'Аналитика', 'admin', { shift: true, href: '/admin/analytics', role: 'admin' }),
  'nav.activity': command('KeyN', 'Действия пользователей', 'admin', { shift: true, href: '/admin/activity', role: 'admin' }),
  'nav.users': command('KeyU', 'Пользователи', 'admin', { shift: true, href: '/admin/users', role: 'admin' }),
  'nav.groups': command('KeyG', 'Группы', 'admin', { shift: true, href: '/admin/groups', role: 'admin' }),
  'nav.adminSolutions': command('KeyR', 'Решения всех участников', 'admin', { shift: true, href: '/admin/solutions', role: 'admin' }),
  'nav.badges': command('KeyB', 'Бейджи', 'admin', { shift: true, href: '/admin/badges', role: 'admin' }),
  'nav.cluster': command('KeyL', 'Кластер', 'admin', { shift: true, href: '/admin/cluster', role: 'admin' }),
  'nav.links': command('KeyM', 'Связи Minecraft', 'admin', { shift: true, href: '/admin/minecraft-links', role: 'admin' }),
  'nav.roles': command('KeyF', 'Дополнительные роли', 'admin', { shift: true, href: '/admin/feature-roles', role: 'admin' }),
  'nav.assistant': command('KeyO', 'ИИ-ассистент', 'admin', { shift: true, href: '/admin/ai/assistant', role: 'admin' }),
  'help': command('Slash', 'Справочник сочетаний', 'navigation', { public: true }),
  'notifications': command('KeyJ', 'Уведомления', 'navigation'),
  'sidebar': command('Backquote', 'Левое меню', 'navigation'),
  'editorMode': command('KeyE', 'Режим редактора', 'editing', { shift: true, role: 'editor' }),
  'primary': command('Enter', 'Отправить решение / запустить код', 'task'),
  'next': command('Period', 'Следующее задание', 'task'),
  'course': command('KeyB', 'К заданиям курса', 'task'),
  'results': command('KeyY', 'Результаты задания', 'task'),
  'edit': command('KeyW', 'Редактировать', 'editing', { shift: true, role: 'editor' }),
  'insights': command('KeyY', 'Аналитика задания', 'editing', { shift: true, role: 'editor' }),
  'save': command('KeyW', 'Сохранить', 'editing'),
  'import': command('KeyI', 'Открыть импорт JSON', 'editing', { role: 'editor' }),
  'export': command('KeyO', 'Открыть экспорт JSON', 'editing', { role: 'editor' }),
  'refresh': command('KeyU', 'Обновить список', 'page'),
  'search': command('KeyQ', 'Поиск на странице', 'page'),
  'filter': command('KeyA', 'Применить фильтры', 'page'),
  'download': command('KeyV', 'Скачать', 'page'),
  'close': command('KeyX', 'Закрыть / остановить', 'page'),
  'fullscreen': command('KeyZ', 'Полный экран', 'compiler'),
  'restart': command('Enter', 'Перезапустить код', 'compiler', { shift: true }),
  'copy': command('KeyV', 'Копировать', 'page', { shift: true }),
  'format': command('KeyF', 'Форматировать JSON', 'editing'),
  'merge': command('Digit8', 'Открыть объединение пары', 'review', { role: 'admin' }),
  'duplicate': command('Digit9', 'Это дубль', 'review', { role: 'admin' }),
  'different': command('Digit0', 'Разные люди', 'review', { role: 'admin' }),
  'ignore': command('Minus', 'Игнорировать пару', 'review', { role: 'admin' }),
  ...Object.fromEntries(Array.from({ length: 9 }, (_, i) => [
    `tab.${i + 1}`, command(`Digit${i + 1}`, `Раздел ${i + 1} текущей страницы`, 'tabs'),
  ])),
});

export function shortcutKeys(id, newTab = false) {
  const item = typeof id === 'string' ? SHORTCUTS[id] : id;
  if (!item) return [];
  const key = ({ Slash: '/', Backquote: '`', Period: '.', Comma: ',', Minus: '-' })[item.code]
    || item.code.replace(/^(Key|Digit)/, '');
  return [...(newTab ? ['Ctrl'] : []), 'Alt', ...(item.shift ? ['Shift'] : []), key];
}

export function shortcutLabel(id, newTab = false) { return shortcutKeys(id, newTab).join('+'); }

export function shortcutForHref(href) {
  if (typeof href !== 'string') return undefined;
  if (href === '/profile') return 'nav.profile';
  if (href === '/admin/support') return 'nav.support';
  const exact = Object.entries(SHORTCUTS).find(([, item]) => item.href === href);
  return exact?.[0];
}

export function shortcutAllowed(item, state) {
  if (!item) return false;
  if (!item.public && !state.authenticated) return false;
  if (item.role === 'admin' && !state.admin) return false;
  if (item.role === 'editor' && !state.editor) return false;
  if (item.role === 'minecraft' && !state.minecraft) return false;
  return true;
}

export function navigationShortcuts(state) {
  return Object.entries(SHORTCUTS).filter(([, item]) => item.href && shortcutAllowed(item, state))
    .map(([id, item]) => ({ ...item, id, href: id === 'nav.support' && state.admin ? '/admin/support' : item.href }));
}
