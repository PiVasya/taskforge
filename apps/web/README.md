# clientapp — фронтенд TaskForge

React + Tailwind + Monaco. Это SPA для задачника: авторизация, курсы, задания, редактор кода, прогон публичных тестов, сабмит решения, рейтинги.

## Быстрый старт (локально)
```bash
cd clientapp
npm i
# CRA dev-server с прокси на http://localhost:8080 (см. package.json "proxy")
npm start
```

По умолчанию все запросы идут на `/api/*` через прокси Nginx (или напрямую к API на :8080 в dev).

## Окружение
- `.env` — дополнительные переменные окружения (опционально).
- `localStorage.token` — JWT токен после логина. Интерсептор автоматически подставляет `Authorization: Bearer ...`.

## Скрипты
- `npm start` — dev сервер.
- `npm run build` — production сборка (`build/`).

## Структура
```text
clientapp/
  src/
    api/           # HTTP-клиенты (axios)
    pages/         # Страницы (Login, Courses, AssignmentEdit/Solve, Admin, Leaderboard)
    components/    # UI-компоненты (Monaco editor, таблицы, формы)
    contexts/      # Контексты (напр. EditorModeContext)
    styles/        # Tailwind/глобальные стили
```

## Ключевые API-клиенты
- `src/api/auth.js`: `login`, `register` → `/api/auth/login`, `/api/auth/register`
- `src/api/profile.js`: `getProfile`, `updateProfile` → `/api/profile` (GET/PUT)
- `src/api/courses.js`: CRUD курсов → `/api/courses`
- `src/api/assignments.js`: CRUD заданий → `/api/courses/{courseId}/assignments`, `/api/assignments/{id}`
- `src/api/compiler.js`: локальный прогон → `/api/compiler/compile-run`, `/api/compiler/run-tests`
- `src/api/solutions.js`: сабмит решения → `/api/assignments/{id}/submit`
- `src/api/leaderboard.js`: лидерборд → `/api/admin/leaderboard` (админ) и публичные эндпоинты
- `src/api/admin.js`: выборки по пользователям/решениям (админ)

## Роли и доступ
- Доступ к редактору заданий и курсов — у владельца курса или `Admin`.
- Аноним может просматривать публичные курсы/задания (если включено).

## Разработка с бэкендом
- В dev используем `package.json.proxy` → `http://localhost:8080`.
- Для собственного API URL можно задать `axios` базовый `baseURL` в `src/api/http.js`.

## Известные моменты
- Monaco/ввод: для задач на ввод пустая строка автоматически отправляется раннерам, чтобы `ReadLine()/input()` не падали.
- Сравнение вывода на сервере нормализует переводы строк и обрезает хвостовые пробелы.
