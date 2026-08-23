#!/usr/bin/env python3
"""TaskForge N-node cluster configuration renderer.

Only the public topology lives in cluster.json. Existing TaskForge secrets stay
in .env. Per-node generated files live in .runtime/cluster and are never meant
for Git.
"""
from __future__ import annotations

import argparse
import base64
import dataclasses
import hashlib
import hmac
import ipaddress
import json
import os
from pathlib import Path
import re
import socket
import sys
import urllib.error
import urllib.request
from typing import Any, Iterable

NODE_RE = re.compile(r"^[A-Za-z][A-Za-z0-9_-]{0,31}$")
HOST_RE = re.compile(r"^[A-Za-z0-9_.:-]+$")
TRUE_VALUES = {"1", "true", "yes", "on"}
PLACEHOLDER_PREFIXES = ("CHANGE_ME", "REPLACE_ME")
CLOUDFLARE_DIRECT_HTTP_PORTS = {80, 8080, 8880, 2052, 2082, 2086, 2095}
CLOUDFLARE_DIRECT_HTTPS_PORTS = {443, 2053, 2083, 2087, 2096, 8443}


def find_layout() -> tuple[Path, Path, Path, Path]:
    here = Path(__file__).resolve().parent
    if (here.parent / "compose.sh").is_file():
        root = here.parent
        return root, root / "compose.sh", root / ".env", here
    root = here.parent.parent
    compose = root / "deploy/prod/compose.sh"
    if compose.is_file():
        return root, compose, root / "deploy/prod/.env", here
    raise RuntimeError("cannot determine TaskForge repository/server-bundle layout")


ROOT, COMPOSE_SCRIPT, DEFAULT_ENV_FILE, CLUSTER_DIR = find_layout()
RUNTIME_DIR = ROOT / ".runtime/cluster"
DEFAULT_CONFIG = CLUSTER_DIR / "cluster.json"
EXAMPLE_CONFIG = CLUSTER_DIR / "cluster.example.json"
NODE_ID_FILE = RUNTIME_DIR / "node-id"
ENABLED_FILE = RUNTIME_DIR / "enabled"


def chown_to_deployment_owner(path: Path) -> None:
    """Keep generated runtime files usable by the account owning the bundle.

    Bootstrap commands run with sudo and the controller runs as root, while the
    existing operational scripts are normally invoked by the Docker-group user.
    Root-owned 0600 node.env/patroni files would make ordinary `check.sh` and
    `compose.sh` calls fail after a service restart.
    """
    if os.geteuid() != 0:
        return
    owner = ROOT.stat()
    os.chown(path, owner.st_uid, owner.st_gid)


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
        if len(value) >= 2 and value[0] == value[-1] and value[0] in {"'", '"'}:
            value = value[1:-1]
        values[key] = value
    return values


def secret_from(key: str, purpose: str, length: int = 64) -> str:
    """Derive a stable per-purpose secret from the existing high-entropy internal key."""
    digest = hmac.new(key.encode("utf-8"), purpose.encode("utf-8"), hashlib.sha512).hexdigest()
    return digest[:length]


def is_placeholder(value: str) -> bool:
    upper = value.upper()
    return any(upper.startswith(prefix) or prefix in upper for prefix in PLACEHOLDER_PREFIXES)


def require_port(value: Any, label: str, errors: list[str]) -> int | None:
    try:
        port = int(value)
    except (TypeError, ValueError):
        errors.append(f"{label} must be an integer")
        return None
    if not 1 <= port <= 65535:
        errors.append(f"{label} must be between 1 and 65535")
        return None
    return port


def endpoint_host(host: str) -> str:
    try:
        parsed = ipaddress.ip_address(host)
        return f"[{parsed}]" if parsed.version == 6 else str(parsed)
    except ValueError:
        return host


@dataclasses.dataclass(frozen=True)
class Node:
    raw: dict[str, Any]

    @property
    def id(self) -> str:
        return str(self.raw["id"])

    @property
    def priority(self) -> int:
        return int(self.raw["priority"])

    @property
    def public_host(self) -> str:
        return str(self.raw["public_host"])

    @property
    def platform(self) -> str:
        return str(self.raw.get("platform", "linux"))

    @property
    def dcs_voter(self) -> bool:
        return bool(self.raw.get("dcs_voter", False))

    @property
    def wg_ip(self) -> str:
        return str(self.raw["wireguard"]["ip"])

    @property
    def wg_port(self) -> int:
        return int(self.raw["wireguard"]["listen_port"])

    @property
    def wg_public_key(self) -> str:
        return str(self.raw["wireguard"]["public_key"])

    @property
    def http_port(self) -> int:
        return int(self.raw["web"]["http_port"])

    @property
    def https_port(self) -> int:
        return int(self.raw["web"]["https_port"])

    @property
    def tls_mode(self) -> str:
        return str(self.raw["web"].get("tls_mode", "origin-ca"))

    @property
    def postgres_port(self) -> int:
        return int(self.raw["postgres"]["host_port"])

    @property
    def patroni_port(self) -> int:
        return int(self.raw["postgres"]["patroni_rest_port"])

    @property
    def etcd_client_port(self) -> int:
        return int(self.raw.get("etcd", {}).get("client_port", 2379))

    @property
    def etcd_peer_port(self) -> int:
        return int(self.raw.get("etcd", {}).get("peer_port", 2380))

    @property
    def minio_port(self) -> int:
        return int(self.raw["minio"]["api_port"])

    @property
    def minio_console_port(self) -> int:
        return int(self.raw["minio"]["console_port"])

    @property
    def health_port(self) -> int:
        return int(self.raw.get("health_port", 9187))


@dataclasses.dataclass(frozen=True)
class Cluster:
    raw: dict[str, Any]
    nodes: tuple[Node, ...]
    network: ipaddress.IPv4Network | ipaddress.IPv6Network

    @property
    def name(self) -> str:
        return str(self.raw["cluster_name"])

    @property
    def preferred_primary(self) -> str:
        return str(self.raw["preferred_primary"])

    @property
    def automatic_failback(self) -> bool:
        return bool(self.raw.get("automatic_failback", True))

    @property
    def voters(self) -> tuple[Node, ...]:
        return tuple(n for n in self.nodes if n.dcs_voter)

    def node(self, node_id: str) -> Node:
        for node in self.nodes:
            if node.id == node_id:
                return node
        raise KeyError(node_id)

    @classmethod
    def load(cls, path: Path, *, allow_placeholders: bool = False) -> "Cluster":
        try:
            raw = json.loads(path.read_text(encoding="utf-8"))
        except FileNotFoundError as exc:
            raise ValueError(f"cluster config not found: {path}") from exc
        except json.JSONDecodeError as exc:
            raise ValueError(f"invalid JSON in {path}: {exc}") from exc
        if not isinstance(raw, dict):
            raise ValueError("cluster config root must be an object")
        errors: list[str] = []
        if raw.get("schema_version") != 1:
            errors.append("schema_version must be 1")
        name = str(raw.get("cluster_name", ""))
        if not NODE_RE.fullmatch(name):
            errors.append("cluster_name must match [A-Za-z][A-Za-z0-9_-]{0,31}")
        try:
            network = ipaddress.ip_network(str(raw.get("wireguard_cidr", "")), strict=False)
        except ValueError:
            network = ipaddress.ip_network("10.80.0.0/24")
            errors.append("wireguard_cidr must be a valid IPv4/IPv6 network")
        node_docs = raw.get("nodes")
        if not isinstance(node_docs, list) or len(node_docs) < 3:
            errors.append("nodes must contain at least three full TaskForge nodes")
            node_docs = []
        nodes: list[Node] = []
        ids: set[str] = set()
        priorities: set[int] = set()
        wg_ips: set[str] = set()
        wg_keys: set[bytes] = set()
        wg_endpoints: set[tuple[str, int]] = set()
        for index, doc in enumerate(node_docs):
            label = f"nodes[{index}]"
            if not isinstance(doc, dict):
                errors.append(f"{label} must be an object")
                continue
            node_id = str(doc.get("id", ""))
            if not NODE_RE.fullmatch(node_id):
                errors.append(f"{label}.id is invalid")
            elif node_id in ids:
                errors.append(f"duplicate node id: {node_id}")
            ids.add(node_id)
            try:
                priority = int(doc.get("priority"))
                if priority <= 0:
                    raise ValueError
                if priority in priorities:
                    errors.append(f"duplicate node priority: {priority}")
                priorities.add(priority)
            except (TypeError, ValueError):
                errors.append(f"{label}.priority must be a positive integer")
            platform = str(doc.get("platform", "linux"))
            if platform not in {"linux", "windows-hyperv-vm"}:
                errors.append(f"{label}.platform must be linux or windows-hyperv-vm")
            if not isinstance(doc.get("dcs_voter", False), bool):
                errors.append(f"{label}.dcs_voter must be true or false")
            host = str(doc.get("public_host", ""))
            if not host or "://" in host or not HOST_RE.fullmatch(host):
                errors.append(f"{label}.public_host must be an IP/DNS name without a URL scheme")
            elif is_placeholder(host) and not allow_placeholders:
                errors.append(f"{label}.public_host still contains a placeholder")
            wg = doc.get("wireguard")
            if not isinstance(wg, dict):
                errors.append(f"{label}.wireguard must be an object")
                wg = {}
            try:
                wg_ip = ipaddress.ip_address(str(wg.get("ip", "")))
                if wg_ip not in network:
                    errors.append(f"{label}.wireguard.ip is outside wireguard_cidr")
                if wg_ip == network.network_address or (
                    isinstance(network, ipaddress.IPv4Network) and wg_ip == network.broadcast_address
                ):
                    errors.append(f"{label}.wireguard.ip cannot be the network/broadcast address")
                if str(wg_ip) in wg_ips:
                    errors.append(f"duplicate WireGuard IP: {wg_ip}")
                wg_ips.add(str(wg_ip))
            except ValueError:
                errors.append(f"{label}.wireguard.ip is invalid")
            wg_port = require_port(wg.get("listen_port"), f"{label}.wireguard.listen_port", errors)
            if host and wg_port is not None:
                endpoint = (host.lower(), wg_port)
                if endpoint in wg_endpoints:
                    errors.append(f"duplicate public WireGuard endpoint: {host}:{wg_port}")
                wg_endpoints.add(endpoint)
            public_key = str(wg.get("public_key", ""))
            if is_placeholder(public_key):
                if not allow_placeholders:
                    errors.append(f"{label}.wireguard.public_key still contains a placeholder")
            else:
                try:
                    decoded = base64.b64decode(public_key, validate=True)
                    if len(decoded) != 32:
                        raise ValueError
                    if decoded == b"\x00" * 32:
                        errors.append(f"{label}.wireguard.public_key cannot be the all-zero key")
                    elif decoded in wg_keys:
                        errors.append(f"duplicate WireGuard public key on {label}")
                    else:
                        wg_keys.add(decoded)
                except Exception:
                    errors.append(f"{label}.wireguard.public_key must be a WireGuard base64 public key")
            web = doc.get("web")
            if not isinstance(web, dict):
                errors.append(f"{label}.web must be an object")
                web = {}
            require_port(web.get("http_port"), f"{label}.web.http_port", errors)
            require_port(web.get("https_port"), f"{label}.web.https_port", errors)
            if web.get("tls_mode", "origin-ca") not in {"origin-ca", "http"}:
                errors.append(f"{label}.web.tls_mode must be origin-ca or http")
            pg = doc.get("postgres")
            if not isinstance(pg, dict):
                errors.append(f"{label}.postgres must be an object")
                pg = {}
            require_port(pg.get("host_port"), f"{label}.postgres.host_port", errors)
            require_port(pg.get("patroni_rest_port"), f"{label}.postgres.patroni_rest_port", errors)
            etcd = doc.get("etcd", {})
            if doc.get("dcs_voter", False):
                if not isinstance(etcd, dict):
                    errors.append(f"{label}.etcd must be an object for a DCS voter")
                    etcd = {}
                require_port(etcd.get("client_port"), f"{label}.etcd.client_port", errors)
                require_port(etcd.get("peer_port"), f"{label}.etcd.peer_port", errors)
            minio = doc.get("minio")
            if not isinstance(minio, dict):
                errors.append(f"{label}.minio must be an object")
                minio = {}
            require_port(minio.get("api_port"), f"{label}.minio.api_port", errors)
            require_port(minio.get("console_port"), f"{label}.minio.console_port", errors)
            require_port(doc.get("health_port", 9187), f"{label}.health_port", errors)
            # Host ports only need to be unique on the same physical/VM node
            # and within the same transport protocol. WireGuard is UDP, while
            # every other listener below is TCP, so UDP 8443 and TCP 8443 are
            # a valid combination when a constrained home router requires it.
            tcp_ports: list[int] = []
            for source in (
                web.get("http_port"), web.get("https_port"),
                pg.get("host_port"), pg.get("patroni_rest_port"),
                minio.get("api_port"), minio.get("console_port"), doc.get("health_port", 9187),
            ):
                try:
                    tcp_ports.append(int(source))
                except (TypeError, ValueError):
                    pass
            if doc.get("dcs_voter", False):
                for source in (etcd.get("client_port"), etcd.get("peer_port")):
                    try:
                        tcp_ports.append(int(source))
                    except (TypeError, ValueError):
                        pass
            duplicates = sorted({p for p in tcp_ports if tcp_ports.count(p) > 1})
            if duplicates:
                errors.append(f"{label} reuses TCP port(s): {', '.join(map(str, duplicates))}")
            try:
                nodes.append(Node(doc))
            except Exception:
                errors.append(f"{label} is incomplete")
        preferred = str(raw.get("preferred_primary", ""))
        if preferred not in ids:
            errors.append("preferred_primary must reference an existing node")
        elif nodes:
            preferred_node = next((n for n in nodes if n.id == preferred), None)
            max_priority = max(n.priority for n in nodes)
            if preferred_node is not None and preferred_node.priority != max_priority:
                errors.append("preferred_primary must have the highest node priority")
        voters = [n for n in nodes if n.dcs_voter]
        if len(voters) < 3 or len(voters) % 2 == 0:
            errors.append("dcs_voter count must be an odd number of at least three")
        for key in (
            "failback_delay_seconds", "failback_stable_seconds",
            "maximum_lag_on_failover_bytes", "maximum_lag_on_failback_bytes",
            "shared_state_interval_seconds",
        ):
            try:
                if int(raw.get(key, 0)) <= 0:
                    raise ValueError
            except (TypeError, ValueError):
                errors.append(f"{key} must be a positive integer")
        if not isinstance(raw.get("automatic_failback", True), bool):
            errors.append("automatic_failback must be true or false")
        image = str(raw.get("etcd_image", ""))
        if not image or ":" not in image:
            errors.append("etcd_image must be a pinned image with a tag")
        if errors:
            raise ValueError("\n".join(errors))
        return cls(raw=raw, nodes=tuple(nodes), network=network)


def config_path(args: argparse.Namespace) -> Path:
    return Path(args.config or os.environ.get("TASKFORGE_CLUSTER_CONFIG", DEFAULT_CONFIG))


def current_node_id(explicit: str | None = None) -> str:
    if explicit:
        return explicit
    value = os.environ.get("TASKFORGE_NODE_ID", "").strip()
    if value:
        return value
    if NODE_ID_FILE.is_file():
        return NODE_ID_FILE.read_text(encoding="utf-8").strip()
    raise ValueError("local node id is not set; run sudo cluster/set-node.sh NODE_ID")


def env_line(key: str, value: Any) -> str:
    text = str(value)
    if "\n" in text or "\r" in text:
        raise ValueError(f"environment value {key} contains a newline")
    return f"{key}={text}"


def render(cluster: Cluster, node: Node, env_file: Path) -> dict[str, Path]:
    env = parse_env(env_file)
    internal_key = env.get("TASKFORGE_INTERNAL_KEY", "")
    pg_password = env.get("POSTGRES_PASSWORD", "")
    pg_user = env.get("POSTGRES_USER", "taskforge")
    if len(internal_key) < 40:
        raise ValueError("TASKFORGE_INTERNAL_KEY in .env must contain at least 40 characters")
    if len(pg_password) < 24:
        raise ValueError("POSTGRES_PASSWORD in .env must contain at least 24 characters")
    RUNTIME_DIR.mkdir(parents=True, exist_ok=True)
    os.chmod(RUNTIME_DIR, 0o700)
    chown_to_deployment_owner(RUNTIME_DIR)
    voters = cluster.voters
    etcd_initial = ",".join(
        f"{v.id}=http://{endpoint_host(v.wg_ip)}:{v.etcd_peer_port}" for v in voters
    )
    etcd_endpoints = ",".join(
        f"http://{endpoint_host(v.wg_ip)}:{v.etcd_client_port}" for v in voters
    )
    tls_mode = node.tls_mode
    gateway_mode = "http" if tls_mode == "http" else "https"
    node_env = [
        env_line("TASKFORGE_CLUSTER_ENABLED", "true"),
        env_line("CLUSTER_NODE_ID", node.id),
        env_line("HA_NODE_ID", node.id),
        env_line("CLUSTER_NODE_PRIORITY", node.priority),
        env_line("CLUSTER_DCS_VOTER", str(node.dcs_voter).lower()),
        env_line("CLUSTER_WG_IP", node.wg_ip),
        env_line("CLUSTER_POSTGRES_BIND", node.wg_ip),
        env_line("CLUSTER_POSTGRES_PORT", node.postgres_port),
        env_line("CLUSTER_PATRONI_BIND", node.wg_ip),
        env_line("CLUSTER_PATRONI_REST_PORT", node.patroni_port),
        env_line("CLUSTER_ETCD_BIND", node.wg_ip),
        env_line("CLUSTER_ETCD_CLIENT_PORT", node.etcd_client_port),
        env_line("CLUSTER_ETCD_PEER_PORT", node.etcd_peer_port),
        env_line("CLUSTER_ETCD_INITIAL_CLUSTER", etcd_initial),
        env_line("CLUSTER_ETCD_ENDPOINTS", etcd_endpoints),
        env_line("CLUSTER_ETCD_TOKEN", cluster.name + "-etcd-v1"),
        env_line("ETCD_IMAGE", cluster.raw["etcd_image"]),
        env_line("PUBLIC_HTTP_PORT", node.http_port),
        env_line("PUBLIC_HTTPS_PORT", node.https_port),
        env_line("POSTGRES_BIND", node.wg_ip),
        env_line("POSTGRES_PORT", node.postgres_port),
        env_line("MINIO_BIND", node.wg_ip),
        env_line("MINIO_PORT", node.minio_port),
        env_line("MINIO_CONSOLE_BIND", node.wg_ip),
        env_line("MINIO_CONSOLE_PORT", node.minio_console_port),
        env_line("CLUSTER_HEALTH_BIND", node.wg_ip),
        env_line("CLUSTER_HEALTH_PORT", node.health_port),
        env_line("GATEWAY_MODE", gateway_mode),
        env_line("GATEWAY_TLS_CERT_FILE", "/etc/taskforge-origin-tls/fullchain.pem"),
        env_line("GATEWAY_TLS_KEY_FILE", "/etc/taskforge-origin-tls/privkey.pem"),
    ]
    node_env_path = RUNTIME_DIR / "node.env"
    node_env_path.write_text("\n".join(node_env) + "\n", encoding="utf-8")
    os.chmod(node_env_path, 0o600)
    chown_to_deployment_owner(node_env_path)

    replication_password = secret_from(internal_key, "taskforge-postgres-replication-v2")
    rewind_password = secret_from(internal_key, "taskforge-postgres-rewind-v2")
    api_password = secret_from(internal_key, "taskforge-patroni-rest-v2")
    pgdata = "/var/lib/postgresql/18/docker"
    patroni = {
        "scope": cluster.name,
        "namespace": "/taskforge/patroni/",
        "name": node.id,
        "restapi": {
            "listen": "0.0.0.0:8008",
            "connect_address": f"{node.wg_ip}:{node.patroni_port}",
            "authentication": {"username": "taskforge_patroni", "password": api_password},
        },
        "etcd3": {"hosts": [f"{v.wg_ip}:{v.etcd_client_port}" for v in voters]},
        "bootstrap": {
            "dcs": {
                "ttl": 30,
                "loop_wait": 10,
                "retry_timeout": 10,
                "maximum_lag_on_failover": int(cluster.raw["maximum_lag_on_failover_bytes"]),
                "check_timeline": True,
                "failsafe_mode": True,
                "postgresql": {
                    "use_pg_rewind": True,
                    "use_slots": True,
                    "remove_data_directory_on_rewind_failure": True,
                    "remove_data_directory_on_diverged_timelines": True,
                    "parameters": {
                        "wal_level": "replica",
                        "hot_standby": "on",
                        "max_wal_senders": max(20, len(cluster.nodes) * 4),
                        "max_replication_slots": max(20, len(cluster.nodes) * 4),
                        "wal_keep_size": "4096MB",
                        "synchronous_commit": "on",
                        "synchronous_standby_names": "",
                        "password_encryption": "scram-sha-256",
                        "max_connections": 300,
                    },
                },
            },
            "initdb": [{"encoding": "UTF8"}, "data-checksums"],
            "pg_hba": [
                "local all all trust",
                "host all all 127.0.0.1/32 scram-sha-256",
                "host all all ::1/128 scram-sha-256",
                "host all all 10.0.0.0/8 scram-sha-256",
                "host all all 172.16.0.0/12 scram-sha-256",
                "host all all 192.168.0.0/16 scram-sha-256",
                "host all all fd00::/8 scram-sha-256",
                f"host replication taskforge_repl {cluster.network} scram-sha-256",
                f"host all taskforge_rewind {cluster.network} scram-sha-256",
            ],
        },
        "postgresql": {
            "listen": "0.0.0.0:5432",
            "connect_address": f"{node.wg_ip}:{node.postgres_port}",
            "data_dir": pgdata,
            "bin_dir": "/usr/lib/postgresql/18/bin",
            "pgpass": "/var/lib/postgresql/.pgpass",
            "authentication": {
                "superuser": {"username": pg_user, "password": pg_password},
                "replication": {"username": "taskforge_repl", "password": replication_password},
                "rewind": {"username": "taskforge_rewind", "password": rewind_password},
            },
            "create_replica_methods": ["basebackup"],
            "basebackup": {"max-rate": "200M", "checkpoint": "fast"},
            "parameters": {"unix_socket_directories": "/var/run/postgresql"},
        },
        "tags": {
            "failover_priority": node.priority,
            "noloadbalance": False,
        },
        "watchdog": {"mode": "off"},
    }
    patroni_path = RUNTIME_DIR / "patroni.yml"
    patroni_path.write_text(json.dumps(patroni, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.chmod(patroni_path, 0o600)
    chown_to_deployment_owner(patroni_path)

    summary = {
        "cluster": cluster.name,
        "node": node.id,
        "priority": node.priority,
        "preferred_primary": cluster.preferred_primary,
        "dcs_voter": node.dcs_voter,
        "wireguard": {"ip": node.wg_ip, "port": node.wg_port},
        "web": {"http_port": node.http_port, "https_port": node.https_port, "tls_mode": node.tls_mode},
        "postgres": {"port": node.postgres_port, "patroni_rest_port": node.patroni_port},
        "etcd_voters": [v.id for v in voters],
        "health_port": node.health_port,
    }
    summary_path = RUNTIME_DIR / "node-summary.json"
    summary_path.write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.chmod(summary_path, 0o644)
    chown_to_deployment_owner(summary_path)
    hash_path = RUNTIME_DIR / "cluster-config.sha256"
    canonical = json.dumps(cluster.raw, sort_keys=True, separators=(",", ":")).encode("utf-8")
    hash_path.write_text(hashlib.sha256(canonical).hexdigest() + "\n", encoding="utf-8")
    os.chmod(hash_path, 0o600)
    chown_to_deployment_owner(hash_path)
    return {"node_env": node_env_path, "patroni": patroni_path, "summary": summary_path, "hash": hash_path}


def wireguard_config(cluster: Cluster, node: Node, private_key: str) -> str:
    prefix = cluster.network.max_prefixlen
    lines = [
        "[Interface]",
        f"Address = {node.wg_ip}/{prefix}",
        f"ListenPort = {node.wg_port}",
        f"PrivateKey = {private_key.strip()}",
        "SaveConfig = false",
        "",
    ]
    for peer in cluster.nodes:
        if peer.id == node.id:
            continue
        lines += [
            f"# TaskForge node {peer.id}",
            "[Peer]",
            f"PublicKey = {peer.wg_public_key}",
            f"AllowedIPs = {peer.wg_ip}/{prefix}",
            f"Endpoint = {endpoint_host(peer.public_host)}:{peer.wg_port}",
            "PersistentKeepalive = 25",
            "",
        ]
    return "\n".join(lines).rstrip() + "\n"


def request_json(url: str, username: str, password: str, timeout: float = 2.5) -> tuple[int, Any]:
    request = urllib.request.Request(url, headers={"Accept": "application/json"})
    token = base64.b64encode(f"{username}:{password}".encode()).decode()
    request.add_header("Authorization", "Basic " + token)
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            data = response.read()
            return response.status, json.loads(data.decode("utf-8")) if data else None
    except urllib.error.HTTPError as exc:
        data = exc.read()
        try:
            body = json.loads(data.decode("utf-8")) if data else None
        except Exception:
            body = data.decode("utf-8", "replace")
        return exc.code, body
    except Exception as exc:
        return 0, {"error": f"{type(exc).__name__}: {exc}"}


def main() -> int:
    parser = argparse.ArgumentParser(description="TaskForge N-node cluster control")
    parser.add_argument("--config", help="path to cluster.json")
    parser.add_argument("--env-file", help="path to TaskForge .env")
    sub = parser.add_subparsers(dest="command", required=True)
    p_validate = sub.add_parser("validate")
    p_validate.add_argument("--allow-placeholders", action="store_true")
    p_set = sub.add_parser("set-node")
    p_set.add_argument("node_id")
    p_render = sub.add_parser("render")
    p_render.add_argument("--node")
    p_wg = sub.add_parser("render-wireguard")
    p_wg.add_argument("--node")
    p_wg.add_argument("--private-key-file", required=True)
    p_wg.add_argument("--output")
    p_info = sub.add_parser("node-info")
    p_info.add_argument("--node")
    sub.add_parser("cloudflare")
    sub.add_parser("status")
    args = parser.parse_args()
    path = config_path(args)
    allow = bool(getattr(args, "allow_placeholders", False))
    cluster = Cluster.load(path, allow_placeholders=allow)
    if args.command == "validate":
        print(f"cluster config ok: {cluster.name}, nodes={len(cluster.nodes)}, voters={len(cluster.voters)}")
        return 0
    if args.command == "set-node":
        cluster.node(args.node_id)
        RUNTIME_DIR.mkdir(parents=True, exist_ok=True)
        os.chmod(RUNTIME_DIR, 0o700)
        chown_to_deployment_owner(RUNTIME_DIR)
        NODE_ID_FILE.write_text(args.node_id + "\n", encoding="utf-8")
        os.chmod(NODE_ID_FILE, 0o600)
        chown_to_deployment_owner(NODE_ID_FILE)
        print(f"local TaskForge cluster node: {args.node_id}")
        return 0
    node_id = current_node_id(getattr(args, "node", None))
    node = cluster.node(node_id)
    env_file = Path(args.env_file or os.environ.get("TASKFORGE_ENV_FILE", DEFAULT_ENV_FILE))
    if args.command == "render":
        written = render(cluster, node, env_file)
        for label, output in written.items():
            print(f"{label}: {output}")
        return 0
    if args.command == "render-wireguard":
        private_key = Path(args.private_key_file).read_text(encoding="utf-8").strip()
        output = wireguard_config(cluster, node, private_key)
        if args.output:
            target = Path(args.output)
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(output, encoding="utf-8")
            os.chmod(target, 0o600)
            print(target)
        else:
            sys.stdout.write(output)
        return 0
    if args.command == "node-info":
        print(json.dumps(node.raw, ensure_ascii=False, indent=2))
        return 0
    if args.command == "cloudflare":
        # A separate one-endpoint pool per node allows every origin to use its
        # own web and health-check ports. Pool order follows node priority.
        # Explicit Host headers are essential because health monitors otherwise
        # use the raw endpoint address, which does not match TaskForge virtual
        # hosts or the Cloudflare Origin CA certificate.
        domain = parse_env(env_file).get("DOMAIN", "").strip()
        pools = []
        for peer in sorted(cluster.nodes, key=lambda n: n.priority, reverse=True):
            protocol = "https" if peer.tls_mode != "http" else "http"
            pools.append(
                {
                    "name": f"{cluster.name}-{peer.id.lower()}",
                    "priority": peer.priority,
                    "monitor": {
                        "type": protocol,
                        "port": peer.https_port if protocol == "https" else peer.http_port,
                        "path": "/ha/traffic-ready",
                        "expected_codes": "200",
                        "header": ({"Host": [domain]} if domain else {}),
                    },
                    "endpoint": {
                        "name": peer.id,
                        "address": peer.public_host,
                        "port": peer.https_port if protocol == "https" else peer.http_port,
                        "protocol": protocol,
                        "host_header": domain or None,
                        "direct_cloudflare_proxy_supported": (
                            peer.https_port in CLOUDFLARE_DIRECT_HTTPS_PORTS
                            if protocol == "https"
                            else peer.http_port in CLOUDFLARE_DIRECT_HTTP_PORTS
                        ),
                    },
                }
            )
        payload = {
            "traffic_steering": "off",
            "behavior": "active-passive-failover",
            "pool_order": [pool["name"] for pool in pools],
            "fallback_pool": pools[-1]["name"] if pools else None,
            "pools": pools,
        }
        print(json.dumps(payload, ensure_ascii=False, indent=2))
        return 0
    if args.command == "status":
        env = parse_env(env_file)
        key = env.get("TASKFORGE_INTERNAL_KEY", "")
        password = secret_from(key, "taskforge-patroni-rest-v2") if key else ""
        results = []
        for peer in sorted(cluster.nodes, key=lambda n: n.priority, reverse=True):
            patroni_status, patroni_body = request_json(
                f"http://{endpoint_host(peer.wg_ip)}:{peer.patroni_port}/patroni",
                "taskforge_patroni",
                password,
            )
            agent_status, agent_body = request_json(
                f"http://{endpoint_host(peer.wg_ip)}:{peer.health_port}/ha/status",
                "",
                "",
            )
            results.append({
                "node": peer.id,
                "patroni_http": patroni_status,
                "patroni": patroni_body,
                "agent_http": agent_status,
                "agent": agent_body,
            })
        hashes = {
            str(row.get("agent", {}).get("cluster_config_sha256", ""))
            for row in results
            if isinstance(row.get("agent"), dict) and row.get("agent", {}).get("cluster_config_sha256")
        }
        print(json.dumps({
            "cluster": cluster.name,
            "topology_hashes_match": len(hashes) <= 1,
            "observed_topology_hashes": sorted(hashes),
            "nodes": results,
        }, ensure_ascii=False, indent=2))
        return 0
    return 2


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (ValueError, KeyError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        raise SystemExit(2)
