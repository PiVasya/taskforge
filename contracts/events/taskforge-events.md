# TaskForge domain events

Минимальный набор событий для настоящей микросервисной схемы.

## identity
- `UserRegistered`
- `UserRoleChanged`
- `UserProfileUpdated`

## tasks
- `TaskCreated`
- `TaskUpdated`
- `TaskPublished`
- `TaskArchived`

## solutions
- `SubmissionCreated`
- `SolutionVerdictChanged`
- `RatingProjectionUpdated`

## execution
- `ExecutionRequested`
- `ExecutionStarted`
- `ExecutionCompleted`
- `ExecutionFailed`

## ai
- `AiRunCreated`
- `AiRunStepAppended`
- `AiRunCompleted`
- `AiTaskDraftCreated`
- `AiConspectDraftCreated`

## support
- `SupportTicketCreated`
- `SupportMessageCreated`

## minecraft
- `MinecraftAccountLinked`
- `MinecraftChatMessageReceived`

## files
- `FileUploaded`
- `FileDeleted`

## notifications
- `NotificationRequested`
- `NotificationDelivered`
- `NotificationFailed`
