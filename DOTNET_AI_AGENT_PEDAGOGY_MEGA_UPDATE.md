# TaskForge .NET AI Agent — Pedagogy Mega Update

## Что изменено

Агент больше не должен строить обучающие задания по зашитой лестнице конкретной темы. Новый поток для `assignment_draft_workflow`:

1. **TeacherPreferenceExecutor**
   - извлекает предпочтения преподавателя из сообщения, памяти и контекста;
   - сохраняет их в `memoryPatch.teachingPreferences`;
   - эти правила дальше видят skill map, draft author и critic.

2. **LLM COURSE_SKILL_MAP**
   - отдельный reasoning-этап до генерации заданий;
   - агент сам оценивает курс: что студент умеет после каждого задания, где появляется новый навык, какие bridge-шаги нужны;
   - backend fallback теперь structure-only: он показывает outline курса, но не выбирает anchor и не выводит навыки по regex/ключевым словам.

3. **BridgePlan-first генерация**
   - DraftAuthor больше не должен добавлять свои любимые темы;
   - если `bridgePlan` есть, генерируется один draft на один step;
   - количество заданий берётся из `bridgePlan`, а не фиксированно `5`.

4. **Quality gate**
   - Critic получает `COURSE_SKILL_MAP` и `teacherPreferences`;
   - добавлена детерминированная проверка соответствия draft текущему `bridgePlan`;
   - если карта навыков не построена уверенно, workflow не сохраняет черновики, чтобы не плодить мусор.

5. **Debuggability**
   - `course_skill_map_ready` теперь попадает в artifacts, чтобы можно было видеть: почему агент выбрал точку вставки, какие навыки считает изученными и какие мостики планирует.

## Миграции

Не нужны. Новые данные хранятся в result/memoryPatch/artifacts, схема БД не менялась.
