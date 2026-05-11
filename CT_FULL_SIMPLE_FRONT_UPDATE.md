# CT full simple front update

## Цель

Второй фронт `clientapp-ct` упрощён под обычного ученика:

- главная страница показывает только номера ЦТ/ЦЭ;
- ученик выбирает номер `A1`, `A2`, ..., `A30`, `B1`, ..., `B10`;
- на странице номера сверху показывается опубликованный HTML-конспект;
- под конспектом показываются случайные опубликованные задания с таким же `sectionCode`.

## Пользовательские маршруты

- `/` — сетка всех номеров;
- `/a1`, `/a2`, ..., `/a30` — страницы части A;
- `/b1`, `/b2`, ..., `/b10` — страницы части B.

Маршрут реализован универсально через `/:sectionCode`, а список номеров задаётся в:

```text
clientapp-ct/src/data/ctSections.js
```

Если структура конкретного года/предмета отличается, достаточно изменить `count` у частей `A` и `B`.

## Админский сценарий

Админ / LearningEditor открывает `/editor`.

В редактор добавлен блок **Основа ЦТ**:

- создаёт корень `russian-ct-ce-2026`;
- создаёт разделы `A1-A30` и `B1-B10`;
- если разделы со slug уже есть, но не заполнен `sectionCode`, дозаполняет метаданные.

Для выбранного номера можно:

1. вставить HTML-конспект в блоке конспекта;
2. создать задания в блоке **Задания для A1/B5/...**.

Задание сохраняется через `POST /api/admin/quiz/tasks` и получает:

- `subjectCode: russian`;
- `examCode: ct-ce-2026`;
- `sectionCode: A1/B5/...`;
- `data.options` для выбора ответа;
- `correctAnswer.selected` для выбора ответа или `correctAnswer.value` для краткого ответа;
- `explanation.text` для объяснения после проверки.

## Изменённые файлы

- `clientapp-ct/src/App.jsx`
- `clientapp-ct/src/pages/SimpleHomePage.jsx`
- `clientapp-ct/src/pages/SimpleSectionPage.jsx`
- `clientapp-ct/src/data/ctSections.js`
- `clientapp-ct/src/components/CtStructureBootstrapPanel.jsx`
- `clientapp-ct/src/components/SectionTaskAdminPanel.jsx`
- `clientapp-ct/src/pages/LearningEditorPage.jsx`
