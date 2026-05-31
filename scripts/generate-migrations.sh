#!/usr/bin/env bash
set -euo pipefail

MIGRATION_NAME="${1:-ProjectLaunch}"

ITEMS=(
  "services/identity/api/TaskForge.Identity.Api.csproj|IdentityDbContext|Migrations"
  "services/education/api/TaskForge.Education.Api.csproj|EducationDbContext|Migrations"
  "services/content/api/learning-content-service.csproj|LearningDbContext|Migrations"
  "services/tasks/assignment-api/TaskForge.Tasks.Api.csproj|TasksDbContext|Migrations"
  "services/tasks/quiz-api/quiz-task-service.csproj|QuizDbContext|Migrations"
  "services/solutions/api/TaskForge.Solutions.Api.csproj|SolutionsDbContext|Migrations"
  "services/execution/api/TaskForge.Execution.Api.csproj|ExecutionDbContext|Migrations"
  "services/ai/api/TaskForge.Ai.Api.csproj|AiDbContext|Migrations"
  "services/support/api/TaskForge.Support.Api.csproj|SupportDbContext|Migrations"
  "services/minecraft/api/TaskForge.Minecraft.Api.csproj|MinecraftDbContext|Migrations"
  "services/files/api/TaskForge.Files.Api.csproj|FilesDbContext|Migrations"
  "services/notifications/api/TaskForge.Notifications.Api.csproj|NotificationsDbContext|Migrations"
  "services/observability/api/TaskForge.Observability.Api.csproj|ObservabilityDbContext|Migrations"
  "services/bots/telegram-quiz-bot/TelegramQuizBot.csproj|TelegramQuizDbContext|Data/Migrations"
)

for item in "${ITEMS[@]}"; do
  IFS="|" read -r PROJECT CONTEXT OUTPUT_DIR <<< "$item"

  echo "========================================"
  echo "RESTORE: $PROJECT"
  echo "========================================"
  dotnet restore "$PROJECT"

  echo "========================================"
  echo "MIGRATION: $CONTEXT -> $OUTPUT_DIR"
  echo "========================================"
  dotnet ef migrations add "$MIGRATION_NAME" \
    --project "$PROJECT" \
    --startup-project "$PROJECT" \
    --context "$CONTEXT" \
    --output-dir "$OUTPUT_DIR"
done
