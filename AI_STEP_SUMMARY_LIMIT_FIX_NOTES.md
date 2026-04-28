# AI step summary DB limit fix

Fixed the repeated AI chat crash:

`22001: value too long for type character varying(2000)`

## What changed

- Agent step text fields are now normalized before DB save:
  - kind: 64 chars
  - status: 64 chars
  - actionName: 128 chars
  - title: 240 chars
  - summary: 1900 chars
- The backend normalizes every pending AgentStep before `SaveChangesAsync`, so the fix covers `/steps`, `/complete`, `/fail`, and internal hidden-draft/course-edit steps.
- The external worker also truncates telemetry steps before sending them.
- Worker telemetry step failures are no longer fatal. If `/steps` fails for any reason, the worker logs it and still attempts to complete the real run result.
- Router now recognizes generic requests like:
  - “найди места куда вставить обучающие задачи”
  - “куда добавить обучающие задания”
  - “где вставить обучающие задачи”
  and routes them to a course gap/placement audit instead of plain free chat.

## Migrations

No EF migrations were added.
