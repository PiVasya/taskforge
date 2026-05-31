#!/usr/bin/env bash
set -euo pipefail

MIGRATION_NAME="${1:-SchemaChange_$(date +%Y%m%d_%H%M%S)}"

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

requires_add() {
  local output_file="$1"

  if grep -Eqi 'No changes|No pending|No model changes|up to date' "$output_file"; then
    return 1
  fi

  if grep -Eqi 'Changes have been made|pending model changes|has pending model changes|Model changes were detected' "$output_file"; then
    return 0
  fi

  return 2
}

for item in "${ITEMS[@]}"; do
  IFS="|" read -r PROJECT CONTEXT OUTPUT_DIR <<< "$item"

  echo "========================================"
  echo "CHECK: $CONTEXT"
  echo "PROJECT: $PROJECT"
  echo "========================================"

  dotnet restore "$PROJECT"

  CHECK_LOG="$(mktemp)"
  set +e
  dotnet ef migrations has-pending-model-changes \
    --project "$PROJECT" \
    --startup-project "$PROJECT" \
    --context "$CONTEXT" >"$CHECK_LOG" 2>&1
  CHECK_EXIT=$?
  set -e

  cat "$CHECK_LOG"

  if [ "$CHECK_EXIT" -eq 0 ]; then
    echo "NO CHANGES: $CONTEXT"
    rm -f "$CHECK_LOG"
    continue
  fi

  if requires_add "$CHECK_LOG"; then
    echo "PENDING CHANGES: $CONTEXT"
    echo "ADD MIGRATION: $MIGRATION_NAME -> $OUTPUT_DIR"
    rm -f "$CHECK_LOG"
    dotnet ef migrations add "$MIGRATION_NAME" \
      --project "$PROJECT" \
      --startup-project "$PROJECT" \
      --context "$CONTEXT" \
      --output-dir "$OUTPUT_DIR"
    continue
  fi

  echo "ERROR: could not safely decide whether $CONTEXT has pending model changes." >&2
  echo "dotnet ef exit code: $CHECK_EXIT" >&2
  echo "The output above did not look like a normal pending/no-pending result." >&2
  echo "Use ./scripts/generate-migrations-force.sh only if you intentionally want to force migration generation." >&2
  rm -f "$CHECK_LOG"
  exit "$CHECK_EXIT"
done
