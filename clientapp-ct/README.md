# clientapp-ct — второй фронт TaskForge

Минимальный React-фронт для поддомена CT Macha. Он использует тот же backend TaskForge и те же endpoints авторизации, что и основной `clientapp`.

## Что оставлено

- `LoginPage` — вход через `/api/auth/login`.
- `RegisterPage` — регистрация через `/api/auth/register`.
- `AuthContext` — refresh/logout/access-token логика из основного фронта.
- `ProtectedRoute` — защита главной страницы.
- `PrivacyPolicyPage` — ссылка из формы регистрации.
- `CtTrainerPage` — защищённая страница, которая открывает `public/trainer.html`.

## Локальный запуск

```bash
cd clientapp-ct
npm ci
npm start
```

По умолчанию dev-сервер запускается на `PORT=82`.

## Production

Сборка и runtime повторяют основной фронт:

```bash
npm run build
```

Dockerfile собирает CRA-приложение и отдаёт статические файлы через Caddy на внутреннем порту `80`.
