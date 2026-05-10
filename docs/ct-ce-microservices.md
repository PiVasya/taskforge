# CT/CE learning microservices plan

## Решение

Новые сущности для ЦТ/ЦЭ больше не добавляются в основной `ApplicationDbContext`.

На этом шаге добавлены два микросервиса и две отдельные базы в том же Postgres-контейнере:

```text
taskforge_learning  -> learning-content-service
taskforge_quiz      -> quiz-task-service
```

`compose/infra.yaml` содержит сервис `db-init`, который создаёт эти базы, если их ещё нет.

## Дерево обучения

Структура поддерживает курс внутри курса:

```text
Русский язык
  └─ ЦТ / ЦЭ 2026
      ├─ A1
      ├─ A2
      ├─ ...
      ├─ B10
      └─ B11
```

В каждый узел дерева можно добавлять:

- дочерние курсы;
- страницы-конспекты;
- ссылки на задания.

То есть можно открыть курс `A1`, сначала увидеть конспект, потом открыть задачки.

## learning-content-service

Отвечает только за структуру обучения:

- `LearningCourse` — курс/раздел/подраздел;
- `LearningPage` — конспект, теория, словарь, заметка;
- `LearningCourseTaskLink` — привязка задания к любому курсу.

Публичные endpoints:

```http
GET /api/learning/courses/tree
GET /api/learning/courses/{slug}/outline
```

Админские endpoints:

```http
POST /api/admin/learning/courses
PUT  /api/admin/learning/courses/{id}
POST /api/admin/learning/courses/{courseId}/pages
POST /api/admin/learning/courses/{courseId}/task-links
```

## quiz-task-service

Отвечает за мини-задачи и тесты:

- A1/A2/B10/B11;
- выбор буквы;
- один вариант;
- несколько вариантов;
- ввод текста;
- будущие ЦТ/ЦЭ наборы по годам.

Публичные endpoints:

```http
GET  /api/quiz/tasks?sectionCode=A1
GET  /api/quiz/tasks/{idOrSlug}
POST /api/quiz/tasks/{id}/attempts
GET  /api/quiz/me/progress
```

Админский endpoint:

```http
POST /api/admin/quiz/tasks
```

## Важное правило

`quiz-task-service` хранит `UserId` из JWT без FK на таблицу Users. Это нормальная граница микросервиса.

## Следующие шаги

1. Перевести `clientapp-ct` с iframe/HTML на `GET /api/learning/courses/tree` и `GET /api/quiz/tasks`.
2. Добавить `task-gateway-service`, когда появятся code/image задачи.
3. Вынести `media-service` для картинок и файлов.
4. Вынести `code-task-service` поверх существующих runner-сервисов.
