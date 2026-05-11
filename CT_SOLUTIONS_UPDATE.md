# CT solutions and weighted task update

## What changed

- Added the student-facing **Решения** button under the HTML conspect task block.
- Added `/api/quiz/me/solutions` to return the user's latest answer per task, correctness and explanation.
- Reworked attempts so that answering the same task again deletes the previous saved attempt for that user and task.
- Updated progress to represent the latest answer state: correct tasks are `solved`, wrong tasks are not solved.
- Weighted random task selection under the conspect:
  - never attempted tasks have the highest chance;
  - attempted but wrong tasks have the next chance;
  - correctly solved tasks have the lowest chance.
- Made explanation required when creating a task from the CT section editor.

## Database

No migration is required. The update uses existing tables:

- `Tasks`
- `TaskVersions`
- `Attempts`
- `Progress`

The old attempt deletion uses existing columns: `UserId` and `TaskId`.

## Main touched files

- `quiz-task-service/Program.cs`
- `quiz-task-service/DTO/QuizDtos.cs`
- `clientapp-ct/src/api/quiz.js`
- `clientapp-ct/src/pages/SimpleSectionPage.jsx`
- `clientapp-ct/src/components/SectionTaskAdminPanel.jsx`
