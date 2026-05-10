# CT front learning-tree fix

Этот апдейт исправляет ошибочную логику, где второй фронт показывал старые курсы программирования TaskForge.

## Как теперь работает `clientapp-ct`

Второй фронт `ct-masha.taskforge.by` больше не ходит в старые endpoint'ы основного TaskForge:

- не использует `/api/courses`;
- не показывает старые кодовые курсы;
- не работает с `Course -> Assignment -> Code solution`.

Теперь фронт строится только от новой учебной модели:

```text
LearningCourse
  -> дочерние LearningCourse
  -> LearningConspect / LearningPage
  -> ссылки на задания

QuizTask
  -> мини-задания ЦТ/ЦЭ
  -> попытки
  -> прогресс
```

## Новая навигация

```text
/
  Учебные курсы ЦТ/ЦЭ

/courses/:courseSlug
  Карточка предмета / экзамена / раздела

/courses/:courseSlug/conspects/:slug
  Конкретный конспект внутри раздела

/courses/:courseSlug/tasks
  Мини-задания для выбранного раздела

/admin/conspects
  Редактор конспектов
```

Ожидаемая структура после seed:

```text
Русский язык
  -> ЦТ / ЦЭ 2026
    -> A1. Орфография
      -> конспект
      -> задания
    -> A2
    -> A3
    -> ...
    -> B11
```

## Backend fixes

1. Исправлен `GET /api/learning/courses/tree`.

Раньше endpoint мог падать с:

```text
Value cannot be null. (Parameter 'key')
```

Причина: дерево группировалось через nullable `ParentCourseId` как ключ словаря. Теперь корневые курсы и дочерние курсы собираются отдельно.

2. `LearningCourseTreeDto` теперь возвращает больше полей, нужных фронту:

```text
Description
SubjectCode
ExamCode
SectionCode
```

3. Убрана явная ссылка на `Microsoft.IdentityModel.Tokens 8.14.0`, чтобы не ловить runtime mismatch с JWT в контейнере.

## Compose

В `compose/core.yaml` включены seed-флаги:

```yaml
Seed__InitialCatalog: 'true'
Seed__A1Samples: 'true'
```

Миграции не добавлялись и не генерировались.
