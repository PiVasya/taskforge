# CT/CE conspects mega update

## Что изменено

1. Добавлен отдельный backend-формат `LearningConspect` для настоящих конспектов, а не просто страниц.
2. Добавлены ссылки `LearningConspectTaskLink`, чтобы у конкретного конспекта были кнопки перехода к заданиям.
3. Добавлены публичные API для списка/деталей конспектов.
4. Добавлены admin API для создания/обновления конспектов и привязки заданий.
5. Второй фронт `clientapp-ct` переведён с iframe на полноценный React UI.
6. Добавлена страница `/tasks` для заданий из `quiz-task-service`.
7. Добавлен базовый редактор `/admin/conspects`.
8. Runtime-создание схемы отключено по умолчанию: миграции генерируются вручную.

## Миграции

Файлы миграций не добавлялись.

После распаковки архива нужно самому сгенерировать миграции для новых сущностей `LearningConspect` и `LearningConspectTaskLink`, а также учитывать, что в `LearningCourseOutlineDto` теперь есть коллекция `Conspects`.

## Новые основные файлы

```text
learning-content-service/Data/Entities/LearningConspect.cs
learning-content-service/Data/Entities/LearningConspectTaskLink.cs
learning-content-service/DTO/LearningDtos.cs
learning-content-service/Program.cs
learning-content-service/Services/LearningSeedService.cs
clientapp-ct/src/components/RichConspectRenderer.jsx
clientapp-ct/src/pages/CtTrainerPage.jsx
clientapp-ct/src/pages/QuizTasksPage.jsx
clientapp-ct/src/pages/AdminConspectsPage.jsx
clientapp-ct/src/api/learning.js
```
