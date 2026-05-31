# database-splitter

Это не runtime-сервис, а миграционный инструмент для будущего разрезания старой монолитной БД.

Сейчас миграции не генерируются. Инструмент должен переносить данные идемпотентно:

- `taskforge.Users` -> `taskforge_identity.Users`
- `taskforge.Courses`, `CourseOwners`, `CourseVisibleGroups`, `UserGroups` -> `taskforge_education`
- `taskforge.TaskAssignments`, `TaskTest*`, `TaskMath*` -> `taskforge_tasks`
- `taskforge.UserTaskSolutions`, `UserImageTaskSolutions`, `UserQuotaBuckets`, `Badge*` -> `taskforge_solutions`
- `taskforge.Agent*` -> `taskforge_ai`
- `taskforge.Support*` -> `taskforge_support`
- `taskforge.Minecraft*` -> `taskforge_minecraft`
- file metadata -> `taskforge_files`
- logs -> `taskforge_observability`

Обязательное правило: повторный запуск не должен создавать дубли.
