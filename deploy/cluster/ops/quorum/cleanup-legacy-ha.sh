#!/usr/bin/env bash
set -Eeuo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)/lib/common.sh"

cluster_need_root

if cluster_is_enabled; then
  cluster_die "N-node cluster mode is already enabled; legacy cleanup must run before cluster bootstrap"
fi

stamp="$(date -u +%Y%m%d-%H%M%S)"
backup_dir="$TASKFORGE_ROOT/.runtime/legacy-ha/$stamp"
mkdir -p "$backup_dir"
chmod 700 "$TASKFORGE_ROOT/.runtime/legacy-ha" "$backup_dir"

legacy_units=(
  taskforge-ha.service
  taskforge-ha-state-sync.service
  taskforge-ha-state-sync.timer
  taskforge-ha-prefetch.service
  taskforge-ha-prefetch.timer
)

if command -v systemctl >/dev/null 2>&1; then
  cluster_info "stopping legacy two-node HA services, if they exist"
  for unit in "${legacy_units[@]}"; do
    unit_path="/etc/systemd/system/$unit"
    if systemctl cat "$unit" >/dev/null 2>&1 || systemctl is-active --quiet "$unit" 2>/dev/null; then
      systemctl disable --now "$unit" >/dev/null 2>&1 || cluster_die "failed to stop legacy unit $unit"
      for _ in $(seq 1 50); do
        systemctl is-active --quiet "$unit" 2>/dev/null || break
        sleep 0.2
      done
      systemctl is-active --quiet "$unit" 2>/dev/null && cluster_die "legacy unit $unit is still active; refusing to delete its unit file"
    fi
    if [ -f "$unit_path" ]; then
      cp -p "$unit_path" "$backup_dir/$unit"
      rm -f "$unit_path"
    fi
  done
  systemctl daemon-reload
  systemctl reset-failed "${legacy_units[@]}" >/dev/null 2>&1 || true
fi

# When a new archive is unpacked over an old bundle, tar does not delete the
# old `ha/` directory. Preserve it outside the live bundle so those scripts can
# never be started accidentally.
if [ -d "$TASKFORGE_ROOT/ha" ]; then
  cluster_info "archiving the legacy ha/ directory"
  mv "$TASKFORGE_ROOT/ha" "$backup_dir/ha"
fi

# Preserve the old two-node runtime state. The N-node controller uses
# .runtime/cluster and never reuses these files.
legacy_runtime="$TASKFORGE_ROOT/.runtime/ha"
if [ -d "$legacy_runtime" ]; then
  cluster_info "archiving legacy runtime HA state"
  mv "$legacy_runtime" "$backup_dir/runtime-ha"
fi
cluster_runtime_prepare

# v25/v26 wrote a long list of HA_* variables into .env. The N-node cluster
# keeps topology in cluster/cluster.json and local identity in .runtime, so
# these values are obsolete. Back up .env first and remove only known legacy
# HA keys; all application secrets and ordinary TaskForge settings remain.
if [ -f "$TASKFORGE_ENV_FILE" ]; then
  cp -p "$TASKFORGE_ENV_FILE" "$backup_dir/env.before-legacy-ha-cleanup"
  python3 - "$TASKFORGE_ENV_FILE" <<'PY'
from pathlib import Path
import os
import re
import sys

path = Path(sys.argv[1])
raw = path.read_text(encoding="utf-8-sig").splitlines()
legacy_exact = {
    "POSTGRES_MAX_WAL_SENDERS",
    "POSTGRES_RESTART_POLICY",
    "POSTGRES_WAL_KEEP_SIZE",
    "POSTGRES_WAL_LOG_HINTS",
}
assignment = re.compile(r"^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=")
kept: list[str] = []
removed: list[str] = []
for line in raw:
    match = assignment.match(line)
    if match:
        key = match.group(1)
        if key.startswith("HA_") or key in legacy_exact:
            removed.append(key)
            continue
    kept.append(line)

tmp = path.with_suffix(path.suffix + ".legacy-ha-cleanup.tmp")
tmp.write_text("\n".join(kept).rstrip() + "\n", encoding="utf-8")
os.chmod(tmp, path.stat().st_mode & 0o777)
tmp.replace(path)
print(f"removed legacy .env assignments: {len(removed)}")
if removed:
    print("  " + ", ".join(sorted(set(removed))))
PY
  chown "$(cluster_project_owner)" "$TASKFORGE_ENV_FILE"
fi

cluster_info "legacy two-node HA cleanup complete"
echo "Backup: $backup_dir"
echo "Next: copy cluster/cluster.example.json to cluster/cluster.json and follow cluster/QUICK_START_RU.md"
