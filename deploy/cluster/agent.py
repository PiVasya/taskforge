#!/usr/bin/env python3
"""Host-level TaskForge cluster runtime controller.

Patroni owns PostgreSQL leader election. This controller only starts the
write-capable TaskForge application stack on the node that Patroni currently
reports as primary, exposes Cloudflare health endpoints, and optionally asks
Patroni for a controlled failback to the preferred node after it has caught up.
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
import shlex
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
    ROOT,
    COMPOSE_SCRIPT,
    RUNTIME_DIR,
    current_node_id,
    endpoint_host,
    parse_env,
    secret_from,
)

READINESS_RUNTIME = RUNTIME_DIR / "readiness"
READY_FILE = READINESS_RUNTIME / "traffic-ready"
STATE_FILE = RUNTIME_DIR / "agent-state.json"
CONFIG_HASH_FILE = RUNTIME_DIR / "cluster-config.sha256"
TRUE_VALUES = {"1", "true", "yes", "on"}


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


class Controller:
    STORAGE = {"postgres", "etcd", "rabbitmq", "redis", "minio", "watchtower"}
    CORE_READY_SERVICES = (
        "gateway",
        "front",
        "front-ct",
        "identity-api",
        "education-api",
        "content-api",
        "tasks-api",
        "quiz-api",
        "solutions-api",
        "execution-api",
        "files-api",
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
        self.app_active = False
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
        self._services_cache: list[str] | None = None
        self.started_at = utc_now()
        self.compose_env = os.environ.copy()
        self.compose_env["TASKFORGE_ENV_FILE"] = str(env_file)
        self.compose_env["TASKFORGE_PROD_ENV_FILE"] = str(env_file)
        self.compose_env["TASKFORGE_CLUSTER_CONFIG"] = str(config_path)
        RUNTIME_DIR.mkdir(parents=True, exist_ok=True)
        READINESS_RUNTIME.mkdir(parents=True, exist_ok=True)
        self._set_ready(False)
        self._write_state()

    def compose(self, *args: str, check: bool = True, timeout: int = 900) -> subprocess.CompletedProcess[str]:
        return run([str(COMPOSE_SCRIPT), *args], check=check, timeout=timeout, env=self.compose_env)

    def services(self) -> list[str]:
        if self._services_cache is None:
            proc = self.compose("config", "--services")
            self._services_cache = [line.strip() for line in proc.stdout.splitlines() if line.strip()]
        return list(self._services_cache)

    def active_services(self) -> list[str]:
        return [name for name in self.services() if name not in self.STORAGE and name != "certbot"]

    def storage_services(self) -> list[str]:
        services = set(self.services())
        wanted = [name for name in ("etcd", "postgres", "rabbitmq", "redis", "minio", "watchtower") if name in services]
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

    def stop_apps(self) -> None:
        services = self.active_services()
        if services:
            self.compose("stop", "--timeout", "20", *services, check=False)
        self.app_active = False

    def start_apps(self) -> None:
        self.shared_state("pull", check=False)
        self.compose("up", "-d", "--remove-orphans")
        self.app_active = True
        self.last_app_reconcile = time.monotonic()

    def reconcile_apps(self, *, force: bool = False) -> None:
        """Periodically heal crashed application containers on the current primary.

        Application services intentionally use restart=no so a rebooted standby
        cannot resurrect a stale write-capable stack before Patroni is checked.
        The host controller therefore performs the restart reconciliation while
        and only while this node owns the Patroni primary role.
        """
        now = time.monotonic()
        if not force and now - self.last_app_reconcile < 30:
            return
        self.compose("up", "-d", "--remove-orphans", check=False)
        self.last_app_reconcile = now

    def gateway_ready(self) -> bool:
        url = f"http://127.0.0.1:{self.node.http_port}/health/gateway"
        try:
            with urllib.request.urlopen(url, timeout=3) as response:
                return response.status == 200
        except Exception:
            return False

    def critical_services_ready(self) -> bool:
        """Require the core user path to be healthy before exposing the origin.

        A promoted standby starts many containers at once. Gateway liveness alone
        is not enough: Cloudflare must not send users to a node while identity,
        courses, assignments, solutions, files, or execution API are still
        starting. Optional AI/analyzer/bot services are deliberately excluded.
        """
        existing = set(self.services())
        required = [name for name in self.CORE_READY_SERVICES if name in existing]
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
        script = Path(__file__).resolve().parent / "shared-state.sh"
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

    def patroni_request(
        self,
        node_id: str,
        path: str,
        *,
        method: str = "GET",
        body: dict[str, Any] | None = None,
        timeout: float = 3.0,
    ) -> tuple[int, Any]:
        target = self.cluster.node(node_id)
        url = f"http://{endpoint_host(target.wg_ip)}:{target.patroni_port}{path}"
        data = None if body is None else json.dumps(body).encode("utf-8")
        req = urllib.request.Request(
            url,
            method=method,
            data=data,
            headers={
                "Accept": "application/json",
                "Content-Type": "application/json",
                "Authorization": self._auth_header(),
            },
        )
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
            "applications_active": self.app_active,
            "traffic_ready": self.ready,
            "last_error": self.last_error,
        }
        temp = STATE_FILE.with_suffix(".tmp")
        temp.write_text(json.dumps(state, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        os.chmod(temp, 0o600)
        temp.replace(STATE_FILE)

    def update_role(self) -> tuple[str, str]:
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
        return self.local_role, leader

    def is_primary(self) -> bool:
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

    def maybe_failback(self) -> None:
        if not self.cluster.automatic_failback or self.node.id != self.cluster.preferred_primary:
            self.preferred_replica_since = None
            self.preferred_replica_leader = None
            return
        if self.is_primary():
            self.preferred_replica_since = None
            self.preferred_replica_leader = None
            return
        if (
            not self.leader
            or self.leader == self.node.id
            or not self.replica_within_failback_lag()
            or not self.local_minio_ready()
        ):
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
        if now - self.preferred_replica_since < required:
            return
        if now - self.last_failback_attempt < 120:
            return
        self.last_failback_attempt = now
        # PostgreSQL switchover is gated by Patroni's health and lag checks.
        # MinIO remains deliberately asynchronous: an unavailable optional
        # object replica must not block database failback forever. Operators
        # can run minio-wait-replication.sh before planned maintenance when a
        # full all-node object drain is required.
        body = {"leader": self.leader, "candidate": self.node.id}
        status, response = self.patroni_request(self.leader, "/switchover", method="POST", body=body, timeout=15)
        if 200 <= status < 300:
            log(f"controlled failback requested: {self.leader} -> {self.node.id}")
            self.preferred_replica_since = None
            self.preferred_replica_leader = None
        else:
            log(f"controlled failback rejected by Patroni: status={status} response={response}")

    def tick(self) -> None:
        self.ensure_storage()
        role, leader = self.update_role()
        if self.is_primary():
            if not self.app_active:
                log(f"node {self.node.id} owns the Patroni leader lock; starting TaskForge applications")
                self.start_apps()
            # The primary periodically reconciles the whole application stack,
            # not only the gateway. A worker/API may crash while the gateway
            # remains healthy, and restart=no is deliberate standby protection.
            self.reconcile_apps()
            if self.gateway_ready() and self.critical_services_ready():
                self._set_ready(True)
            else:
                self._set_ready(False)
                self.reconcile_apps(force=True)
            now = time.monotonic()
            interval = int(self.cluster.raw["shared_state_interval_seconds"])
            if now - self.last_shared_push >= interval:
                self.shared_state("push", check=False)
                self.last_shared_push = now
        else:
            if self.ready or self.app_active:
                log(f"node {self.node.id} is not primary (role={role}, leader={leader or 'none'}); stopping TaskForge applications")
            self._set_ready(False)
            now = time.monotonic()
            if self.app_active or now - self.last_standby_reconcile >= 60:
                self.stop_apps()
                self.last_standby_reconcile = now
            self.maybe_failback()
        self.last_error = ""
        self._write_state()

    def run_forever(self) -> None:
        log(f"starting node={self.node.id} cluster={self.cluster.name} preferred={self.cluster.preferred_primary}")
        while not self.shutdown.is_set():
            try:
                with self.lock:
                    self.tick()
            except Exception as exc:
                self.last_error = f"{type(exc).__name__}: {exc}"
                log(self.last_error)
                self._set_ready(False)
                self._write_state()
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
            "cluster_config_sha256": CONFIG_HASH_FILE.read_text(encoding="utf-8").strip() if CONFIG_HASH_FILE.is_file() else "",
            "traffic_ready": self.ready,
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
        if self.path == "/ha/live":
            self._reply(200, {"status": "ok", "cluster": state.get("cluster"), "node": state.get("node")})
            return
        if self.path in {"/ha/traffic-ready", "/ha/primary-ready"}:
            ready = bool(state.get("traffic_ready"))
            self._reply(200 if ready else 503, {"status": "ready" if ready else "standby", **state})
            return
        if self.path == "/ha/status":
            self._reply(200, state)
            return
        self._reply(404, {"error": "not-found"})


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", default=os.environ.get("TASKFORGE_CLUSTER_CONFIG", str(DEFAULT_CONFIG)))
    parser.add_argument("--env-file", default=os.environ.get("TASKFORGE_ENV_FILE", str(DEFAULT_ENV_FILE)))
    parser.add_argument("--once", action="store_true")
    parser.add_argument("--status", action="store_true")
    args = parser.parse_args()
    controller = Controller(Path(args.config), Path(args.env_file))
    if args.status:
        print(json.dumps(controller.public_state(), ensure_ascii=False, indent=2))
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
