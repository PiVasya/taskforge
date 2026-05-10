# learning-content-service

Отдельный микросервис для учебного контента: дерево курсов, полноценные конспекты, старые простые страницы и привязки заданий.

## Главное изменение

`LearningPage` больше не считается основным форматом для учебного материала. Для материалов уровня HTML-прототипа добавлена отдельная сущность:

```text
LearningConspect
```

Она хранит именно конспект:

- вкладки (`tabs`);
- заголовочный hero-блок;
- карточки правил;
- предупреждения;
- примеры;
- алгоритмы и чек-листы;
- таблицы;
- словари;
- слова ЦТ/ЦЭ по годам;
- блок перехода к заданиям;
- привязанные кнопки/ссылки на задания через `LearningConspectTaskLink`.

## БД

По умолчанию использует отдельную базу в том же Postgres-контейнере:

```text
taskforge_learning
```

Миграции в этот апдейт НЕ добавлены. Их нужно сгенерировать вручную командами EF Core.

На старте сервис по умолчанию не вызывает `EnsureCreated` и не сидит данные:

```text
Database__EnsureCreated=false
Seed__InitialCatalog=false
```

Для локального одноразового прототипирования можно включить эти флаги, но для нормальной схемы лучше использовать миграции.

## Основные маршруты

```http
GET    /health/live
GET    /health/ready
GET    /api/learning/courses/tree
GET    /api/learning/courses/{slug}/outline
GET    /api/learning/courses/{slug}/conspects
GET    /api/learning/conspects
GET    /api/learning/conspects/{idOrSlug}

POST   /api/admin/learning/courses
PUT    /api/admin/learning/courses/{id}
POST   /api/admin/learning/courses/{courseId}/pages
POST   /api/admin/learning/courses/{courseId}/conspects
PUT    /api/admin/learning/conspects/{id}
POST   /api/admin/learning/conspects/{conspectId}/task-links
DELETE /api/admin/learning/conspect-task-links/{id}
POST   /api/admin/learning/courses/{courseId}/task-links
```

## Формат `ContentJson`

Минимальный пример:

```json
{
  "schemaVersion": 1,
  "layout": "tabs",
  "startTabId": "theory",
  "hero": {
    "eyebrow": "Русский язык · ЦТ/ЦЭ",
    "title": "A1. Гласная в корне слова",
    "description": "Большой конспект по теме.",
    "stats": [{ "label": "Блок", "value": "A1" }]
  },
  "tabs": [
    {
      "id": "theory",
      "title": "Теория",
      "blocks": [
        {
          "id": "core-rule",
          "type": "rule-card",
          "title": "Главные правила",
          "items": ["Правило 1", "Правило 2"]
        }
      ]
    },
    {
      "id": "practice",
      "title": "Тренировка",
      "blocks": [
        {
          "type": "practice-intro",
          "title": "Переход к заданиям",
          "text": "После конспекта открыть задания.",
          "cta": { "label": "Сделать задания", "href": "/tasks?sectionCode=A1" }
        }
      ]
    }
  ]
}
```
