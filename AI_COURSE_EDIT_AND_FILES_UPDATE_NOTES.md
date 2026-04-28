# AI course editing + file context update

## What changed

- Added a new `course_edit` worker scenario for applying edits to existing assignments instead of only generating reports or drafts.
- The worker now receives `editableAssignments`: a full editable snapshot of the selected course with ids, sort, difficulty, rating, type, statements, code tests, quiz questions and math blocks.
- The worker can return an `assignment_update_batch` artifact. The backend applies it automatically to existing assignments.
- Supported editable assignment types:
  - `code-test`: title, statement, tags, difficulty, rating, allowed languages, tests.
  - `test`: title, statement, tags, difficulty, rating, quiz settings/questions.
  - `math`: title, statement, tags, difficulty, rating, math settings/blocks.
  - `image-test`: title, statement, tags, difficulty, rating, sort and similarity threshold only; no AI generation of image references.
- Added AI-driven course order edits using exact assignment ids.
- Added AI-driven rating normalization. Default policy: difficulty 1 -> rating 10, difficulty 2 -> rating 20, difficulty 3 -> rating 30.
- Added chat file attachments:
  - Frontend file picker in AI chat.
  - Backend upload endpoint for agent conversation attachments.
  - Files are stored under `agent-conversations/{conversationId}` in the existing S3/MinIO storage.
  - Text-like files are read back into `fileContexts` for the worker so they influence AI answers and course edits.
  - Binary/unsupported files remain available as metadata and private links.
- Private file access now allows the owner of the AI conversation to open its uploaded files.

## Notes

- No EF migration was added.
- The previous manual `20260427060000_AiDraftRobustness` migration is not included.
- This update reuses existing `AgentMessage.DataJson` and `AgentRun.RequestJson` to store attachment metadata, so no schema change is required.

## Validation performed in container

- Python worker syntax: `python3 -S -m py_compile` on all worker `.py` files.
- Frontend API syntax: `node --check clientapp/src/api/agent.js`.
- `dotnet build` was not run because the container does not have the .NET SDK installed.
