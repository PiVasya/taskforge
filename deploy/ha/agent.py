#!/usr/bin/env python3
"""TaskForge two-node HA controller.

The controller intentionally uses no third-party Python packages. It runs on the
host, keeps PostgreSQL promotion/rejoin out of containers, exposes tiny health
endpoints for Cloudflare, and uses an authenticated WireGuard-only control API
between A and B.

Safety model:
- normal writes are asynchronous to the standby;
- a reachable primary cooperatively yields before the standby promotes;
- an unreachable primary must be fenced by HA_FENCE_SCRIPT unless the operator
  explicitly enables HA_ALLOW_UNFENCED_FAILOVER=true;
- the preferred node can automatically fail back only after it has rejoined as
  a standby and caught up to the current primary.
"""
from __future__ import annotations

import argparse
import contextlib
import dataclasses
import datetime as dt
import hmac
import http.client
import http.server
import ipaddress
import json
import os
from pathlib import Path
import re
import shlex
import socket
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
from typing import Any, Iterable

LSN_RE = re.compile(r"^[0-9A-Fa-f]+/[0-9A-Fa-f]+$")
TRUE_VALUES = {"1", "true", "yes", "on"}


def utc_now() -> str:
    return dt.datetime.now(dt.timezone.utc).replace(microsecond=0).isoformat()


def log(message: str) -> None:
    print(f"[{utc_now()}] [taskforge-ha] {message}", flush=True)


def parse_env(path: Path) -> dict[str, str]:
    values: dict[str, str] = {}
    if not path.is_file():
        return values
    for raw in path.read_text(encoding="utf-8-sig").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        key = key.strip()
        if key.startswith("export "):
            key = key[7:].strip()
        if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", key):
            continue
        value = value.strip()
        if len(value) >= 2 and value[0] == value[-1] and value[0] in {'"', "'"}:
            value = value[1:-1]
        values[key] = value
    return values


def find_layout() -> tuple[Path, Path, Path]:
    here = Path(__file__).resolve().parent
    # Standalone live-server bundle: <root>/ha/agent.py + <root>/compose.sh
    bundle_root = here.parent
    if (bundle_root / "compose.sh").is_file():
        return bundle_root, bundle_root / "compose.sh", bundle_root / ".env"

    # Repository layout: <root>/deploy/ha/agent.py + <root>/deploy/prod/compose.sh
    repo_root = here.parent.parent
    compose = repo_root / "deploy/prod/compose.sh"
    if compose.is_file():
        return repo_root, compose, repo_root / "deploy/prod/.env"
    raise RuntimeError("cannot determine TaskForge root/compose layout")


ROOT, COMPOSE_SCRIPT, DEFAULT_ENV_FILE = find_layout()
HA_DIR = Path(__file__).resolve().parent


@dataclasses.dataclass(frozen=True)
class Config:
    env_file: Path
    enabled: bool
    node_id: str
    preferred_node: str
    wg_ip: str
    peer_wg_ip: str
    node_a_public_ip: str
    node_b_public_ip: str
    wg_cidr: str
    agent_bind: str
    agent_port: int
    shared_key: str
    postgres_user: str
    postgres_password: str
    postgres_port: int
    postgres_image: str
    repl_user: str
    repl_password: str
    auto_failover: bool
    auto_failback: bool
    allow_unfenced: bool
    fence_script: str
    recover_script: str
    failover_after: int
    service_failover_after: int
    failback_delay: int
    failback_stable: int
    failback_max_lag_bytes: int
    rejoin_after: int
    loop_seconds: int
    operation_timeout: int
    gateway_ready_timeout: int
    minio_drain_timeout: int

    @property
    def peer_node_id(self) -> str:
        return "B" if self.node_id == "A" else "A"

    @property
    def peer_public_ip(self) -> str:
        return self.node_b_public_ip if self.node_id == "A" else self.node_a_public_ip

    @classmethod
    def load(cls, env_file: Path | None = None) -> "Config":
        path = env_file or Path(os.environ.get("TASKFORGE_ENV_FILE", DEFAULT_ENV_FILE))
        env = parse_env(path)

        def value(key: str, default: str = "") -> str:
            return os.environ.get(key, env.get(key, default)).strip()

        def boolean(key: str, default: bool = False) -> bool:
            fallback = "true" if default else "false"
            return value(key, fallback).lower() in TRUE_VALUES

        def integer(key: str, default: int, minimum: int = 0) -> int:
            try:
                result = int(value(key, str(default)))
            except ValueError as exc:
                raise ValueError(f"{key} must be an integer") from exc
            if result < minimum:
                raise ValueError(f"{key} must be >= {minimum}")
            return result

        node_id = value("HA_NODE_ID", "A").upper()
        if node_id not in {"A", "B"}:
            raise ValueError("HA_NODE_ID must be A or B")
        preferred = value("HA_PREFERRED_NODE", "A").upper()
        if preferred not in {"A", "B"}:
            raise ValueError("HA_PREFERRED_NODE must be A or B")

        wg_ip = value("HA_WG_IP")
        peer_wg_ip = value("HA_PEER_WG_IP")
        wg_cidr = value("HA_WG_CIDR", "10.80.0.0/24")
        if boolean("HA_ENABLED", False):
            for key, raw in (("HA_WG_IP", wg_ip), ("HA_PEER_WG_IP", peer_wg_ip)):
                try:
                    ipaddress.ip_address(raw)
                except ValueError as exc:
                    raise ValueError(f"{key} must be a valid IP address") from exc
            try:
                ipaddress.ip_network(wg_cidr, strict=False)
            except ValueError as exc:
                raise ValueError("HA_WG_CIDR must be a valid network") from exc

        return cls(
            env_file=path,
            enabled=boolean("HA_ENABLED", False),
            node_id=node_id,
            preferred_node=preferred,
            wg_ip=wg_ip,
            peer_wg_ip=peer_wg_ip,
            node_a_public_ip=value("HA_NODE_A_PUBLIC_IP"),
            node_b_public_ip=value("HA_NODE_B_PUBLIC_IP"),
            wg_cidr=wg_cidr,
            agent_bind=value("HA_AGENT_BIND", "0.0.0.0"),
            agent_port=integer("HA_AGENT_PORT", 9187, 1),
            shared_key=value("HA_SHARED_KEY"),
            postgres_user=value("POSTGRES_USER", "taskforge"),
            postgres_password=value("POSTGRES_PASSWORD"),
            postgres_port=integer("POSTGRES_PORT", 5432, 1),
            postgres_image=value("POSTGRES_IMAGE", "postgres:18-alpine"),
            repl_user=value("HA_REPLICATION_USER", "taskforge_repl"),
            repl_password=value("HA_REPLICATION_PASSWORD"),
            auto_failover=boolean("HA_AUTO_FAILOVER", False),
            auto_failback=boolean("HA_AUTO_FAILBACK", True),
            allow_unfenced=boolean("HA_ALLOW_UNFENCED_FAILOVER", False),
            fence_script=value("HA_FENCE_SCRIPT"),
            recover_script=value("HA_RECOVER_SCRIPT"),
            failover_after=integer("HA_FAILOVER_AFTER_SECONDS", 30, 5),
            service_failover_after=integer("HA_SERVICE_FAILOVER_AFTER_SECONDS", 90, 10),
            failback_delay=integer("HA_FAILBACK_DELAY_SECONDS", 180, 0),
            failback_stable=integer("HA_FAILBACK_STABLE_SECONDS", 30, 0),
            failback_max_lag_bytes=integer("HA_FAILBACK_MAX_LAG_BYTES", 1048576, 0),
            rejoin_after=integer("HA_REJOIN_AFTER_SECONDS", 120, 30),
            loop_seconds=integer("HA_LOOP_SECONDS", 5, 1),
            operation_timeout=integer("HA_OPERATION_TIMEOUT_SECONDS", 900, 30),
            gateway_ready_timeout=integer("HA_GATEWAY_READY_TIMEOUT_SECONDS", 240, 30),
            minio_drain_timeout=integer("HA_MINIO_DRAIN_TIMEOUT_SECONDS", 300, 30),
        )

    def validate_runtime(self) -> list[str]:
        errors: list[str] = []
        if not self.enabled:
            return errors
        if len(self.shared_key) < 32:
            errors.append("HA_SHARED_KEY must contain at least 32 characters")
        if len(self.repl_password) < 24:
            errors.append("HA_REPLICATION_PASSWORD must contain at least 24 characters")
        if not self.postgres_password:
            errors.append("POSTGRES_PASSWORD is required")
        if self.wg_ip == self.peer_wg_ip:
            errors.append("HA_WG_IP and HA_PEER_WG_IP must differ")
        for key, raw in (("HA_NODE_A_PUBLIC_IP", self.node_a_public_ip), ("HA_NODE_B_PUBLIC_IP", self.node_b_public_ip)):
            if raw:
                try:
                    ipaddress.ip_address(raw)
                except ValueError:
                    errors.append(f"{key} must be an IP address when set")
        if self.auto_failover and not self.fence_script and not self.allow_unfenced:
            errors.append("HA_AUTO_FAILOVER=true requires HA_FENCE_SCRIPT unless HA_ALLOW_UNFENCED_FAILOVER=true")
        if self.operation_timeout < self.minio_drain_timeout + 120:
            errors.append("HA_OPERATION_TIMEOUT_SECONDS must be at least HA_MINIO_DRAIN_TIMEOUT_SECONDS + 120")
        for key, raw in (("HA_FENCE_SCRIPT", self.fence_script), ("HA_RECOVER_SCRIPT", self.recover_script)):
            if raw:
                hook = Path(raw)
                if not hook.is_absolute():
                    hook = ROOT / hook
                if not hook.is_file():
                    errors.append(f"{key} does not exist: {hook}")
                elif not os.access(hook, os.X_OK):
                    errors.append(f"{key} is not executable: {hook}")
        return errors


class CommandError(RuntimeError):
    pass


def run(
    args: Iterable[str],
    *,
    check: bool = True,
    input_text: str | None = None,
    timeout: int | None = None,
    env: dict[str, str] | None = None,
) -> subprocess.CompletedProcess[str]:
    argv = [str(x) for x in args]
    proc = subprocess.run(
        argv,
        input=input_text,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        timeout=timeout,
        env=env,
    )
    if check and proc.returncode != 0:
        detail = proc.stderr.strip() or proc.stdout.strip() or f"exit {proc.returncode}"
        raise CommandError(f"{shlex.join(argv)}: {detail}")
    return proc


class Controller:
    STORAGE_SERVICES = {"postgres", "rabbitmq", "redis", "minio", "watchtower"}

    def __init__(self, cfg: Config):
        self.cfg = cfg
        self.runtime = ROOT / ".runtime/ha"
        self.runtime.mkdir(parents=True, exist_ok=True)
        self.state_path = self.runtime / "state.json"
        self.ready_path = self.runtime / "traffic-ready"
        self.lock = threading.RLock()
        self.shutdown = threading.Event()
        self.state = self._read_state()
        self.peer_unreachable_since: float | None = None
        self.peer_unready_since: float | None = None
        self.replica_caught_up_since: float | None = None
        self.replication_broken_since: float | None = None

    def _read_state(self) -> dict[str, Any]:
        try:
            data = json.loads(self.state_path.read_text(encoding="utf-8"))
            return data if isinstance(data, dict) else {}
        except Exception:
            return {}

    def _write_state(self, **updates: Any) -> None:
        self.state.update(updates)
        self.state["updated_at"] = utc_now()
        tmp = self.state_path.with_suffix(".tmp")
        tmp.write_text(json.dumps(self.state, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        os.chmod(tmp, 0o600)
        tmp.replace(self.state_path)

    def event(self, name: str, detail: str = "") -> None:
        log(f"event={name}" + (f" detail={detail}" if detail else ""))
        self._write_state(last_event=name, last_event_detail=detail, last_event_at=utc_now())

    def _set_ready(self, ready: bool) -> None:
        if ready:
            self.ready_path.write_text(f"{self.cfg.node_id} {utc_now()}\n", encoding="utf-8")
            os.chmod(self.ready_path, 0o644)
        else:
            with contextlib.suppress(FileNotFoundError):
                self.ready_path.unlink()

    def compose(self, *args: str, check: bool = True, timeout: int | None = None) -> subprocess.CompletedProcess[str]:
        env = os.environ.copy()
        env["TASKFORGE_ENV_FILE"] = str(self.cfg.env_file)
        env["TASKFORGE_PROD_ENV_FILE"] = str(self.cfg.env_file)
        return run(
            [str(COMPOSE_SCRIPT), *args],
            check=check,
            timeout=timeout or self.cfg.operation_timeout,
            env=env,
        )

    def services(self) -> list[str]:
        proc = self.compose("config", "--services")
        return [x.strip() for x in proc.stdout.splitlines() if x.strip()]

    def active_services(self) -> list[str]:
        return [x for x in self.services() if x not in self.STORAGE_SERVICES and x != "certbot"]

    def stop_active_services(self) -> None:
        services = self.active_services()
        if services:
            self.compose("stop", "--timeout", "20", *services, check=False)

    def start_storage(self) -> None:
        wanted = [s for s in ("rabbitmq", "redis", "minio") if s in self.services()]
        if wanted:
            self.compose("up", "-d", *wanted)

    def start_full_stack(self) -> None:
        self.compose("up", "-d", "--remove-orphans")

    def container_running(self, service: str) -> bool:
        proc = self.compose("ps", "-q", service, check=False)
        cid = proc.stdout.strip()
        if not cid:
            return False
        inspect = run(["docker", "inspect", "-f", "{{.State.Running}}", cid], check=False)
        return inspect.returncode == 0 and inspect.stdout.strip().lower() == "true"

    def start_postgres(self) -> None:
        self.compose("up", "-d", "postgres")
        deadline = time.monotonic() + 90
        while time.monotonic() < deadline:
            if self.local_pg_query("select 1;", check=False) == "1":
                return
            time.sleep(2)
        raise CommandError("local PostgreSQL did not become ready within 90 seconds")

    def stop_postgres(self) -> None:
        self.compose("stop", "--timeout", "30", "postgres", check=False)

    def local_pg_query(self, sql: str, *, check: bool = True) -> str:
        if not self.container_running("postgres"):
            if check:
                raise CommandError("local postgres container is not running")
            return ""
        proc = self.compose(
            "exec", "-T", "postgres",
            "psql", "-X", "-v", "ON_ERROR_STOP=1", "-U", self.cfg.postgres_user,
            "-d", "postgres", "-Atqc", sql,
            check=check,
            timeout=60,
        )
        return proc.stdout.strip() if proc.returncode == 0 else ""

    def pg_role(self) -> str:
        value = self.local_pg_query("select pg_is_in_recovery();", check=False).lower()
        if value in {"f", "false"}:
            return "primary"
        if value in {"t", "true"}:
            return "standby"
        return "stopped"

    def standby_streaming(self) -> bool:
        if self.pg_role() != "standby":
            return False
        status = self.local_pg_query(
            "select coalesce((select status from pg_stat_wal_receiver limit 1),'');",
            check=False,
        ).lower()
        return status == "streaming"

    def current_lsn(self) -> str:
        role = self.pg_role()
        if role == "primary":
            value = self.local_pg_query("select pg_current_wal_lsn();", check=False)
        elif role == "standby":
            value = self.local_pg_query("select coalesce(pg_last_wal_replay_lsn(),'0/0'::pg_lsn);", check=False)
        else:
            value = ""
        return value if LSN_RE.fullmatch(value or "") else ""

    def replication_lag_bytes(self, peer_lsn: str) -> int | None:
        if self.pg_role() != "standby" or not LSN_RE.fullmatch(peer_lsn or ""):
            return None
        sql = (
            "select greatest(0, pg_wal_lsn_diff(" +
            "'" + peer_lsn + "'::pg_lsn, coalesce(pg_last_wal_replay_lsn(),'0/0'::pg_lsn)))::bigint;"
        )
        raw = self.local_pg_query(sql, check=False)
        try:
            return int(raw)
        except (TypeError, ValueError):
            return None

    def wait_replay(self, target_lsn: str, timeout_seconds: int = 120) -> None:
        if not LSN_RE.fullmatch(target_lsn):
            raise CommandError(f"invalid target LSN: {target_lsn!r}")
        deadline = time.monotonic() + timeout_seconds
        while time.monotonic() < deadline:
            sql = (
                "select coalesce(pg_last_wal_replay_lsn(),'0/0'::pg_lsn) >= "
                f"'{target_lsn}'::pg_lsn;"
            )
            if self.local_pg_query(sql, check=False).lower() in {"t", "true"}:
                return
            time.sleep(1)
        raise CommandError(f"standby did not replay through {target_lsn} in {timeout_seconds}s")

    def configure_replication_source(self) -> None:
        if self.pg_role() != "primary":
            raise CommandError("replication source configuration requires a primary PostgreSQL")
        sql = r"""\
SELECT format('CREATE ROLE %I WITH REPLICATION LOGIN PASSWORD %L', :'repl_user', :'repl_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'repl_user')\gexec
SELECT format('ALTER ROLE %I WITH REPLICATION LOGIN PASSWORD %L', :'repl_user', :'repl_password')\gexec
"""
        env = os.environ.copy()
        env["TASKFORGE_ENV_FILE"] = str(self.cfg.env_file)
        env["TASKFORGE_PROD_ENV_FILE"] = str(self.cfg.env_file)
        proc = run(
            [
                str(COMPOSE_SCRIPT), "exec", "-T", "postgres",
                "psql", "-X", "-v", "ON_ERROR_STOP=1",
                "-v", f"repl_user={self.cfg.repl_user}",
                "-v", f"repl_password={self.cfg.repl_password}",
                "-U", self.cfg.postgres_user, "-d", "postgres",
            ],
            input_text=sql,
            check=False,
            timeout=60,
            env=env,
        )
        if proc.returncode != 0:
            raise CommandError(proc.stderr.strip() or "failed to create replication role")

        peer_ip = ipaddress.ip_address(self.cfg.peer_wg_ip)
        peer_net = f"{peer_ip}/{32 if peer_ip.version == 4 else 128}"
        hba_line = f"host replication {self.cfg.repl_user} {peer_net} scram-sha-256"
        shell = 'grep -Fqx "$1" "$PGDATA/pg_hba.conf" || printf "%s\\n" "$1" >> "$PGDATA/pg_hba.conf"; pg_ctl reload -D "$PGDATA"'
        proc = self.compose(
            "exec", "-T", "-u", "postgres", "postgres", "sh", "-ec", shell, "sh", hba_line,
            check=False,
            timeout=60,
        )
        if proc.returncode != 0:
            raise CommandError(proc.stderr.strip() or "failed to update pg_hba.conf")
        self.event("replication-source-configured", peer_net)

    def _postgres_volume_name(self) -> str:
        self.compose("create", "postgres")
        cid = self.compose("ps", "-aq", "postgres").stdout.strip()
        if not cid:
            raise CommandError("cannot locate postgres compose container")
        raw = run(["docker", "inspect", cid]).stdout
        data = json.loads(raw)[0]
        for mount in data.get("Mounts", []):
            if mount.get("Destination") == "/var/lib/postgresql" and mount.get("Type") == "volume":
                name = mount.get("Name")
                if name:
                    return str(name)
        raise CommandError("cannot locate postgres-data Docker volume")

    def _image_pgdata(self) -> str:
        proc = run([
            "docker", "run", "--rm", "--entrypoint", "sh", self.cfg.postgres_image,
            "-ec", 'printf "%s" "$PGDATA"',
        ], timeout=60)
        value = proc.stdout.strip()
        if not value.startswith("/var/lib/postgresql/"):
            raise CommandError(f"unexpected PGDATA for {self.cfg.postgres_image}: {value!r}")
        return value

    @staticmethod
    def _pgpass_escape(value: str) -> str:
        return value.replace("\\", "\\\\").replace(":", "\\:")

    @staticmethod
    def _postgres_setting_escape(value: str) -> str:
        return value.replace("\\", "\\\\").replace("'", "''")

    def basebackup_from_peer(self) -> None:
        if not self.cfg.peer_wg_ip:
            raise CommandError("HA_PEER_WG_IP is not configured")
        self._set_ready(False)
        self.stop_active_services()
        self.stop_postgres()
        volume = self._postgres_volume_name()
        pgdata = self._image_pgdata()
        self.event("rejoin-start", f"source={self.cfg.peer_wg_ip}")

        prep = (
            'set -eu; mkdir -p "$PGDATA"; '
            'find "$PGDATA" -mindepth 1 -maxdepth 1 -exec rm -rf -- {} +; '
            'rm -f /var/lib/postgresql/.pgpass; '
            'chown -R postgres:postgres /var/lib/postgresql'
        )
        run([
            "docker", "run", "--rm", "-e", f"PGDATA={pgdata}",
            "-v", f"{volume}:/var/lib/postgresql", "--entrypoint", "sh",
            self.cfg.postgres_image, "-ec", prep,
        ], timeout=300)

        env = os.environ.copy()
        env["PGPASSWORD"] = self.cfg.repl_password
        conn = (
            f"host={self.cfg.peer_wg_ip} port={self.cfg.postgres_port} "
            f"user={self.cfg.repl_user} application_name=taskforge-{self.cfg.node_id}"
        )
        run([
            "docker", "run", "--rm", "--network", "host", "--user", "postgres",
            "-e", f"PGDATA={pgdata}", "-e", "PGPASSWORD",
            "-v", f"{volume}:/var/lib/postgresql", "--entrypoint", "pg_basebackup",
            self.cfg.postgres_image,
            "-D", pgdata, "-R", "-X", "stream", "-c", "fast", "-P", "-d", conn,
        ], timeout=self.cfg.operation_timeout, env=env)

        pgpass = ":".join([
            self._pgpass_escape(self.cfg.peer_wg_ip),
            str(self.cfg.postgres_port),
            "*",
            self._pgpass_escape(self.cfg.repl_user),
            self._pgpass_escape(self.cfg.repl_password),
        ])
        primary_conninfo = (
            f"host={self.cfg.peer_wg_ip} port={self.cfg.postgres_port} "
            f"user={self.cfg.repl_user} application_name=taskforge-{self.cfg.node_id} "
            "passfile=/var/lib/postgresql/.pgpass"
        )
        auto_line = "primary_conninfo = '" + self._postgres_setting_escape(primary_conninfo) + "'"
        script = (
            "set -eu\n"
            "umask 077\n"
            f"printf '%s\\n' {shlex.quote(pgpass)} > /var/lib/postgresql/.pgpass\n"
            "chown postgres:postgres /var/lib/postgresql/.pgpass\n"
            f"printf '%s\\n' {shlex.quote(auto_line)} >> \"$PGDATA/postgresql.auto.conf\"\n"
            "chown postgres:postgres \"$PGDATA/postgresql.auto.conf\"\n"
        )
        run([
            "docker", "run", "--rm", "-i", "-e", f"PGDATA={pgdata}",
            "-v", f"{volume}:/var/lib/postgresql", "--entrypoint", "sh",
            self.cfg.postgres_image, "-seu",
        ], input_text=script, timeout=60)

        self.start_postgres()
        if self.pg_role() != "standby":
            raise CommandError("rejoined PostgreSQL did not start in standby mode")
        deadline = time.monotonic() + min(120, self.cfg.operation_timeout)
        while time.monotonic() < deadline:
            if self.standby_streaming():
                break
            time.sleep(2)
        else:
            raise CommandError("rejoined PostgreSQL did not establish WAL streaming")
        self.replication_broken_since = None
        self.event("rejoin-complete", f"source={self.cfg.peer_wg_ip}")
        self._write_state(became_standby_at=time.time())

    def promote(self, *, target_lsn: str | None = None, reason: str = "failover") -> None:
        with self.lock:
            self._set_ready(False)
            self.stop_active_services()
            if not self.container_running("postgres"):
                self.start_postgres()
            if self.pg_role() == "standby":
                if target_lsn:
                    self.wait_replay(target_lsn, min(180, self.cfg.operation_timeout))
                result = self.local_pg_query("select pg_promote(true, 60);", check=True).lower()
                if result not in {"t", "true"}:
                    raise CommandError("pg_promote did not report success")
            if self.pg_role() != "primary":
                raise CommandError("local PostgreSQL is not primary after promotion")
            self.configure_replication_source()
            self.start_full_stack()
            self.wait_gateway_ready()
            self._set_ready(True)
            self._write_state(became_primary_at=time.time(), became_standby_at=None)
            self.event("promoted", reason)

    def wait_gateway_ready(self) -> None:
        deadline = time.monotonic() + self.cfg.gateway_ready_timeout
        while time.monotonic() < deadline:
            if self.gateway_ready():
                return
            time.sleep(3)
        raise CommandError("gateway did not become healthy after promotion/start")

    def gateway_ready(self) -> bool:
        if not self.container_running("gateway"):
            return False
        proc = self.compose(
            "exec", "-T", "gateway", "sh", "-ec",
            "wget -q --spider http://127.0.0.1/health/gateway || curl -fsS http://127.0.0.1/health/gateway >/dev/null",
            check=False,
            timeout=20,
        )
        return proc.returncode == 0

    def ensure_active(self) -> None:
        if self.pg_role() != "primary":
            self._set_ready(False)
            return
        if not self.gateway_ready():
            self._set_ready(False)
            try:
                self.start_full_stack()
            except Exception as exc:
                log(f"active stack start failed: {exc}")
                return
        if self.gateway_ready():
            self._set_ready(True)
        else:
            self._set_ready(False)

    def ensure_standby(self) -> None:
        self._set_ready(False)
        self.stop_active_services()
        self.start_storage()
        if not self.container_running("postgres"):
            self.start_postgres()
        if self.pg_role() == "primary":
            raise CommandError("refusing standby mode while local PostgreSQL is primary")

    def apply_update(self) -> None:
        """Apply already-pulled images without accidentally activating a standby."""
        role = self.pg_role()
        self.event("apply-update-start", f"role={role}")
        if role == "primary":
            self._set_ready(False)
            self.start_full_stack()
            self.wait_gateway_ready()
            self._set_ready(True)
        elif role == "standby":
            self.ensure_standby()
        else:
            peer = self.peer_status()
            if peer and peer.get("postgres_role") == "primary":
                self.basebackup_from_peer()
                self.ensure_standby()
            else:
                raise CommandError("cannot apply HA update while PostgreSQL role is unknown and peer is not primary")
        self.event("apply-update-complete", f"role={self.pg_role()}")

    def wait_minio_replication(self) -> None:
        """Wait until this active node has no pending MinIO bucket replication.

        This runs only during a cooperative handoff, after application writers
        have been stopped. Hard failover cannot drain a dead primary and keeps
        the explicitly accepted asynchronous-storage RPO.
        """
        helper = HA_DIR / "minio-wait-replication.sh"
        if not helper.is_file():
            raise CommandError(f"MinIO replication drain helper is missing: {helper}")
        env = os.environ.copy()
        env["TASKFORGE_ENV_FILE"] = str(self.cfg.env_file)
        proc = run(
            [str(helper), str(self.cfg.minio_drain_timeout)],
            check=False,
            timeout=self.cfg.minio_drain_timeout + 45,
            env=env,
        )
        if proc.returncode != 0:
            detail = proc.stderr.strip() or proc.stdout.strip() or f"exit {proc.returncode}"
            raise CommandError(f"MinIO replication did not drain before handoff: {detail}")

    def sync_handoff_state(self) -> None:
        """Best-effort final copy of TLS/DataProtection state to the peer."""
        helper = HA_DIR / "sync-shared-volumes.sh"
        if not helper.is_file():
            return
        env = os.environ.copy()
        env["TASKFORGE_ENV_FILE"] = str(self.cfg.env_file)
        proc = run([str(helper), "--force"], check=False, timeout=300, env=env)
        if proc.returncode != 0:
            detail = proc.stderr.strip() or proc.stdout.strip() or f"exit {proc.returncode}"
            log(f"warning: final shared-volume sync failed before handoff: {detail}")

    def _restore_primary_after_aborted_yield(self, error: Exception) -> None:
        """Bring the local primary back online if pre-handoff validation fails."""
        try:
            role = self.pg_role()
            if role == "stopped":
                self.start_postgres()
                role = self.pg_role()
            if role == "primary":
                self.start_full_stack()
                self.wait_gateway_ready()
                self._set_ready(True)
                self.event("yield-aborted-restored", f"{type(error).__name__}: {error}")
            else:
                self._set_ready(False)
                self.event("yield-aborted-needs-operator", f"role={role}; {type(error).__name__}: {error}")
        except Exception as restore_error:
            self._set_ready(False)
            self.event(
                "yield-aborted-restore-failed",
                f"original={type(error).__name__}: {error}; restore={type(restore_error).__name__}: {restore_error}",
            )

    def yield_primary(self, reason: str) -> dict[str, Any]:
        with self.lock:
            if self.pg_role() != "primary":
                return {"ok": False, "error": "local PostgreSQL is not primary"}
            self.event("yield-start", reason)
            self._set_ready(False)
            self.stop_active_services()
            try:
                # No TaskForge writer is running now, so the MinIO backlog has a
                # fixed upper bound and can converge before traffic moves.
                self.wait_minio_replication()
                self.sync_handoff_state()
                self.local_pg_query("checkpoint;", check=True)
                final_lsn = self.current_lsn()
                if not final_lsn:
                    raise CommandError("could not read final primary WAL LSN")
                self.stop_postgres()
            except Exception as exc:
                self._restore_primary_after_aborted_yield(exc)
                raise
            self._write_state(became_primary_at=None)
            self.event("yield-complete", f"reason={reason} lsn={final_lsn}")
            return {"ok": True, "target_lsn": final_lsn, "node": self.cfg.node_id}

    def fence_peer(self) -> bool:
        if self.cfg.fence_script:
            script = Path(self.cfg.fence_script)
            if not script.is_absolute():
                script = ROOT / script
            if not script.is_file():
                log(f"fence script missing: {script}")
                return False
            env = os.environ.copy()
            env.update({
                "HA_TARGET_NODE_ID": self.cfg.peer_node_id,
                "HA_TARGET_WG_IP": self.cfg.peer_wg_ip,
                "HA_TARGET_PUBLIC_IP": self.cfg.peer_public_ip,
                "HA_LOCAL_NODE_ID": self.cfg.node_id,
            })
            log(f"running fencing hook for node {self.cfg.peer_node_id}")
            proc = run([str(script)], check=False, timeout=120, env=env)
            if proc.returncode == 0:
                self.event("peer-fenced", self.cfg.peer_node_id)
                return True
            log("fencing hook failed: " + (proc.stderr.strip() or proc.stdout.strip() or str(proc.returncode)))
            return False
        if self.cfg.allow_unfenced:
            self.event("unsafe-unfenced-failover", self.cfg.peer_node_id)
            return True
        return False


    def recover_peer_after_fence(self) -> None:
        """Best-effort power-on/recovery hook after this node is safely primary.

        Fencing must finish first and promotion must already have completed. The
        recovery hook may then power the old node back on so it can rejoin as a
        standby and, when it is the preferred node, participate in auto-failback.
        """
        if not self.cfg.recover_script:
            self.event("peer-recovery-manual", self.cfg.peer_node_id)
            return
        script = Path(self.cfg.recover_script)
        if not script.is_absolute():
            script = ROOT / script
        if not script.is_file():
            log(f"recover script missing: {script}")
            self.event("peer-recovery-hook-missing", str(script))
            return
        env = os.environ.copy()
        env.update({
            "HA_TARGET_NODE_ID": self.cfg.peer_node_id,
            "HA_TARGET_WG_IP": self.cfg.peer_wg_ip,
            "HA_TARGET_PUBLIC_IP": self.cfg.peer_public_ip,
            "HA_LOCAL_NODE_ID": self.cfg.node_id,
        })
        log(f"running recovery hook for fenced node {self.cfg.peer_node_id}")
        try:
            proc = run([str(script)], check=False, timeout=120, env=env)
            if proc.returncode == 0:
                self.event("peer-recovery-requested", self.cfg.peer_node_id)
                return
            detail = proc.stderr.strip() or proc.stdout.strip() or str(proc.returncode)
        except Exception as exc:
            detail = f"{type(exc).__name__}: {exc}"
        # Recovery happens after successful promotion and must never make the new
        # primary unavailable merely because provider power-on failed.
        log(f"warning: peer recovery hook failed after successful promotion: {detail}")
        self.event("peer-recovery-hook-failed", detail[:500])

    def _authorized_request(self, req: urllib.request.Request, timeout: int = 5) -> dict[str, Any] | None:
        req.add_header("Authorization", f"Bearer {self.cfg.shared_key}")
        try:
            with urllib.request.urlopen(req, timeout=timeout) as response:
                data = json.loads(response.read(1024 * 1024).decode("utf-8"))
                return data if isinstance(data, dict) else None
        except (urllib.error.URLError, TimeoutError, socket.timeout, json.JSONDecodeError, ConnectionError):
            return None

    def peer_public_live(self) -> bool:
        if not self.cfg.peer_public_ip:
            return False
        url = f"http://{self.cfg.peer_public_ip}:{self.cfg.agent_port}/ha/live"
        try:
            with urllib.request.urlopen(url, timeout=4) as response:
                return response.status == 200
        except (urllib.error.URLError, TimeoutError, socket.timeout, http.client.HTTPException, ConnectionError):
            return False

    def peer_status(self) -> dict[str, Any] | None:
        if not self.cfg.peer_wg_ip:
            return None
        url = f"http://{self.cfg.peer_wg_ip}:{self.cfg.agent_port}/v1/status"
        return self._authorized_request(urllib.request.Request(url, method="GET"), timeout=4)

    def peer_control(self, action: str, payload: dict[str, Any] | None = None, timeout: int | None = None) -> dict[str, Any] | None:
        body = json.dumps(payload or {}).encode("utf-8")
        url = f"http://{self.cfg.peer_wg_ip}:{self.cfg.agent_port}/v1/control/{action}"
        req = urllib.request.Request(url, data=body, method="POST", headers={"Content-Type": "application/json"})
        return self._authorized_request(req, timeout=timeout or min(30, self.cfg.operation_timeout))

    def snapshot(self, include_peer: bool = False) -> dict[str, Any]:
        role = self.pg_role()
        gateway = self.gateway_ready() if role == "primary" else False
        ready = self.ready_path.is_file() and role == "primary" and gateway
        data: dict[str, Any] = {
            "status": "ok",
            "node": self.cfg.node_id,
            "preferred_node": self.cfg.preferred_node,
            "postgres_role": role,
            "wal_lsn": self.current_lsn(),
            "standby_streaming": self.standby_streaming() if role == "standby" else None,
            "gateway_ready": gateway,
            "traffic_ready": ready,
            "auto_failover": self.cfg.auto_failover,
            "auto_failback": self.cfg.auto_failback,
            "minio_drain_timeout_seconds": self.cfg.minio_drain_timeout,
            "unfenced_failover_allowed": self.cfg.allow_unfenced,
            "last_event": self.state.get("last_event", ""),
            "updated_at": utc_now(),
        }
        if include_peer:
            data["peer"] = self.peer_status()
        return data

    def _try_cooperative_failover(self, peer: dict[str, Any]) -> bool:
        if self.pg_role() != "standby" or peer.get("postgres_role") != "primary":
            return False
        response = self.peer_control(
            "yield",
            {"reason": f"service-failover-to-{self.cfg.node_id}"},
            timeout=min(self.cfg.operation_timeout, max(120, self.cfg.minio_drain_timeout + 120)),
        )
        if not response or not response.get("ok"):
            return False
        target = str(response.get("target_lsn", ""))
        self.promote(target_lsn=target if LSN_RE.fullmatch(target) else None, reason="cooperative-service-failover")
        return True

    def _try_failback_to_preferred(self, peer: dict[str, Any]) -> bool:
        if self.cfg.node_id != self.cfg.preferred_node or not self.cfg.auto_failback:
            self.replica_caught_up_since = None
            return False
        if self.pg_role() != "standby" or peer.get("postgres_role") != "primary":
            self.replica_caught_up_since = None
            return False
        standby_since = float(self.state.get("became_standby_at") or time.time())
        if time.time() - standby_since < self.cfg.failback_delay:
            self.replica_caught_up_since = None
            return False
        peer_lsn = str(peer.get("wal_lsn", ""))
        lag = self.replication_lag_bytes(peer_lsn)
        if lag is None or lag > self.cfg.failback_max_lag_bytes:
            self.replica_caught_up_since = None
            return False
        if self.replica_caught_up_since is None:
            self.replica_caught_up_since = time.monotonic()
            return False
        if time.monotonic() - self.replica_caught_up_since < self.cfg.failback_stable:
            return False

        self.event("failback-start", f"peer={self.cfg.peer_node_id} lag={lag}")
        response = self.peer_control(
            "yield",
            {"reason": f"automatic-failback-to-{self.cfg.node_id}"},
            timeout=min(self.cfg.operation_timeout, max(120, self.cfg.minio_drain_timeout + 120)),
        )
        if not response or not response.get("ok"):
            self.replica_caught_up_since = None
            return False
        target = str(response.get("target_lsn", ""))
        self.promote(target_lsn=target if LSN_RE.fullmatch(target) else None, reason="automatic-failback")
        self.peer_control("rejoin", {"reason": f"follow-{self.cfg.node_id}"}, timeout=5)
        self.replica_caught_up_since = None
        return True

    def reconcile_once(self) -> None:
        if not self.cfg.enabled:
            self._set_ready(False)
            return
        peer = self.peer_status()
        role = self.pg_role()

        # Never auto-resolve two writable primaries. A preference is not proof
        # that one timeline contains the other's writes. Stop advertising both
        # nodes and require operator recovery instead of risking silent data loss.
        if role == "primary" and peer and peer.get("postgres_role") == "primary":
            self._set_ready(False)
            self.event("split-brain-detected", f"peer={self.cfg.peer_node_id}; operator-recovery-required")
            return

        if role == "primary":
            self.peer_unreachable_since = None
            self.peer_unready_since = None
            self.ensure_active()
            return

        if role == "stopped":
            was_primary = bool(self.state.get("became_primary_at"))
            was_standby = bool(self.state.get("became_standby_at"))
            if peer and peer.get("postgres_role") == "primary":
                if was_standby and not was_primary:
                    # A normal standby reboot should not copy the entire database.
                    # Start the existing standby first; the streaming-loss path
                    # below will rebuild it only if it can no longer follow.
                    self.start_postgres()
                    role = self.pg_role()
                    if role == "primary":
                        self.stop_postgres()
                        self.event("unsafe-old-primary-blocked", "expected-standby-started-writable")
                        self.basebackup_from_peer()
                        role = "standby"
                else:
                    # A node that was previously primary may be on a divergent
                    # timeline after the peer was promoted. Never boot that old
                    # writable timeline; replace it from the current authority.
                    self.basebackup_from_peer()
                    role = "standby"
            else:
                if was_standby and not was_primary:
                    self.start_postgres()
                    role = self.pg_role()
                    if role == "primary":
                        self.stop_postgres()
                        self.event("unsafe-old-primary-blocked", "expected-standby-started-writable")
                        return
                elif was_primary:
                    # This is required so a legitimate current primary can reboot
                    # while its standby is unavailable. Safety after external
                    # fencing depends on the fencing provider keeping the old host
                    # powered off until connectivity/rejoin is restored.
                    self.start_postgres()
                    role = self.pg_role()
                    if role == "primary":
                        self.ensure_active()
                        return
                else:
                    self._set_ready(False)
                    self.event("postgres-start-blocked", "role-history-unknown")
                    return

        if role != "standby":
            self._set_ready(False)
            return

        self.ensure_standby()

        if peer and peer.get("postgres_role") == "primary":
            self.peer_unreachable_since = None
            if self.standby_streaming():
                self.replication_broken_since = None
            else:
                if self.replication_broken_since is None:
                    self.replication_broken_since = time.monotonic()
                elif time.monotonic() - self.replication_broken_since >= self.cfg.rejoin_after:
                    self.event("standby-streaming-lost", f"peer={self.cfg.peer_node_id}")
                    self.basebackup_from_peer()
                    return
            if peer.get("traffic_ready"):
                self.peer_unready_since = None
            else:
                if self.peer_unready_since is None:
                    self.peer_unready_since = time.monotonic()
                elif (
                    self.cfg.auto_failover
                    and time.monotonic() - self.peer_unready_since >= self.cfg.service_failover_after
                ):
                    if self._try_cooperative_failover(peer):
                        return
                    self.peer_unready_since = time.monotonic()
            if self._try_failback_to_preferred(peer):
                return
            return

        self.peer_unready_since = None
        self.replica_caught_up_since = None
        self.replication_broken_since = None

        if peer and peer.get("postgres_role") == "standby":
            # Initial/no-primary state: preferred node is the deterministic winner.
            if self.cfg.node_id == self.cfg.preferred_node:
                self.promote(reason="no-primary-preferred-node")
            return

        # Peer agent is unreachable through WireGuard. If its public HA endpoint
        # still answers, treat this as a private-link failure and do not fence a
        # demonstrably live primary.
        if self.peer_public_live():
            self.peer_unreachable_since = None
            self.event("peer-wireguard-unreachable-but-public-live", self.cfg.peer_node_id)
            return

        # The local standby only promotes after a sustained outage and successful
        # fencing (or explicit unsafe override).
        if self.peer_unreachable_since is None:
            self.peer_unreachable_since = time.monotonic()
            return
        if not self.cfg.auto_failover:
            return
        if time.monotonic() - self.peer_unreachable_since < self.cfg.failover_after:
            return
        if self.fence_peer():
            self.promote(reason="peer-unreachable-failover")
            self.peer_unreachable_since = None
            self.recover_peer_after_fence()
        else:
            self.event("failover-blocked-no-fence", self.cfg.peer_node_id)
            self.peer_unreachable_since = time.monotonic()

    def daemon_loop(self) -> None:
        errors = self.cfg.validate_runtime()
        if errors:
            for error in errors:
                log("configuration error: " + error)
            raise SystemExit(2)
        self._set_ready(False)
        self.event("agent-start", f"node={self.cfg.node_id}")
        while not self.shutdown.is_set():
            try:
                with self.lock:
                    self.reconcile_once()
            except Exception as exc:
                self._set_ready(False)
                log(f"reconcile error: {type(exc).__name__}: {exc}")
            self.shutdown.wait(self.cfg.loop_seconds)


class Handler(http.server.BaseHTTPRequestHandler):
    controller: Controller
    server_version = "TaskForgeHA/1"
    sys_version = ""

    def log_message(self, fmt: str, *args: Any) -> None:
        log("http " + (fmt % args))

    def _json(self, status: int, payload: dict[str, Any]) -> None:
        data = (json.dumps(payload, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _authorized(self) -> bool:
        header = self.headers.get("Authorization", "")
        expected = f"Bearer {self.controller.cfg.shared_key}"
        return bool(self.controller.cfg.shared_key) and hmac.compare_digest(header, expected)

    def _peer_control_allowed(self) -> bool:
        try:
            remote = ipaddress.ip_address(self.client_address[0])
            peer = ipaddress.ip_address(self.controller.cfg.peer_wg_ip)
            return remote == peer
        except ValueError:
            return False

    def _read_json(self) -> dict[str, Any]:
        try:
            length = int(self.headers.get("Content-Length", "0"))
        except ValueError:
            length = 0
        if length < 0 or length > 65536:
            raise ValueError("invalid request size")
        if not length:
            return {}
        data = json.loads(self.rfile.read(length).decode("utf-8"))
        return data if isinstance(data, dict) else {}

    def do_GET(self) -> None:  # noqa: N802
        if self.path == "/ha/live":
            self._json(200, {"status": "ok", "node": self.controller.cfg.node_id})
            return
        if self.path == "/ha/traffic-ready":
            ready = self.controller.ready_path.is_file()
            self._json(200 if ready else 503, {
                "status": "ready" if ready else "standby",
                "node": self.controller.cfg.node_id,
            })
            return
        if self.path == "/v1/status":
            if not self._authorized():
                self._json(401, {"error": "unauthorized"})
                return
            self._json(200, self.controller.snapshot())
            return
        self._json(404, {"error": "not-found"})

    def do_POST(self) -> None:  # noqa: N802
        if not self.path.startswith("/v1/control/"):
            self._json(404, {"error": "not-found"})
            return
        if not self._authorized():
            self._json(401, {"error": "unauthorized"})
            return
        if not self._peer_control_allowed():
            self._json(403, {"error": "control endpoint is WireGuard-peer only"})
            return
        try:
            payload = self._read_json()
            action = self.path.rsplit("/", 1)[-1]
            if action == "yield":
                result = self.controller.yield_primary(str(payload.get("reason", "peer-request")))
                self._json(200 if result.get("ok") else 409, result)
                return
            if action == "rejoin":
                # Do not block the peer's HTTP request for a potentially long base backup.
                def worker() -> None:
                    try:
                        with self.controller.lock:
                            self.controller.basebackup_from_peer()
                    except Exception as exc:
                        log(f"peer-requested rejoin failed: {exc}")
                threading.Thread(target=worker, daemon=True, name="ha-rejoin").start()
                self._json(202, {"ok": True, "status": "rejoin-started"})
                return
            self._json(404, {"error": "unknown-control-action"})
        except Exception as exc:
            log(f"control request failed: {type(exc).__name__}: {exc}")
            self._json(500, {"error": str(exc)})


def serve(controller: Controller) -> None:
    Handler.controller = controller
    server = http.server.ThreadingHTTPServer((controller.cfg.agent_bind, controller.cfg.agent_port), Handler)
    server.daemon_threads = True
    log(f"health/control API listening on {controller.cfg.agent_bind}:{controller.cfg.agent_port}")
    try:
        server.serve_forever(poll_interval=0.5)
    finally:
        server.server_close()


def status_command(controller: Controller, peer: bool) -> int:
    print(json.dumps(controller.snapshot(include_peer=peer), ensure_ascii=False, indent=2))
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description="TaskForge two-node HA controller")
    parser.add_argument("--env-file", type=Path, default=None)
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("daemon")
    status = sub.add_parser("status")
    status.add_argument("--peer", action="store_true")
    sub.add_parser("reconcile")
    sub.add_parser("apply-update")
    sub.add_parser("configure-primary")
    rejoin = sub.add_parser("rejoin")
    rejoin.add_argument("--yes", action="store_true", help="required because local PostgreSQL data is replaced")
    promote = sub.add_parser("promote")
    promote.add_argument("--force", action="store_true", help="required for operator-forced promotion")

    args = parser.parse_args()
    cfg = Config.load(args.env_file)
    controller = Controller(cfg)
    errors = cfg.validate_runtime()
    if cfg.enabled and errors:
        for error in errors:
            print("error: " + error, file=sys.stderr)
        return 2

    if args.command == "status":
        return status_command(controller, args.peer)
    if args.command == "configure-primary":
        controller.start_postgres()
        controller.configure_replication_source()
        controller._write_state(became_primary_at=time.time(), became_standby_at=None)
        controller.event("initial-primary-configured", cfg.node_id)
        return 0
    if args.command == "rejoin":
        if not args.yes:
            print("error: rejoin replaces the local PostgreSQL data directory; pass --yes", file=sys.stderr)
            return 2
        controller.basebackup_from_peer()
        return 0
    if args.command == "promote":
        if not args.force:
            print("error: operator promotion bypasses automatic fencing; pass --force", file=sys.stderr)
            return 2
        controller.promote(reason="operator-force-promote")
        return 0
    if args.command == "reconcile":
        controller.reconcile_once()
        return 0
    if args.command == "apply-update":
        controller.apply_update()
        return 0
    if args.command == "daemon":
        thread = threading.Thread(target=controller.daemon_loop, daemon=True, name="ha-monitor")
        thread.start()
        try:
            serve(controller)
        except KeyboardInterrupt:
            controller.shutdown.set()
            return 0
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
