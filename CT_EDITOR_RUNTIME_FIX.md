# CT editor runtime fix

Исправлен белый экран редактора на `/editor/courses/...`.

Причина:
- `LearningEditorPage` вызывал `setEditorMode(true)` из `useEditorMode()`.
- `EditorModeContext` не отдавал `setEditorMode`, а отдавал только `toggle`.
- В production-сборке это превращалось в ошибку вида `TypeError: n is not a function`.

Дополнительно исправлено:
- `RichConspectRenderer` больше не вызывает `useState` условно после раннего `return` для HTML-конспекта.
- ESLint по `clientapp-ct/src` проходит без ошибок.

Миграции не нужны.
