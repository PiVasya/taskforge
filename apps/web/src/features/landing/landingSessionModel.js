export function getLandingSessionCta({ ready, access }) {
  if (!ready) {
    return {
      pending: true,
      authenticated: false,
      primary: null,
      secondary: null,
      final: {
        kicker: 'TaskForge',
        title: 'Проверяем вашу сессию',
        text: 'Определяем состояние входа, чтобы показать правильное продолжение.',
      },
    };
  }

  if (access) {
    return {
      pending: false,
      authenticated: true,
      primary: { href: '/courses', text: 'Открыть курсы' },
      secondary: { href: '/news', text: 'Новости' },
      final: {
        kicker: 'Продолжить',
        title: 'Продолжить обучение?',
        text: 'Открой курсы, выбери доступный маршрут и переходи к следующему заданию.',
      },
    };
  }

  return {
    pending: false,
    authenticated: false,
    primary: { href: '/register', text: 'Начать обучение' },
    secondary: { href: '/login', text: 'Войти' },
    final: {
      kicker: 'Старт',
      title: 'Готов начать обучение?',
      text: 'Создай аккаунт, выбери курс и переходи к первому заданию.',
    },
  };
}
