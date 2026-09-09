#!/usr/bin/env python3
"""TaskForge host-level HA, warm-standby and node telemetry agent.

Patroni/etcd remain the authority for PostgreSQL leader election. This process
never invents quorum. It controls only the local application profile, keeps
standby images warm, exposes health/telemetry on WireGuard, and starts a lite
assistant profile only after a remote primary is already traffic-ready.
"""
from __future__ import annotations

import argparse
import base64
import contextlib
import datetime as dt
import http.server
import json
import os
from pathlib import Path
import secrets
import shlex
import shutil
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from typing import Any, Iterable

sys.path.insert(0, str(Path(__file__).resolve().parent))
from clusterctl import (  # noqa: E402
    Cluster,
    DEFAULT_CONFIG,
    DEFAULT_ENV_FILE,
    COMPOSE_SCRIPT,
    RUNTIME_DIR,
    ROOT,
    ENABLED_FILE,
    current_node_id,
    endpoint_host,
    parse_env,
    secret_from,
)

READINESS_RUNTIME = RUNTIME_DIR / "readiness"
READY_FILE = READINESS_RUNTIME / "traffic-ready"
STATE_FILE = RUNTIME_DIR / "agent-state.json"
TELEMETRY_FILE = RUNTIME_DIR / "node-telemetry.json"
EVENTS_FILE = RUNTIME_DIR / "node-events.json"
APP_ROUTING_ENV = RUNTIME_DIR / "app-routing.env"
CONFIG_HASH_FILE = RUNTIME_DIR / "cluster-config.sha256"
TRUE_VALUES = {"1", "true", "yes", "on"}
STORAGE_SERVICES = {"postgres", "etcd", "rabbitmq", "redis", "minio"}
# Watchtower is control-plane: active on normal and standby nodes, briefly
# fenced only during application activation/recovery.
NON_APP_SERVICES = STORAGE_SERVICES | {"watchtower", "certbot"}
DEFAULT_ASSIST_EXCLUDES = {"support-bot", "telegram-quiz-bot", "rating-worker"}


def utc_now() -> str:
    return dt.datetime.now(dt.timezone.utc).replace(microsecond=0).isoformat()


def log(message: str) -> None:
    print(f"[{utc_now()}] [taskforge-cluster] {message}", flush=True)


def run(
    args: Iterable[str],
    *,
    check: bool = True,
    timeout: int | None = None,
    env: dict[str, str] | None = None,
) -> subprocess.CompletedProcess[str]:
    argv = [str(x) for x in args]
    proc = subprocess.run(
        argv,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        timeout=timeout,
        env=env,
    )
    if check and proc.returncode != 0:
        detail = proc.stderr.strip() or proc.stdout.strip() or f"exit {proc.returncode}"
        raise RuntimeError(f"{shlex.join(argv)}: {detail}")
    return proc


def read_meminfo() -> dict[str, int]:
    result: dict[str, int] = {}
    try:
        for raw in Path("/proc/meminfo").read_text(encoding="utf-8").splitlines():
            key, value = raw.split(":", 1)
            result[key] = int(value.strip().split()[0]) * 1024
    except Exception:
        pass
    return result


def read_uptime() -> float:
    try:
        return float(Path("/proc/uptime").read_text(encoding="utf-8").split()[0])
    except Exception:
        return 0.0


def cpu_sample() -> tuple[int, int] | None:
    try:
        fields = [int(x) for x in Path("/proc/stat").read_text(encoding="utf-8").splitlines()[0].split()[1:]]
        idle = fields[3] + (fields[4] if len(fields) > 4 else 0)
        return sum(fields), idle
    except Exception:
        return None


def temperature_c() -> float | None:
    values: list[float] = []
    for path in Path("/sys/class/thermal").glob("thermal_zone*/temp"):
        try:
            value = float(path.read_text(encoding="utf-8").strip())
            if value > 1000:
                value /= 1000.0
            if -20 <= value <= 150:
                values.append(value)
        except Exception:
            pass
    return round(max(values), 1) if values else None


class Controller:
    CORE_READY_SERVICES = (
        "gateway", "front", "front-ct", "identity-api", "education-api",
        "content-api", "tasks-api", "quiz-api", "solutions-api",
        "execution-api", "files-api", "observability-api",
    )

    def __init__(self, config_path: Path, env_file: Path):
        self.config_path = config_path
        self.env_file = env_file
        self.cluster = Cluster.load(config_path)
        self.node = self.cluster.node(current_node_id())
        self.env_values = parse_env(env_file)
        internal = self.env_values.get("TASKFORGE_INTERNAL_KEY", "")
        if len(internal) < 40:
            raise ValueError("TASKFORGE_INTERNAL_KEY must contain at least 40 characters")
        self.api_user = "taskforge_patroni"
        self.api_password = secret_from(internal, "taskforge-patroni-rest-v2")
        self.shutdown = threading.Event()
        self.lock = threading.RLock()
        # Serializes only short container state transitions. Long image pulls run
        # outside this lock, so a failover never waits for the registry.
        self.deploy_lock = threading.RLock()
        self.events_lock = threading.RLock()
        self.app_active = False
        self.app_mode = "off"
        self.ready = False
        self.local_role = "unknown"
        self.leader = ""
        self.last_error = ""
        self.preferred_replica_since: float | None = None
        self.preferred_replica_leader: str | None = None
        self.last_failback_attempt = 0.0
        self.last_shared_push = 0.0
        self.last_storage_reconcile = 0.0
        self.last_app_reconcile = 0.0
        self.last_standby_reconcile = 0.0
        self.last_telemetry_refresh = 0.0
        self.watchtower_active: bool | None = None
        self.watchtower_error = ""
        self.last_image_scan: dict[str, str] = {}
        self.pending_image_changes: set[str] = set()
        self.last_image_change_monotonic = 0.0
        self.last_seen_leader = ""
        self._services_cache: list[str] | None = None
        self._compose_service_images_cache: dict[str, str] | None = None
        self._cpu_prev = cpu_sample()
        self._cpu_percent: float | None = None
        self._telemetry_cache: dict[str, Any] = {}
        self._events: list[dict[str, Any]] = self._load_events()
        self.pull_thread: threading.Thread | None = None
        self.last_pull_started_at = ""
        self.last_pull_completed_at = ""
        self.last_pull_status = "idle"
        self.last_pull_error = ""
        self.last_pull_changed: list[str] = []
        self.next_pull_after = 0.0
        self.started_at = utc_now()
        # The same agent is installed before Patroni migration. In replica mode it
        # is telemetry + warm-image preparation only and never promotes/stops the
        # current production application. Once the quorum marker exists, Patroni
        # becomes authoritative and the existing HA controller path is enabled.
        self.quorum_enabled = ENABLED_FILE.is_file()
        self.compose_env = os.environ.copy()
        self.compose_env["TASKFORGE_ENV_FILE"] = str(env_file)
        self.compose_env["TASKFORGE_PROD_ENV_FILE"] = str(env_file)
        self.compose_env["TASKFORGE_CLUSTER_CONFIG"] = str(config_path)
        RUNTIME_DIR.mkdir(parents=True, exist_ok=True)
        READINESS_RUNTIME.mkdir(parents=True, exist_ok=True)
        self._set_ready(False)
        # Before quorum is enabled, keep standby application configuration
        # explicitly non-migrating. The first control tick rewrites this based on
        # the actual PostgreSQL role, so an accidentally started standby cannot
        # attempt schema changes against its read-only local database.
        if not self.quorum_enabled:
            if self.node.id == self.cluster.preferred_primary:
                self._write_local_routing()
            else:
                self._write_standby_routing()
        self._write_state()

    def compose(self, *args: str, check: bool = True, timeout: int = 900) -> subprocess.CompletedProcess[str]:
        return run([str(COMPOSE_SCRIPT), *args], check=check, timeout=timeout, env=self.compose_env)

    def services(self) -> list[str]:
        if self._services_cache is None:
            proc = self.compose("config", "--services")
            self._services_cache = [line.strip() for line in proc.stdout.splitlines() if line.strip()]
        return list(self._services_cache)

    def application_services(self) -> list[str]:
        return [name for name in self.services() if name not in NON_APP_SERVICES]

    def assigned_services(self) -> list[str]:
        if self.node.app_profile == "none":
            return []
        excluded = set(self.node.app_exclude_services)
        return [name for name in self.application_services() if name not in excluded]

    def assist_services(self) -> list[str]:
        excluded = set(self.node.assist_exclude_services) | DEFAULT_ASSIST_EXCLUDES
        return [name for name in self.assigned_services() if name not in excluded]

    def storage_services(self) -> list[str]:
        services = set(self.services())
        # Watchtower is deliberately managed separately. It stays alive on a
        # standby to update STOPPED containers, but is fenced during the short
        # hot-start transition so it cannot replace a container mid-activation.
        wanted = [name for name in ("etcd", "postgres", "rabbitmq", "redis", "minio") if name in services]
        if not self.node.dcs_voter and "etcd" in wanted:
            wanted.remove("etcd")
        return wanted

    def ensure_storage(self) -> None:
        now = time.monotonic()
        if now - self.last_storage_reconcile < 30:
            return
        wanted = self.storage_services()
        if wanted:
            self.compose("up", "-d", *wanted, check=False)
        self.last_storage_reconcile = now

    def _atomic_env(self, values: dict[str, str]) -> None:
        temp = APP_ROUTING_ENV.with_suffix(".tmp")
        temp.write_text("".join(f"{k}={v}\n" for k, v in values.items()), encoding="utf-8")
        os.chmod(temp, 0o600)
        temp.replace(APP_ROUTING_ENV)

    def _agent_urls(self) -> str:
        return ";".join(f"http://{endpoint_host(node.wg_ip)}:{node.health_port}" for node in self.cluster.nodes if node.id != self.node.id)

    def _write_local_routing(self) -> None:
        self._atomic_env({
            "DB_HOST": "postgres",
            "DB_PORT": "5432",
            "RABBITMQ_HOST": "rabbitmq",
            "REDIS_HOST": "redis",
            "S3_INTERNAL_ENDPOINT": "http://minio:9000",
            "MIGRATE_ON_STARTUP": "true",
            "BROWSER_API_UPSTREAM": "browser-api:8080",
            "IMAGE_ANALYZER_UPSTREAM": "image-analyzer:8080",
            "TASKFORGE_APP_RUNTIME_MODE": "primary",
            "CLUSTER_TELEMETRY_NOTIFICATIONS_ENABLED": "true",
            "TASKFORGE_CLUSTER_AGENT_URLS": self._agent_urls(),
        })

    def _write_standby_routing(self) -> None:
        self._atomic_env({
            "DB_HOST": "postgres",
            "DB_PORT": "5432",
            "RABBITMQ_HOST": "rabbitmq",
            "REDIS_HOST": "redis",
            "S3_INTERNAL_ENDPOINT": "http://minio:9000",
            "MIGRATE_ON_STARTUP": "false",
            "BROWSER_API_UPSTREAM": "browser-api:8080",
            "IMAGE_ANALYZER_UPSTREAM": "image-analyzer:8080",
            "TASKFORGE_APP_RUNTIME_MODE": "standby",
            "CLUSTER_TELEMETRY_NOTIFICATIONS_ENABLED": "false",
            "TASKFORGE_CLUSTER_AGENT_URLS": self._agent_urls(),
        })

    def _write_remote_routing(self, leader_id: str) -> None:
        leader = self.cluster.node(leader_id)
        self._atomic_env({
            "DB_HOST": leader.wg_ip,
            "DB_PORT": str(leader.postgres_port),
            "RABBITMQ_HOST": leader.wg_ip,
            "REDIS_HOST": leader.wg_ip,
            # MinIO is multi-writer and locally replicated, so C can use its local replica.
            "S3_INTERNAL_ENDPOINT": "http://minio:9000",
            "MIGRATE_ON_STARTUP": "false",
            "BROWSER_API_UPSTREAM": f"{leader.wg_ip}:18080",
            "IMAGE_ANALYZER_UPSTREAM": f"{leader.wg_ip}:18090",
            "TASKFORGE_APP_RUNTIME_MODE": "assist",
            "CLUSTER_TELEMETRY_NOTIFICATIONS_ENABLED": "false",
            "TASKFORGE_CLUSTER_AGENT_URLS": self._agent_urls(),
        })

    def stop_apps(self) -> None:
        with self.deploy_lock:
            services = self.application_services()
            if services:
                self.compose("stop", "--timeout", "20", *services, check=False)
            if self.app_active:
                self._event("application.stopped", "Приложение остановлено", f"Нода {self.node.id} перешла в standby.", "info")
            self.app_active = False
            self.app_mode = "off"

    def _start_service_set(self, services: list[str], mode: str) -> None:
        if not services:
            return
        with self.deploy_lock:
            # Preferred A may use normal compose reconciliation. Hot-standby
            # activation on B/C uses _activate_prepared_service_set instead.
            self.compose("up", "-d", "--no-deps", *services)
            self.app_active = True
            self.app_mode = mode
            self.last_app_reconcile = time.monotonic()

    def _activate_prepared_service_set(self, services: list[str], mode: str) -> None:
        """Activate a warm standby using cached images only.

        Watchtower can be interrupted between removing and recreating a STOPPED
        container. Once it is fenced, compose reconciles the prepared set with
        --pull never. This also applies the freshly rewritten app-routing.env.
        The failover path therefore never waits for GHCR or another registry.
        """
        if not services:
            return
        with self.deploy_lock:
            proc = self.compose(
                "up", "--no-start", "--no-deps", "--pull", "never", *services,
                check=False, timeout=300,
            )
            if proc.returncode != 0:
                detail = proc.stderr.strip() or proc.stdout.strip() or f"compose hot reconcile exit {proc.returncode}"
                raise RuntimeError(f"hot-start preparation failed without registry access: {detail}")
            proc = self.compose("start", *services, check=False, timeout=180)
            if proc.returncode != 0:
                detail = proc.stderr.strip() or proc.stdout.strip() or f"compose start exit {proc.returncode}"
                raise RuntimeError(f"hot-start failed: {detail}")
            self.app_active = True
            self.app_mode = mode
            self.last_app_reconcile = time.monotonic()

    def watchtower_container_id(self) -> str:
        try:
            if "watchtower" not in set(self.services()):
                return ""
            proc = self.compose("ps", "-a", "-q", "watchtower", check=False, timeout=30)
            return next((line.strip() for line in proc.stdout.splitlines() if line.strip()), "") if proc.returncode == 0 else ""
        except Exception:
            # The HA controller must stay alive even while Docker/Compose is
            # restarting. Watchtower is an updater, never a liveness dependency.
            return ""

    def watchtower_running(self) -> bool:
        try:
            container_id = self.watchtower_container_id()
            if not container_id:
                return False
            inspected = run(["docker", "inspect", "--format", "{{.State.Running}}", container_id], check=False, timeout=15)
            return inspected.returncode == 0 and inspected.stdout.strip().lower() == "true"
        except Exception:
            return False

    def _record_watchtower_failure(self, message: str) -> None:
        message = message[:600]
        self.watchtower_active = self.watchtower_running()
        self.last_pull_status = "watchtower-failed"
        self.last_pull_error = message
        if message != self.watchtower_error:
            self._event(
                "update.watchtower_failed",
                "Автообновление недоступно",
                f"Нода {self.node.id}: {message}",
                "warning",
            )
        self.watchtower_error = message
        log(f"Watchtower reconcile warning: {message}")

    def _record_watchtower_recovered(self) -> None:
        if self.watchtower_error:
            self._event(
                "update.watchtower_recovered",
                "Автообновление восстановлено",
                f"Нода {self.node.id}: Watchtower снова работает.",
                "success",
            )
        self.watchtower_error = ""
        self.last_pull_error = ""
        if not self.app_active and self.last_pull_status in {"watchtower-failed", "idle", "preparing"}:
            self.last_pull_status = "watchtower"

    def reconcile_watchtower(self, should_run: bool, *, required: bool | None = None) -> bool:
        """Reconcile the updater without making registry availability part of HA readiness.

        Starting Watchtower is best-effort: a broken registry/updater must never
        make a healthy TaskForge origin traffic-unready. Stopping Watchtower for
        an application role transition is strict because failover must not race a
        container replacement. Existing stopped Watchtower containers are started
        directly so recovery never needs a registry pull.
        """
        if "watchtower" not in set(self.services()):
            return False
        if required is None:
            required = not should_run
        actual = self.watchtower_running()
        if actual == should_run:
            self.watchtower_active = actual
            if actual:
                self._record_watchtower_recovered()
            return True

        try:
            with self.deploy_lock:
                if should_run:
                    if self.watchtower_container_id():
                        proc = self.compose("start", "watchtower", check=False, timeout=60)
                    else:
                        proc = self.compose("up", "-d", "--no-deps", "watchtower", check=False, timeout=120)
                    if proc.returncode != 0:
                        raise RuntimeError(proc.stderr.strip() or proc.stdout.strip() or "cannot start Watchtower")
                    deadline = time.monotonic() + 30
                    while time.monotonic() < deadline and not self.watchtower_running():
                        time.sleep(0.25)
                    if not self.watchtower_running():
                        raise RuntimeError("Watchtower did not reach running state")
                else:
                    self.compose("stop", "--timeout", "10", "watchtower", check=False, timeout=120)
                    deadline = time.monotonic() + 20
                    while time.monotonic() < deadline and self.watchtower_running():
                        time.sleep(0.2)
                    if self.watchtower_running():
                        raise RuntimeError("Watchtower did not stop before application transition")
                self.watchtower_active = should_run
        except Exception as exc:
            message = f"{type(exc).__name__}: {exc}"
            self._record_watchtower_failure(message)
            if required:
                raise
            return False

        if should_run:
            self._record_watchtower_recovered()
        return True

    def running_application_services(self) -> list[str]:
        proc = self.compose("ps", "--services", "--status", "running", check=False, timeout=30)
        running = {line.strip() for line in proc.stdout.splitlines() if line.strip()} if proc.returncode == 0 else set()
        apps = set(self.application_services())
        return sorted(running & apps)

    def start_primary_apps(self) -> None:
        self.shared_state("pull", check=False)
        self._write_local_routing()
        services = self.assigned_services()
        if self.node.id == self.cluster.preferred_primary:
            self._start_service_set(services, "primary")
        else:
            self._activate_prepared_service_set(services, "primary")
        kind = "failover.application_started" if self.node.id != self.cluster.preferred_primary else "application.primary_started"
        title = "Failover: приложение запускается" if kind.startswith("failover.") else "TaskForge запущен"
        self._event(kind, title, f"Нода {self.node.id} запустила основной профиль приложения.", "success")

    def start_assist_apps(self, leader_id: str) -> None:
        self.shared_state("pull", check=False)
        self._write_remote_routing(leader_id)
        # C is a real warm lite standby as well: use only already-cached images.
        self._activate_prepared_service_set(self.assist_services(), "assist")
        self._event("application.assist_started", "Lite-профиль запущен", f"Нода {self.node.id} помогает активной ноде {leader_id}.", "success")

    def reconcile_apps(self, services: list[str], *, force: bool = False, local_only: bool = False) -> None:
        now = time.monotonic()
        if not force and now - self.last_app_reconcile < 30:
            return
        if services:
            args = ["up", "-d", "--no-deps"]
            if local_only:
                args += ["--pull", "never"]
            self.compose(*args, *services, check=False)
        self.last_app_reconcile = now

    def gateway_ready(self) -> bool:
        url = f"http://127.0.0.1:{self.node.http_port}/health/gateway"
        try:
            with urllib.request.urlopen(url, timeout=3) as response:
                return response.status == 200
        except Exception:
            return False

    def critical_services_ready(self, services: list[str]) -> bool:
        existing = set(services)
        required = [name for name in self.CORE_READY_SERVICES if name in existing]
        if not required:
            return False
        proc = self.compose("ps", "-q", *required, check=False, timeout=30)
        ids = [line.strip() for line in proc.stdout.splitlines() if line.strip()]
        if proc.returncode != 0 or len(ids) < len(required):
            return False
        inspected = run(["docker", "inspect", *ids], check=False, timeout=30)
        if inspected.returncode != 0:
            return False
        try:
            containers = json.loads(inspected.stdout)
        except Exception:
            return False
        ready: set[str] = set()
        for container in containers if isinstance(containers, list) else []:
            config = container.get("Config", {}) if isinstance(container, dict) else {}
            labels = config.get("Labels", {}) if isinstance(config, dict) else {}
            service = str((labels or {}).get("com.docker.compose.service", ""))
            state = container.get("State", {}) if isinstance(container, dict) else {}
            if not service or not isinstance(state, dict) or state.get("Status") != "running":
                continue
            health = state.get("Health")
            if isinstance(health, dict) and health.get("Status") != "healthy":
                continue
            ready.add(service)
        return all(name in ready for name in required)

    def shared_state(self, action: str, *, check: bool) -> bool:
        script = Path(__file__).resolve().parent / "ops" / "quorum" / "shared-state.sh"
        if not script.is_file():
            return True
        proc = run([str(script), action], check=False, timeout=180, env=self.compose_env)
        if proc.returncode != 0:
            detail = proc.stderr.strip() or proc.stdout.strip()
            if check:
                raise RuntimeError(f"shared-state {action} failed: {detail}")
            log(f"shared-state {action} skipped/failed: {detail[:300]}")
            return False
        return True

    def _auth_header(self) -> str:
        token = base64.b64encode(f"{self.api_user}:{self.api_password}".encode()).decode()
        return "Basic " + token

    def patroni_request(self, node_id: str, path: str, *, method: str = "GET", body: dict[str, Any] | None = None, timeout: float = 3.0) -> tuple[int, Any]:
        target = self.cluster.node(node_id)
        url = f"http://{endpoint_host(target.wg_ip)}:{target.patroni_port}{path}"
        data = None if body is None else json.dumps(body).encode("utf-8")
        req = urllib.request.Request(url, method=method, data=data, headers={
            "Accept": "application/json", "Content-Type": "application/json", "Authorization": self._auth_header(),
        })
        try:
            with urllib.request.urlopen(req, timeout=timeout) as response:
                raw = response.read()
                try:
                    payload = json.loads(raw.decode("utf-8")) if raw else None
                except Exception:
                    payload = raw.decode("utf-8", "replace")
                return response.status, payload
        except urllib.error.HTTPError as exc:
            raw = exc.read()
            try:
                payload = json.loads(raw.decode("utf-8")) if raw else None
            except Exception:
                payload = raw.decode("utf-8", "replace")
            return exc.code, payload
        except Exception as exc:
            return 0, {"error": f"{type(exc).__name__}: {exc}"}

    def local_patroni(self) -> tuple[int, dict[str, Any]]:
        status, payload = self.patroni_request(self.node.id, "/patroni")
        return status, payload if isinstance(payload, dict) else {}

    def cluster_status(self) -> tuple[int, dict[str, Any]]:
        status, payload = self.patroni_request(self.node.id, "/cluster")
        return status, payload if isinstance(payload, dict) else {}

    def _set_ready(self, value: bool) -> None:
        self.ready = value
        if value:
            READY_FILE.write_text(f"{self.node.id} {utc_now()}\n", encoding="utf-8")
            os.chmod(READY_FILE, 0o644)
        else:
            with contextlib.suppress(FileNotFoundError):
                READY_FILE.unlink()

    def _write_state(self) -> None:
        state = {
            "cluster": self.cluster.name,
            "node": self.node.id,
            "preferred_primary": self.cluster.preferred_primary,
            "cluster_config_sha256": CONFIG_HASH_FILE.read_text(encoding="utf-8").strip() if CONFIG_HASH_FILE.is_file() else "",
            "started_at": self.started_at,
            "updated_at": utc_now(),
            "role": self.local_role,
            "leader": self.leader,
            "app_profile": self.node.app_profile,
            "app_mode": self.app_mode,
            "applications_active": self.app_active,
            "traffic_ready": self.ready,
            "standby_update": {
                "status": self.last_pull_status,
                "started_at": self.last_pull_started_at,
                "completed_at": self.last_pull_completed_at,
                "changed_services": list(self.last_pull_changed),
                "error": self.last_pull_error,
            },
            "last_error": self.last_error,
        }
        temp = STATE_FILE.with_suffix(".tmp")
        temp.write_text(json.dumps(state, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        os.chmod(temp, 0o600)
        temp.replace(STATE_FILE)

    def _replica_mode_role(self) -> tuple[str, str]:
        pg = f"{self.env_values.get('COMPOSE_PROJECT_NAME', 'taskforge-prod')}-postgres-1"
        proc = run([
            "docker", "exec", pg, "sh", "-lc",
            'psql -X -U "$POSTGRES_USER" -d postgres -Atc "select pg_is_in_recovery()"',
        ], check=False, timeout=8)
        value = proc.stdout.strip() if proc.returncode == 0 else ""
        if value == "f":
            return "primary", self.node.id
        if value == "t":
            # Replica-mode topology has one preferred writable node. Physical WAL
            # replication remains authoritative until quorum migration is enabled.
            return "standby", self.cluster.preferred_primary
        return "unreachable", ""

    def update_role(self) -> tuple[str, str]:
        if not self.quorum_enabled:
            role, leader = self._replica_mode_role()
        else:
            status, payload = self.local_patroni()
            role = str(payload.get("role", "unknown")) if status else "unreachable"
            cluster_status, cluster_payload = self.cluster_status()
            leader = ""
            if cluster_status:
                for member in cluster_payload.get("members", []):
                    if isinstance(member, dict) and str(member.get("role", "")).lower() in {"leader", "primary", "master"}:
                        leader = str(member.get("name", ""))
                        break
        self.local_role = role.lower()
        self.leader = leader
        if leader != self.last_seen_leader:
            if self.last_seen_leader or leader:
                self._event("cluster.leader_changed", "Сменилась активная нода", f"Primary: {leader or 'не определён'}.", "warning" if not leader else "success", {"leader": leader})
            self.last_seen_leader = leader
        return self.local_role, leader

    def is_primary(self) -> bool:
        if not self.quorum_enabled:
            return self.local_role == "primary"
        status, _ = self.patroni_request(self.node.id, "/primary", method="GET")
        return status == 200

    def replica_within_failback_lag(self) -> bool:
        lag = int(self.cluster.raw["maximum_lag_on_failback_bytes"])
        query = urllib.parse.urlencode({"lag": lag})
        status, _ = self.patroni_request(self.node.id, f"/replica?{query}")
        return status == 200

    def local_minio_ready(self) -> bool:
        url = f"http://{endpoint_host(self.node.wg_ip)}:{self.node.minio_port}/minio/health/ready"
        try:
            with urllib.request.urlopen(url, timeout=3) as response:
                return response.status == 200
        except Exception:
            return False

    def _postgres_runtime_metrics(self) -> dict[str, Any]:
        """Read the real local PostgreSQL runtime without changing it."""
        project = self.env_values.get("COMPOSE_PROJECT_NAME", "taskforge-prod")
        pg = f"{project}-postgres-1"
        result: dict[str, Any] = {
            "healthy": False,
            "actual_role": "unreachable",
            "receiver": "",
            "source": "",
            "slot": "",
            "replay_gap_bytes": None,
            "replicas": [],
        }
        recovery = run([
            "docker", "exec", pg, "sh", "-lc",
            'psql -X -U "$POSTGRES_USER" -d postgres -Atc "select pg_is_in_recovery()"',
        ], check=False, timeout=8)
        if recovery.returncode != 0:
            return result
        value = recovery.stdout.strip()
        if value == "f":
            result["healthy"] = True
            result["actual_role"] = "primary"
            result["replay_gap_bytes"] = 0
            replicas = run([
                "docker", "exec", pg, "sh", "-lc",
                "psql -X -U \"$POSTGRES_USER\" -d postgres -Atc \"select coalesce(client_addr::text,'')||'|'||coalesce(state,'')||'|'||coalesce(sync_state,'')||'|'||coalesce(pg_wal_lsn_diff(pg_current_wal_lsn(),replay_lsn)::bigint,0) from pg_stat_replication order by client_addr\"",
            ], check=False, timeout=8)
            if replicas.returncode == 0:
                parsed: list[dict[str, Any]] = []
                for raw in replicas.stdout.splitlines():
                    if not raw.strip():
                        continue
                    parts = raw.strip().split("|", 3)
                    parts += [""] * (4 - len(parts))
                    host, state, sync_state, lag = parts[:4]
                    item: dict[str, Any] = {"host": host, "state": state, "sync_state": sync_state, "lag_bytes": None}
                    with contextlib.suppress(ValueError):
                        item["lag_bytes"] = int(lag or "0")
                    parsed.append(item)
                result["replicas"] = parsed
            return result
        if value != "t":
            return result

        result["actual_role"] = "standby"
        receiver = run([
            "docker", "exec", pg, "sh", "-lc",
            "psql -X -U \"$POSTGRES_USER\" -d postgres -Atc \"select coalesce(status,'')||'|'||coalesce(sender_host,'')||'|'||coalesce(sender_port::text,'')||'|'||coalesce(slot_name,'') from pg_stat_wal_receiver limit 1\"",
        ], check=False, timeout=8)
        if receiver.returncode == 0 and receiver.stdout.strip():
            parts = receiver.stdout.strip().split("|", 3)
            parts += [""] * (4 - len(parts))
            status, host, port, slot = parts[:4]
            result["receiver"] = status
            result["source"] = f"{host}:{port}" if host and port else host
            result["slot"] = slot
            result["healthy"] = status.lower() == "streaming"
        gap = run([
            "docker", "exec", pg, "sh", "-lc",
            "psql -X -U \"$POSTGRES_USER\" -d postgres -Atc \"select coalesce(pg_wal_lsn_diff(pg_last_wal_receive_lsn(),pg_last_wal_replay_lsn())::bigint,0)\"",
        ], check=False, timeout=8)
        if gap.returncode == 0:
            with contextlib.suppress(ValueError):
                result["replay_gap_bytes"] = int(gap.stdout.strip() or "0")
        return result

    def _tls_ready(self) -> bool:
        web = self.node.raw.get("web", {}) if isinstance(self.node.raw, dict) else {}
        if str(web.get("tls_mode", "origin-ca")) == "http":
            return True
        return (RUNTIME_DIR / "tls" / "fullchain.pem").stat().st_size > 0 and (RUNTIME_DIR / "tls" / "privkey.pem").stat().st_size > 0 if (RUNTIME_DIR / "tls" / "fullchain.pem").is_file() and (RUNTIME_DIR / "tls" / "privkey.pem").is_file() else False

    def _hot_start_blockers(
        self,
        *,
        assigned_count: int,
        images_ready: int,
        prepared_apps: int,
        minio_ready: bool,
        postgres_ready: bool,
        tls_ready: bool,
    ) -> list[str]:
        blockers: list[str] = []
        if not assigned_count:
            blockers.append("no-assigned-app-services")
        if self.local_role == "unreachable":
            blockers.append("postgres-role-unreachable")
        if images_ready != assigned_count:
            blockers.append(f"images:{images_ready}/{assigned_count}")
        if prepared_apps != assigned_count:
            blockers.append(f"containers:{prepared_apps}/{assigned_count}")
        if not minio_ready:
            blockers.append("minio-not-ready")
        if not tls_ready:
            blockers.append("origin-tls-not-ready")
        # A failover candidate must have a healthy local PostgreSQL replica.
        # Lite/witness nodes never promote, so their local replica health does
        # not block application-assist readiness after another node is primary.
        if self.node.can_be_primary and not postgres_ready:
            blockers.append("postgres-not-ready")
        return blockers

    def _hot_start_ready(
        self,
        *,
        assigned_count: int,
        images_ready: int,
        prepared_apps: int,
        minio_ready: bool,
        postgres_ready: bool,
        tls_ready: bool = True,
    ) -> bool:
        return not self._hot_start_blockers(
            assigned_count=assigned_count,
            images_ready=images_ready,
            prepared_apps=prepared_apps,
            minio_ready=minio_ready,
            postgres_ready=postgres_ready,
            tls_ready=tls_ready,
        )

    def remote_agent_state(self, node_id: str) -> dict[str, Any]:
        target = self.cluster.node(node_id)
        url = f"http://{endpoint_host(target.wg_ip)}:{target.health_port}/ha/status"
        try:
            with urllib.request.urlopen(url, timeout=2.5) as response:
                payload = json.loads(response.read().decode("utf-8"))
                return payload if isinstance(payload, dict) else {}
        except Exception:
            return {}

    def maybe_failback(self) -> None:
        if not self.cluster.automatic_failback or self.node.id != self.cluster.preferred_primary:
            self.preferred_replica_since = None
            self.preferred_replica_leader = None
            return
        if self.is_primary():
            self.preferred_replica_since = None
            self.preferred_replica_leader = None
            return
        if not self.leader or self.leader == self.node.id or not self.replica_within_failback_lag() or not self.local_minio_ready():
            self.preferred_replica_since = None
            self.preferred_replica_leader = None
            return
        now = time.monotonic()
        if self.preferred_replica_since is None or self.preferred_replica_leader != self.leader:
            self.preferred_replica_since = now
            self.preferred_replica_leader = self.leader
            log(f"preferred node is a healthy replica of {self.leader}; waiting before automatic failback")
            return
        required = int(self.cluster.raw["failback_delay_seconds"]) + int(self.cluster.raw["failback_stable_seconds"])
        if now - self.preferred_replica_since < required or now - self.last_failback_attempt < 120:
            return
        self.last_failback_attempt = now
        body = {"leader": self.leader, "candidate": self.node.id}
        status, response = self.patroni_request(self.leader, "/switchover", method="POST", body=body, timeout=15)
        if 200 <= status < 300:
            log(f"controlled failback requested: {self.leader} -> {self.node.id}")
            self._event("cluster.failback_requested", "Запрошен возврат primary", f"{self.leader} → {self.node.id}", "info")
            self.preferred_replica_since = None
            self.preferred_replica_leader = None
        else:
            log(f"controlled failback rejected by Patroni: status={status} response={response}")

    def _compose_service_images(self) -> dict[str, str]:
        if self._compose_service_images_cache is not None:
            return dict(self._compose_service_images_cache)
        proc = self.compose("config", "--format", "json", check=False, timeout=60)
        result: dict[str, str] = {}
        if proc.returncode == 0:
            try:
                payload = json.loads(proc.stdout)
                services = payload.get("services", {}) if isinstance(payload, dict) else {}
                if isinstance(services, dict):
                    for name, cfg in services.items():
                        if isinstance(cfg, dict) and cfg.get("image"):
                            result[str(name)] = str(cfg["image"])
            except Exception:
                pass
        self._compose_service_images_cache = result
        return dict(result)

    def _image_fingerprints(self, services: list[str]) -> dict[str, str]:
        images = self._compose_service_images()
        result: dict[str, str] = {}
        for service in services:
            ref = images.get(service)
            if not ref:
                continue
            proc = run(["docker", "image", "inspect", ref, "--format", "{{.Id}}"], check=False, timeout=15)
            if proc.returncode == 0 and proc.stdout.strip():
                result[service] = proc.stdout.strip()
        return result

    def unassigned_application_containers(self) -> list[str]:
        assigned = set(self.assigned_services())
        stale: list[str] = []
        for service in self.application_services():
            if service in assigned:
                continue
            proc = self.compose("ps", "-a", "-q", service, check=False, timeout=30)
            if proc.returncode == 0 and any(line.strip() for line in proc.stdout.splitlines()):
                stale.append(service)
        return stale

    def prune_unassigned_application_containers(self, stale: list[str] | None = None) -> None:
        """Remove stale containers outside this node profile.

        Lite C must not retain an old browser/image container: Watchtower scans
        existing labeled containers, so a stale excluded container would make C
        silently download/update an image that is intentionally not assigned.
        The caller fences Watchtower before pruning during standby reconciliation.
        """
        stale = self.unassigned_application_containers() if stale is None else stale
        for service in stale:
            with self.deploy_lock:
                self.compose("rm", "-s", "-f", service, check=False, timeout=120)

    def missing_standby_services(self, services: list[str]) -> list[str]:
        missing: list[str] = []
        for service in services:
            proc = self.compose("ps", "-a", "-q", service, check=False, timeout=30)
            if proc.returncode != 0 or not any(line.strip() for line in proc.stdout.splitlines()):
                missing.append(service)
        return missing

    def prepare_standby_containers(self, services: list[str]) -> None:
        """Create/reconcile assigned standby containers without starting them.

        First try using only cached images under the short deployment lock. If
        images are missing, release the lock before the long registry pull. A
        promotion can then acquire the lock immediately and start the last fully
        prepared set instead of waiting for GHCR. After the pull, re-check role
        before touching stopped containers.
        """
        if not services:
            return
        with self.deploy_lock:
            if self.app_active or self.local_role in {"primary", "leader", "master"}:
                return
            proc = self.compose(
                "up", "--no-start", "--no-deps", "--pull", "never", *services,
                check=False, timeout=300,
            )
            if proc.returncode == 0:
                return

        # Potentially slow network operation is deliberately outside deploy_lock.
        pulled = self.compose("pull", *services, check=False, timeout=1800)
        if pulled.returncode != 0:
            detail = pulled.stderr.strip() or pulled.stdout.strip() or f"compose pull exit {pulled.returncode}"
            raise RuntimeError(detail)

        with self.deploy_lock:
            if self.app_active or self.local_role in {"primary", "leader", "master"}:
                return
            proc = self.compose(
                "up", "--no-start", "--no-deps", "--pull", "never", *services,
                check=False, timeout=300,
            )
            if proc.returncode != 0:
                detail = proc.stderr.strip() or proc.stdout.strip() or f"compose warm prepare exit {proc.returncode}"
                raise RuntimeError(detail)

    def _standby_pull_worker(self) -> None:
        """Initial/self-healing warm preparation; Watchtower owns updates.

        There must not be two independent five-minute pull loops. This worker
        only creates missing assigned containers in STOPPED state. Compose may
        download an image when the node has none; afterwards Watchtower with
        include-stopped=true keeps the prepared container current.
        """
        services = self.missing_standby_services(self.assigned_services())
        if not services:
            self.last_pull_status = "watchtower"
            return
        self.last_pull_started_at = utc_now()
        self.last_pull_status = "preparing"
        self.last_pull_error = ""
        try:
            self.prepare_standby_containers(services)
            self._compose_service_images_cache = None
            self.last_pull_status = "watchtower"
        except Exception as exc:
            self.last_pull_status = "failed"
            self.last_pull_error = f"{type(exc).__name__}: {exc}"[:600]
            self._event("update.failed", "Не удалось подготовить standby", f"Нода {self.node.id}: {self.last_pull_error}", "error")
            log(f"standby container preparation failed: {self.last_pull_error}")
        finally:
            self.last_pull_completed_at = utc_now()

    def standby_containers_exist(self, services: list[str]) -> bool:
        return not self.missing_standby_services(services)

    def schedule_standby_pull(self) -> None:
        # Historical method name retained for the public telemetry schema.
        # Ongoing updates are Watchtower's job; only missing prepared containers
        # cause a background prepare operation here.
        if self.node.app_profile == "none" or self.app_active:
            return
        services = self.assigned_services()
        if self.standby_containers_exist(services):
            if self.last_pull_status in {"idle", "preparing"}:
                self.last_pull_status = "watchtower"
            return
        now = time.monotonic()
        if self.pull_thread is not None and self.pull_thread.is_alive():
            return
        if now < self.next_pull_after:
            return
        self.next_pull_after = now + 30
        self.pull_thread = threading.Thread(target=self._standby_pull_worker, name="taskforge-standby-prepare", daemon=True)
        self.pull_thread.start()

    def _load_events(self) -> list[dict[str, Any]]:
        try:
            data = json.loads(EVENTS_FILE.read_text(encoding="utf-8"))
            return data[-200:] if isinstance(data, list) else []
        except Exception:
            return []

    def _event(self, kind: str, title: str, message: str, severity: str = "info", data: dict[str, Any] | None = None) -> None:
        event = {
            "id": f"{int(time.time() * 1000)}-{secrets.token_hex(4)}",
            "at": utc_now(),
            "node": self.node.id,
            "kind": kind,
            "severity": severity,
            "title": title,
            "message": message,
            "data": data or {},
        }
        with self.events_lock:
            self._events.append(event)
            self._events = self._events[-200:]
            temp = EVENTS_FILE.with_suffix(".tmp")
            temp.write_text(json.dumps(self._events, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
            os.chmod(temp, 0o600)
            temp.replace(EVENTS_FILE)

    def tick(self) -> None:
        self.ensure_storage()
        role, leader = self.update_role()

        if not self.quorum_enabled:
            # Replica-mode safety: no automatic promotion. A stays active; B/C
            # are hot standby and Watchtower continuously updates their STOPPED
            # assigned containers.
            if self.is_primary():
                self._write_local_routing()
                self.app_mode = "primary-existing"
                self.app_active = self.gateway_ready()
                self._set_ready(self.app_active and self.critical_services_ready(self.assigned_services()))
                # Updater health is not part of traffic readiness.
                self.reconcile_watchtower(True)
            else:
                self._write_standby_routing()
                running = self.running_application_services()
                stale = self.unassigned_application_containers()
                missing = self.missing_standby_services(self.assigned_services())
                # Watchtower stays ON during normal standby operation, but any
                # local container-set transition is fenced first. This prevents
                # the updater racing initial prepare or removal of C-only excludes.
                if running or stale or missing:
                    self.reconcile_watchtower(False)
                if running:
                    self.stop_apps()
                if stale:
                    self.prune_unassigned_application_containers(stale)
                self.app_mode = "warm-standby"
                self.app_active = False
                self._set_ready(False)
                if missing:
                    self.schedule_standby_pull()
                else:
                    self.reconcile_watchtower(True)
            self.last_error = ""
            self._write_state()
            self.refresh_telemetry()
            return

        if self.is_primary():
            if not self.node.can_be_primary:
                self._set_ready(False)
                self.reconcile_watchtower(False)
                self.stop_apps()
                raise RuntimeError(f"node {self.node.id} is primary but app.can_be_primary=false; Patroni fencing must be checked")

            desired = self.assigned_services()
            self._write_local_routing()
            healthy_now = self.gateway_ready() and self.critical_services_ready(desired)

            if healthy_now:
                # Steady state: never stop/start Watchtower every control tick.
                if not self.app_active or self.app_mode != "primary":
                    self.app_active = True
                    self.app_mode = "primary"
                    self.last_app_reconcile = time.monotonic()
                    log(f"adopted already-running primary application on node {self.node.id}")
                self._set_ready(True)
                self.reconcile_watchtower(True)
            else:
                # Only the activation/recovery window fences the updater. B uses
                # cached/prepared containers and --pull never, so a dead registry
                # cannot delay restoring the site.
                self.reconcile_watchtower(False)
                self._set_ready(False)
                if not self.app_active or self.app_mode != "primary":
                    log(f"node {self.node.id} owns the Patroni leader lock; starting TaskForge profile={self.node.app_profile}")
                    self.stop_apps()
                    self.start_primary_apps()
                else:
                    self.reconcile_apps(desired, force=True, local_only=self.node.id != self.cluster.preferred_primary)
                if self.gateway_ready() and self.critical_services_ready(desired):
                    was_ready = self.ready
                    self._set_ready(True)
                    self.reconcile_watchtower(True)
                    if not was_ready and self.node.id != self.cluster.preferred_primary:
                        self._event("failover.ready", "Failover завершён", f"Нода {self.node.id} стала активной и готова принимать трафик.", "success")
                else:
                    self._set_ready(False)

            now = time.monotonic()
            interval = int(self.cluster.raw["shared_state_interval_seconds"])
            if now - self.last_shared_push >= interval:
                self.shared_state("push", check=False)
                self.last_shared_push = now
        else:
            assist = False
            if self.node.assist_on_failover and leader and leader != self.cluster.preferred_primary:
                remote = self.remote_agent_state(leader)
                assist = bool(remote.get("traffic_ready")) and str(remote.get("role", "")).lower() in {"leader", "primary", "master"}

            if assist:
                self.prune_unassigned_application_containers()
                desired = self.assist_services()
                self._write_remote_routing(leader)
                healthy_now = self.gateway_ready() and self.critical_services_ready(desired)
                if healthy_now:
                    if not self.app_active or self.app_mode != "assist":
                        self.app_active = True
                        self.app_mode = "assist"
                        self.last_app_reconcile = time.monotonic()
                        log(f"adopted already-running lite assist application on node {self.node.id} for primary {leader}")
                    self._set_ready(True)
                    self.reconcile_watchtower(True)
                else:
                    self.reconcile_watchtower(False)
                    self._set_ready(False)
                    if not self.app_active or self.app_mode != "assist":
                        log(f"node {self.node.id} starting lite assist profile for primary {leader}")
                        self.stop_apps()
                        self.start_assist_apps(leader)
                    else:
                        self.reconcile_apps(desired, force=True, local_only=True)
                    assist_ready = self.gateway_ready() and self.critical_services_ready(desired)
                    self._set_ready(assist_ready)
                    if assist_ready:
                        self.reconcile_watchtower(True)
            else:
                if self.ready or self.app_active:
                    log(f"node {self.node.id} is standby (role={role}, leader={leader or 'none'}); stopping TaskForge applications")
                self._set_ready(False)
                self._write_standby_routing()
                running = self.running_application_services()
                stale = self.unassigned_application_containers()
                missing = self.missing_standby_services(self.assigned_services())
                if self.app_active or running or stale or missing:
                    self.reconcile_watchtower(False)
                if self.app_active or running:
                    self.stop_apps()
                    self.last_standby_reconcile = time.monotonic()
                if stale:
                    self.prune_unassigned_application_containers(stale)
                self.app_mode = "warm-standby"
                self.app_active = False
                if missing:
                    self.schedule_standby_pull()
                else:
                    self.reconcile_watchtower(True)
                self.maybe_failback()

        self.last_error = ""
        self._write_state()
        self.refresh_telemetry()

    def run_forever(self) -> None:
        log(f"starting node={self.node.id} cluster={self.cluster.name} preferred={self.cluster.preferred_primary} app_profile={self.node.app_profile} deployment_mode={'quorum' if self.quorum_enabled else 'replica'}")
        while not self.shutdown.is_set():
            try:
                with self.lock:
                    self.tick()
            except Exception as exc:
                self.last_error = f"{type(exc).__name__}: {exc}"
                log(self.last_error)
                self._set_ready(False)
                self._write_state()
                with contextlib.suppress(Exception):
                    self.refresh_telemetry(force=True)
            self.shutdown.wait(5)
        self._set_ready(False)

    def public_state(self) -> dict[str, Any]:
        try:
            state = json.loads(STATE_FILE.read_text(encoding="utf-8"))
            if isinstance(state, dict):
                return state
        except Exception:
            pass
        return {
            "cluster": self.cluster.name,
            "node": self.node.id,
            "role": self.local_role,
            "leader": self.leader,
            "app_profile": self.node.app_profile,
            "app_mode": self.app_mode,
            "deployment_mode": "quorum" if self.quorum_enabled else "replica",
            "cluster_config_sha256": CONFIG_HASH_FILE.read_text(encoding="utf-8").strip() if CONFIG_HASH_FILE.is_file() else "",
            "traffic_ready": self.ready,
        }

    def _host_metrics(self) -> dict[str, Any]:
        now_sample = cpu_sample()
        if self._cpu_prev and now_sample:
            total_delta = now_sample[0] - self._cpu_prev[0]
            idle_delta = now_sample[1] - self._cpu_prev[1]
            if total_delta > 0:
                self._cpu_percent = round(max(0.0, min(100.0, (total_delta - idle_delta) * 100.0 / total_delta)), 1)
        self._cpu_prev = now_sample
        mem = read_meminfo()
        total = mem.get("MemTotal", 0)
        available = mem.get("MemAvailable", 0)
        swap_total = mem.get("SwapTotal", 0)
        swap_free = mem.get("SwapFree", 0)
        disk = shutil.disk_usage("/")
        try:
            load = list(os.getloadavg())
        except Exception:
            load = [0.0, 0.0, 0.0]
        return {
            "hostname": os.uname().nodename,
            "kernel": os.uname().release,
            "cpu_count": os.cpu_count() or 0,
            "cpu_percent": self._cpu_percent,
            "load": [round(float(x), 2) for x in load],
            "memory": {"total": total, "used": max(0, total - available), "available": available},
            "swap": {"total": swap_total, "used": max(0, swap_total - swap_free)},
            "disk": {"total": disk.total, "used": disk.used, "free": disk.free},
            "uptime_seconds": round(read_uptime()),
            "temperature_c": temperature_c(),
        }

    def _firewall_status(self) -> str:
        proc = run(["ufw", "status"], check=False, timeout=8)
        if proc.returncode != 0:
            return "unknown"
        first = proc.stdout.splitlines()[0].strip().lower() if proc.stdout.splitlines() else ""
        return "active" if first == "status: active" else "inactive" if first == "status: inactive" else "unknown"

    def _wireguard_metrics(self) -> dict[str, Any]:
        proc = run(["wg", "show", "wg-taskforge", "dump"], check=False, timeout=8)
        peers: list[dict[str, Any]] = []
        if proc.returncode == 0:
            lines = [line.split("\t") for line in proc.stdout.splitlines() if line.strip()]
            for row in lines[1:]:
                if len(row) >= 8:
                    try:
                        handshake = int(row[4])
                        rx, tx = int(row[5]), int(row[6])
                    except Exception:
                        handshake, rx, tx = 0, 0, 0
                    peers.append({
                        "allowed_ips": row[3], "endpoint": row[2], "latest_handshake_epoch": handshake,
                        "rx_bytes": rx, "tx_bytes": tx,
                    })
        return {"interface": "wg-taskforge", "peers": peers}

    def _docker_telemetry(self) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
        project = self.env_values.get("COMPOSE_PROJECT_NAME", "taskforge-prod")
        proc = run(["docker", "ps", "-a", "--filter", f"label=com.docker.compose.project={project}", "--format", "{{.ID}}"], check=False, timeout=20)
        ids = [x.strip() for x in proc.stdout.splitlines() if x.strip()] if proc.returncode == 0 else []
        inspected: list[dict[str, Any]] = []
        if ids:
            iproc = run(["docker", "inspect", *ids], check=False, timeout=40)
            if iproc.returncode == 0:
                try:
                    payload = json.loads(iproc.stdout)
                    inspected = payload if isinstance(payload, list) else []
                except Exception:
                    pass
        stats_by_id: dict[str, dict[str, str]] = {}
        running_ids = [str(c.get("Id", "")) for c in inspected if isinstance(c, dict) and (c.get("State") or {}).get("Running")]
        if running_ids:
            sproc = run(["docker", "stats", "--no-stream", "--format", "{{json .}}", *running_ids], check=False, timeout=30)
            if sproc.returncode == 0:
                for line in sproc.stdout.splitlines():
                    try:
                        row = json.loads(line)
                        key = str(row.get("ID", ""))
                        if key:
                            stats_by_id[key] = row
                    except Exception:
                        pass
        containers: list[dict[str, Any]] = []
        by_service: dict[str, dict[str, Any]] = {}
        for c in inspected:
            cfg = c.get("Config", {}) or {}
            labels = cfg.get("Labels", {}) or {}
            state = c.get("State", {}) or {}
            service = str(labels.get("com.docker.compose.service", ""))
            cid = str(c.get("Id", ""))
            stat = next((v for k, v in stats_by_id.items() if cid.startswith(k) or k.startswith(cid[:12])), {})
            health = (state.get("Health") or {}).get("Status") if isinstance(state.get("Health"), dict) else None
            if service == "postgres" and state.get("Running") and self.local_role == "standby":
                # PostgreSQL standby is a healthy database process. Leadership is
                # reported separately from Patroni/pg_is_in_recovery().
                health = "standby"
            item = {
                "service": service,
                "name": str(c.get("Name", "")).lstrip("/"),
                "state": str(state.get("Status", "unknown")),
                "health": health or ("healthy" if state.get("Running") else "stopped"),
                "restart_count": int(c.get("RestartCount", 0) or 0),
                "started_at": state.get("StartedAt"),
                "cpu": stat.get("CPUPerc"),
                "memory": stat.get("MemUsage"),
                "image_fingerprint": str(c.get("Image", "")),
            }
            containers.append(item)
            if service:
                by_service[service] = item
        image_refs = self._compose_service_images()
        all_app = self.application_services()
        assigned = set(self.assigned_services())
        excluded = set(self.node.app_exclude_services)
        local_images = self._image_fingerprints([s for s in all_app if s not in excluded])
        services: list[dict[str, Any]] = []
        for name in all_app:
            container = by_service.get(name)
            is_assigned = name in assigned
            # Compare the image actually bound to the running/prepared container.
            # A tag may already point to a freshly pulled image while the active
            # container still runs the previous one; reporting the tag here would
            # falsely claim that nodes are synchronized. Fall back to the local
            # image only when no prepared container exists yet.
            container_fingerprint = str(container.get("image_fingerprint") or "") if container else ""
            fingerprint = (container_fingerprint or local_images.get(name, "")) if is_assigned else ""
            if not is_assigned:
                desired = "not-assigned"
            elif container and container.get("state") == "running":
                desired = "running"
            elif container and container.get("state") in {"created", "exited"} and fingerprint:
                desired = "prepared"
            elif fingerprint:
                desired = "image-ready"
            else:
                desired = "image-missing"
            services.append({
                "service": name,
                "assigned": is_assigned,
                "desired_state": desired,
                "container_state": container.get("state") if container else "absent",
                "health": container.get("health") if container else None,
                "image_fingerprint": fingerprint,
            })
        # Running/storage containers stay visible even if they are not app services.
        return sorted(containers, key=lambda x: (x.get("service") or x.get("name") or "")), sorted(services, key=lambda x: x["service"])

    def refresh_telemetry(self, *, force: bool = False) -> dict[str, Any]:
        now = time.monotonic()
        if not force and self._telemetry_cache and now - self.last_telemetry_refresh < 10:
            return self._telemetry_cache
        containers, services = self._docker_telemetry()
        if self.quorum_enabled:
            _, cluster_payload = self.cluster_status()
        else:
            cluster_payload = {"members": []}
        local_member = None
        members = cluster_payload.get("members", []) if isinstance(cluster_payload, dict) else []
        if isinstance(members, list):
            local_member = next((m for m in members if isinstance(m, dict) and str(m.get("name", "")) == self.node.id), None)
        assigned = [x for x in services if x.get("assigned")]
        images_ready = sum(1 for x in assigned if x.get("image_fingerprint"))
        prepared_apps = sum(1 for x in assigned if x.get("desired_state") in {"prepared", "running"})
        minio_ready = self.local_minio_ready()
        postgres_runtime = self._postgres_runtime_metrics()
        tls_ready = self._tls_ready()
        hot_start_blockers = self._hot_start_blockers(
            assigned_count=len(assigned),
            images_ready=images_ready,
            prepared_apps=prepared_apps,
            minio_ready=minio_ready,
            postgres_ready=bool(postgres_runtime.get("healthy")),
            tls_ready=tls_ready,
        )
        hot_start_ready = not hot_start_blockers

        # Watchtower owns image replacement. Coalesce a rolling update into one
        # event after a quiet window instead of sending a Telegram message for
        # every container that Watchtower replaces.
        current_scan = {str(x.get("service")): str(x.get("image_fingerprint") or "") for x in assigned if x.get("image_fingerprint")}
        if self.last_image_scan:
            changed = sorted(name for name, fingerprint in current_scan.items() if self.last_image_scan.get(name) not in {None, fingerprint})
            if changed:
                if not self.pending_image_changes:
                    self.last_pull_started_at = utc_now()
                self.pending_image_changes.update(changed)
                self.last_image_change_monotonic = now
                self.last_pull_changed = sorted(self.pending_image_changes)
                self.last_pull_status = "updating"
        self.last_image_scan = current_scan

        if self.pending_image_changes and now - self.last_image_change_monotonic >= 20:
            changed = sorted(self.pending_image_changes)
            self.pending_image_changes.clear()
            self.last_pull_changed = changed
            self.last_pull_completed_at = utc_now()
            if self.is_primary():
                self.last_pull_status = "activated"
                self._event(
                    "update.activated",
                    "TaskForge обновлён",
                    f"Активная нода {self.node.id}: обновлено сервисов — {len(changed)}.",
                    "success",
                    {"services": changed},
                )
            else:
                self.last_pull_status = "prepared"
                self._event(
                    "update.prepared",
                    "Обновление подготовлено",
                    f"Нода {self.node.id}: Watchtower обновил подготовленных сервисов — {len(changed)}.",
                    "success",
                    {"services": changed},
                )

        telemetry = {
            "schema_version": 1,
            "generated_at": utc_now(),
            "cluster": self.cluster.name,
            "node": {
                "id": self.node.id,
                "priority": self.node.priority,
                "preferred": self.node.id == self.cluster.preferred_primary,
                "dcs_voter": self.node.dcs_voter,
                "can_be_primary": self.node.can_be_primary,
                "app_profile": self.node.app_profile,
                "assist_on_failover": self.node.assist_on_failover,
                "excluded_services": list(self.node.app_exclude_services),
                "deployment_mode": "quorum" if self.quorum_enabled else "replica",
                "bundle_version": (ROOT / "VERSION").read_text(encoding="utf-8").strip() if (ROOT / "VERSION").is_file() else "",
                "bundle_revision": (ROOT / "REVISION").read_text(encoding="utf-8").strip() if (ROOT / "REVISION").is_file() else "",
            },
            # Internal topology lets observability render an offline B/C even if
            # that agent has never answered since observability-api started. It
            # is never forwarded verbatim to the browser.
            "topology": [
                {
                    "id": n.id,
                    "agent_url": f"http://{endpoint_host(n.wg_ip)}:{n.health_port}",
                    "app_profile": n.app_profile,
                    "can_be_primary": n.can_be_primary,
                    "dcs_voter": n.dcs_voter,
                }
                for n in self.cluster.nodes
            ],
            "ha": {
                "role": self.local_role,
                "leader": self.leader,
                "app_mode": self.app_mode,
                "applications_active": self.app_active,
                "traffic_ready": self.ready,
                "hot_start_ready": hot_start_ready,
                "hot_start_blockers": hot_start_blockers,
                "tls_ready": tls_ready,
                "last_error": self.last_error,
            },
            "host": self._host_metrics(),
            "firewall": {"status": self._firewall_status()},
            "wireguard": self._wireguard_metrics(),
            "postgres": {
                **postgres_runtime,
                "role": self.local_role,
                "leader": self.leader,
                "member": local_member or {},
            },
            "minio": {"ready": minio_ready},
            "docker": {
                "containers": containers,
                "services": services,
                "container_count": len(containers),
                "assigned_app_count": len(assigned),
                "prepared_app_count": prepared_apps,
                "images_ready": images_ready,
                "images_missing": max(0, len(assigned) - images_ready),
            },
            "update": {
                "mode": "watchtower",
                "watchtower_running": self.watchtower_running(),
                "include_stopped": True,
                "revive_stopped": False,
                "status": self.last_pull_status,
                "started_at": self.last_pull_started_at,
                "completed_at": self.last_pull_completed_at,
                "changed_services": list(self.last_pull_changed),
                "error": self.last_pull_error,
                "watchtower_error": self.watchtower_error,
                "poll_interval_seconds": int(self.env_values.get("WATCHTOWER_POLL_INTERVAL", "300") or 300),
            },
            "events": list(self._events[-50:]),
        }
        self._telemetry_cache = telemetry
        self.last_telemetry_refresh = now
        temp = TELEMETRY_FILE.with_suffix(".tmp")
        temp.write_text(json.dumps(telemetry, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        os.chmod(temp, 0o600)
        temp.replace(TELEMETRY_FILE)
        return telemetry

    def telemetry_snapshot(self) -> dict[str, Any]:
        # HTTP readers must never wait for the HA control lock. tick() already
        # refreshes telemetry periodically; serving that completed snapshot keeps
        # /ha/telemetry cheap even while Docker/PostgreSQL reconciliation is busy.
        snapshot = self._telemetry_cache
        if snapshot:
            return snapshot
        try:
            payload = json.loads(TELEMETRY_FILE.read_text(encoding="utf-8"))
            if isinstance(payload, dict):
                return payload
        except Exception:
            pass
        return {
            "schema_version": 1,
            "generated_at": utc_now(),
            "cluster": self.cluster.name,
            "node": {
                "id": self.node.id,
                "priority": self.node.priority,
                "preferred": self.node.id == self.cluster.preferred_primary,
                "dcs_voter": self.node.dcs_voter,
                "can_be_primary": self.node.can_be_primary,
                "app_profile": self.node.app_profile,
            },
            "topology": [],
            "ha": {
                "role": self.local_role,
                "leader": self.leader,
                "app_mode": self.app_mode,
                "applications_active": self.app_active,
                "traffic_ready": self.ready,
                "hot_start_ready": False,
                "hot_start_blockers": ["telemetry-warming-up"],
                "last_error": self.last_error,
            },
            "events": [],
        }


class Handler(http.server.BaseHTTPRequestHandler):
    controller: Controller

    def log_message(self, fmt: str, *args: Any) -> None:
        log("http " + (fmt % args))

    def _reply(self, status: int, payload: dict[str, Any]) -> None:
        body = (json.dumps(payload, ensure_ascii=False) + "\n").encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(body)

    def do_HEAD(self) -> None:  # noqa: N802
        self.do_GET()

    def do_GET(self) -> None:  # noqa: N802
        state = self.controller.public_state()
        path = urllib.parse.urlsplit(self.path).path
        if path == "/ha/live":
            self._reply(200, {"status": "ok", "cluster": state.get("cluster"), "node": state.get("node")})
            return
        if path in {"/ha/traffic-ready", "/ha/primary-ready"}:
            ready = bool(state.get("traffic_ready"))
            self._reply(200 if ready else 503, {"status": "ready" if ready else "standby", **state})
            return
        if path == "/ha/status":
            self._reply(200, state)
            return
        if path == "/ha/telemetry":
            self._reply(200, self.controller.telemetry_snapshot())
            return
        self._reply(404, {"error": "not-found"})


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", default=os.environ.get("TASKFORGE_CLUSTER_CONFIG", str(DEFAULT_CONFIG)))
    parser.add_argument("--env-file", default=os.environ.get("TASKFORGE_ENV_FILE", str(DEFAULT_ENV_FILE)))
    parser.add_argument("--once", action="store_true")
    parser.add_argument("--status", action="store_true")
    parser.add_argument("--telemetry", action="store_true")
    args = parser.parse_args()
    controller = Controller(Path(args.config), Path(args.env_file))
    if args.status:
        print(json.dumps(controller.public_state(), ensure_ascii=False, indent=2))
        return 0
    if args.telemetry:
        controller.refresh_telemetry(force=True)
        print(json.dumps(controller.telemetry_snapshot(), ensure_ascii=False, indent=2))
        return 0
    if args.once:
        controller.tick()
        print(json.dumps(controller.public_state(), ensure_ascii=False, indent=2))
        return 0
    Handler.controller = controller
    server = http.server.ThreadingHTTPServer((controller.node.wg_ip, controller.node.health_port), Handler)
    server.daemon_threads = True
    thread = threading.Thread(target=server.serve_forever, name="taskforge-cluster-http", daemon=True)
    thread.start()
    try:
        controller.run_forever()
    except KeyboardInterrupt:
        pass
    finally:
        controller.shutdown.set()
        server.shutdown()
        server.server_close()
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (ValueError, KeyError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        raise SystemExit(2)
