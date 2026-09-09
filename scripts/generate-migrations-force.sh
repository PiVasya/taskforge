#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR="$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

BUILD_CONFIGURATION="${DOTNET_BUILD_CONFIGURATION:-Debug}"

ITEMS=(
  "identity|services/identity/api/TaskForge.Identity.Api.csproj|IdentityDbContext|Migrations"
  "education|services/education/api/TaskForge.Education.Api.csproj|EducationDbContext|Migrations"
  "content|services/content/api/learning-content-service.csproj|LearningDbContext|Migrations"
  "assignment|services/tasks/assignment-api/TaskForge.Tasks.Api.csproj|TasksDbContext|Migrations"
  "quiz|services/tasks/quiz-api/quiz-task-service.csproj|QuizDbContext|Migrations"
  "solutions|services/solutions/api/TaskForge.Solutions.Api.csproj|SolutionsDbContext|Migrations"
  "execution|services/execution/api/TaskForge.Execution.Api.csproj|ExecutionDbContext|Migrations"
  "ai|services/ai/api/TaskForge.Ai.Api.csproj|AiDbContext|Migrations"
  "support|services/support/api/TaskForge.Support.Api.csproj|SupportDbContext|Migrations"
  "minecraft|services/minecraft/api/TaskForge.Minecraft.Api.csproj|MinecraftDbContext|Migrations"
  "files|services/files/api/TaskForge.Files.Api.csproj|FilesDbContext|Migrations"
  "notifications|services/notifications/api/TaskForge.Notifications.Api.csproj|NotificationsDbContext|Migrations"
  "observability|services/observability/api/TaskForge.Observability.Api.csproj|ObservabilityDbContext|Migrations"
  "telegram-quiz|services/bots/telegram-quiz-bot/TelegramQuizBot.csproj|TelegramQuizDbContext|Data/Migrations"
)

usage() {
  cat <<'USAGE'
Usage:
  ./scripts/generate-migrations-force.sh <MigrationName> <target[,target...]>
  ./scripts/generate-migrations-force.sh <MigrationName> all

This command intentionally skips the pending-model guard and may create empty
migrations. An explicit target is required so force mode cannot touch every
DbContext accidentally.
USAGE
}

MIGRATION_NAME="${1:-}"
TARGET_SPEC="${2:-}"
if [[ -z "$MIGRATION_NAME" || -z "$TARGET_SPEC" ]]; then
  usage >&2
  exit 2
fi
if [[ ! "$MIGRATION_NAME" =~ ^[A-Za-z][A-Za-z0-9_]*$ ]]; then
  echo "error: invalid migration name '$MIGRATION_NAME'." >&2
  exit 2
fi

SELECTED=()
add_target() {
  local requested="$1"
  local item alias project context output_dir
  for item in "${ITEMS[@]}"; do
    IFS='|' read -r alias project context output_dir <<< "$item"
    if [[ "$requested" == "$alias" || "$requested" == "$context" ]]; then
      SELECTED+=("$item")
      return
    fi
  done
  echo "error: unknown migration target '$requested'." >&2
  exit 2
}

if [[ "$TARGET_SPEC" == "all" ]]; then
  SELECTED=("${ITEMS[@]}")
else
  IFS=',' read -ra REQUESTED_TARGETS <<< "$TARGET_SPEC"
  for target in "${REQUESTED_TARGETS[@]}"; do
    target="${target//[[:space:]]/}"
    [[ -n "$target" ]] && add_target "$target"
  done
fi

[[ "${#SELECTED[@]}" -gt 0 ]] || { echo "error: no migration targets selected." >&2; exit 2; }

declare -A SEEN=()
for item in "${SELECTED[@]}"; do
  IFS='|' read -r alias project context output_dir <<< "$item"
  [[ -z "${SEEN[$context]:-}" ]] || continue
  SEEN[$context]=1

  echo "========================================"
  echo "FORCE TARGET: $alias"
  echo "CONTEXT: $context"
  echo "PROJECT: $project"
  echo "========================================"

  dotnet restore "$project"
  dotnet build "$project" -c "$BUILD_CONFIGURATION" --no-restore
  dotnet ef dbcontext info \
    --project "$project" \
    --startup-project "$project" \
    --context "$context" \
    --no-build >/dev/null

  dotnet ef migrations add "$MIGRATION_NAME" \
    --project "$project" \
    --startup-project "$project" \
    --context "$context" \
    --output-dir "$output_dir" \
    --no-build

  dotnet build "$project" -c "$BUILD_CONFIGURATION" --no-restore
done
