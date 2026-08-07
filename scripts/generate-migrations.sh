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
  ./scripts/generate-migrations.sh <MigrationName> <target[,target...]>
  ./scripts/generate-migrations.sh <MigrationName> all
  ./scripts/generate-migrations.sh --list

The target is required intentionally so an unrelated DbContext can never receive a
migration by accident. The script restores/builds only selected projects, checks
pending model changes, and adds a migration only when EF reports a real model change.

Examples:
  ./scripts/generate-migrations.sh AddAiAccountType identity
  ./scripts/generate-migrations.sh AddCourseFields education
  ./scripts/generate-migrations.sh SomeCrossServiceChange identity,education

Use "all" only when you deliberately want to inspect every DB-owning service.
USAGE
}

list_targets() {
  printf '%-16s %-28s %s\n' "TARGET" "DBCONTEXT" "PROJECT"
  printf '%-16s %-28s %s\n' "------" "---------" "-------"
  local item alias project context output_dir
  for item in "${ITEMS[@]}"; do
    IFS='|' read -r alias project context output_dir <<< "$item"
    printf '%-16s %-28s %s\n' "$alias" "$context" "$project"
  done
}

if [[ "${1:-}" == "--list" ]]; then
  list_targets
  exit 0
fi

MIGRATION_NAME="${1:-}"
TARGET_SPEC="${2:-}"

if [[ -z "$MIGRATION_NAME" || -z "$TARGET_SPEC" ]]; then
  usage >&2
  echo >&2
  echo "error: migration name and explicit target are required." >&2
  echo "Run './scripts/generate-migrations.sh --list' to see available targets." >&2
  exit 2
fi

if [[ ! "$MIGRATION_NAME" =~ ^[A-Za-z][A-Za-z0-9_]*$ ]]; then
  echo "error: migration name must match ^[A-Za-z][A-Za-z0-9_]*$." >&2
  exit 2
fi

SELECTED=()
add_target() {
  local requested="$1"
  local item alias project context output_dir matched=0
  for item in "${ITEMS[@]}"; do
    IFS='|' read -r alias project context output_dir <<< "$item"
    if [[ "$requested" == "$alias" || "$requested" == "$context" ]]; then
      SELECTED+=("$item")
      matched=1
      break
    fi
  done
  if [[ "$matched" -eq 0 ]]; then
    echo "error: unknown migration target '$requested'." >&2
    list_targets >&2
    exit 2
  fi
}

if [[ "$TARGET_SPEC" == "all" ]]; then
  SELECTED=("${ITEMS[@]}")
else
  IFS=',' read -ra REQUESTED_TARGETS <<< "$TARGET_SPEC"
  for target in "${REQUESTED_TARGETS[@]}"; do
    target="${target//[[:space:]]/}"
    [[ -n "$target" ]] || continue
    add_target "$target"
  done
fi

if [[ "${#SELECTED[@]}" -eq 0 ]]; then
  echo "error: no migration targets selected." >&2
  exit 2
fi

# Remove duplicate aliases/contexts while preserving order.
DEDUPED=()
declare -A SEEN=()
for item in "${SELECTED[@]}"; do
  IFS='|' read -r alias project context output_dir <<< "$item"
  if [[ -z "${SEEN[$context]:-}" ]]; then
    DEDUPED+=("$item")
    SEEN[$context]=1
  fi
done
SELECTED=("${DEDUPED[@]}")

pending_from_output() {
  local output_file="$1"

  # EF currently uses a non-zero exit code when pending model changes are found.
  # We still inspect output so build/design-time failures are never mistaken for
  # a schema change. Keep both English and Russian phrases because developer
  # machines in this project commonly run a localized .NET CLI.
  if grep -Eqi \
    'Changes have been made|pending model changes|has pending model changes|Model changes were detected|model.*changed|обнаружен[^ ]* изменен|обнаружены изменения|есть изменения модели|модель.*изменен' \
    "$output_file"; then
    return 0
  fi

  return 1
}

for item in "${SELECTED[@]}"; do
  IFS='|' read -r alias project context output_dir <<< "$item"

  echo "========================================"
  echo "TARGET: $alias"
  echo "CONTEXT: $context"
  echo "PROJECT: $project"
  echo "BUILD: $BUILD_CONFIGURATION"
  echo "========================================"

  if [[ ! -f "$project" ]]; then
    echo "error: project file not found: $project" >&2
    exit 2
  fi

  dotnet restore "$project"
  dotnet build "$project" -c "$BUILD_CONFIGURATION" --no-restore

  # A separate design-time probe makes an ordinary startup/configuration error
  # fail before we interpret the pending-model command's non-zero exit code.
  dotnet ef dbcontext info \
    --project "$project" \
    --startup-project "$project" \
    --context "$context" \
    --no-build >/dev/null

  check_log="$(mktemp)"
  set +e
  dotnet ef migrations has-pending-model-changes \
    --project "$project" \
    --startup-project "$project" \
    --context "$context" \
    --no-build >"$check_log" 2>&1
  check_exit=$?
  set -e

  cat "$check_log"

  if [[ "$check_exit" -eq 0 ]]; then
    echo "NO CHANGES: $context"
    rm -f "$check_log"
    continue
  fi

  if ! pending_from_output "$check_log"; then
    echo "error: EF returned exit code $check_exit for $context, but its output did not clearly report pending model changes." >&2
    echo "No migration was generated. Fix the design-time/EF error and rerun the script." >&2
    rm -f "$check_log"
    exit "$check_exit"
  fi

  rm -f "$check_log"
  echo "PENDING CHANGES: $context"
  echo "ADD MIGRATION: $MIGRATION_NAME -> $output_dir"

  dotnet ef migrations add "$MIGRATION_NAME" \
    --project "$project" \
    --startup-project "$project" \
    --context "$context" \
    --output-dir "$output_dir" \
    --no-build

  dotnet build "$project" -c "$BUILD_CONFIGURATION" --no-restore
  echo "DONE: $context"
done
