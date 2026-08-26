#!/usr/bin/env python3
"""TaskForge easy cluster inventory manager.

The inventory contains public topology only. Secrets stay in .env and private
WireGuard keys stay in /etc/wireguard. Two-node replica mode is supported
without pretending that two machines provide a safe consensus quorum. Once
three independent voters exist, this inventory can render the advanced
Patroni/etcd cluster.json used by the quorum controller.
"""
from __future__ import annotations

import argparse
import base64
import ipaddress
import json
import os
from pathlib import Path
import re
from typing import Any, NoReturn

NODE_RE = re.compile(r"^[A-Za-z][A-Za-z0-9_-]{0,31}$")
HOST_RE = re.compile(r"^[A-Za-z0-9_.:-]+$")
HERE = Path(__file__).resolve().parent
if (HERE.parent / "compose.sh").is_file():
    ROOT = HERE.parent
else:
    ROOT = HERE.parent.parent
RUNTIME = ROOT / ".runtime" / "cluster"
DEFAULT_INVENTORY = HERE / "inventory.json"
NODE_ID_FILE = RUNTIME / "node-id"



def atomic_write_text(path: Path, text: str, mode: int | None = None) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(path.name + f".tmp-{os.getpid()}")
    tmp.write_text(text, encoding="utf-8")
    if mode is not None:
        os.chmod(tmp, mode)
    tmp.replace(path)

def fail(message: str) -> NoReturn:
    raise SystemExit(f"error: {message}")


def load_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        fail(f"missing {path}")
    except json.JSONDecodeError as exc:
        fail(f"invalid JSON in {path}: {exc}")


def save_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    tmp.replace(path)


def valid_port(value: Any, label: str, errors: list[str]) -> int | None:
    try:
        port = int(value)
        if not 1 <= port <= 65535:
            raise ValueError
        return port
    except (TypeError, ValueError):
        errors.append(f"{label} must be an integer from 1 to 65535")
        return None


def load_inventory(path: Path, *, allow_placeholders: bool = False) -> dict[str, Any]:
    raw = load_json(path)
    if not isinstance(raw, dict):
        fail("inventory root must be an object")
    errors: list[str] = []
    if raw.get("schema_version") != 1:
        errors.append("schema_version must be 1")
    mode = raw.get("mode", "replica")
    if mode not in {"replica", "quorum"}:
        errors.append("mode must be replica or quorum")
    name = str(raw.get("cluster_name", ""))
    if not NODE_RE.fullmatch(name):
        errors.append("cluster_name is invalid")
    try:
        network = ipaddress.ip_network(str(raw.get("wireguard_cidr", "")), strict=False)
    except ValueError:
        network = ipaddress.ip_network("10.80.0.0/24")
        errors.append("wireguard_cidr is invalid")
    nodes = raw.get("nodes")
    if not isinstance(nodes, list) or len(nodes) < 2:
        errors.append("nodes must contain at least A and B")
        nodes = []
    ids: set[str] = set()
    priorities: set[int] = set()
    wg_ips: set[str] = set()
    wg_endpoints: set[tuple[str, int]] = set()
    keys: set[bytes] = set()
    for index, node in enumerate(nodes):
        label = f"nodes[{index}]"
        if not isinstance(node, dict):
            errors.append(f"{label} must be an object")
            continue
        node_id = str(node.get("id", ""))
        if not NODE_RE.fullmatch(node_id):
            errors.append(f"{label}.id is invalid")
        elif node_id in ids:
            errors.append(f"duplicate node id {node_id}")
        ids.add(node_id)
        try:
            priority = int(node.get("priority"))
            if priority <= 0 or priority in priorities:
                raise ValueError
            priorities.add(priority)
        except (TypeError, ValueError):
            errors.append(f"{label}.priority must be a unique positive integer")
        host = str(node.get("public_host", ""))
        if not host or "://" in host or not HOST_RE.fullmatch(host):
            errors.append(f"{label}.public_host is invalid")
        elif host.startswith("CHANGE_ME") and not allow_placeholders:
            errors.append(f"{label}.public_host still contains a placeholder")
        wg = node.get("wireguard") or {}
        try:
            ip = ipaddress.ip_address(str(wg.get("ip", "")))
            if ip not in network or ip == network.network_address:
                errors.append(f"{label}.wireguard.ip is outside the cluster network")
            if str(ip) in wg_ips:
                errors.append(f"duplicate WireGuard IP {ip}")
            wg_ips.add(str(ip))
        except ValueError:
            errors.append(f"{label}.wireguard.ip is invalid")
        wg_port = valid_port(wg.get("listen_port"), f"{label}.wireguard.listen_port", errors)
        if host and wg_port:
            ep = (host.lower(), wg_port)
            if ep in wg_endpoints:
                errors.append(f"duplicate public WireGuard endpoint {host}:{wg_port}")
            wg_endpoints.add(ep)
        key = str(wg.get("public_key", ""))
        if key.startswith("CHANGE_ME"):
            if not allow_placeholders:
                errors.append(f"{label}.wireguard.public_key still contains a placeholder")
        else:
            try:
                decoded = base64.b64decode(key, validate=True)
                if len(decoded) != 32 or decoded == b"\0" * 32 or decoded in keys:
                    raise ValueError
                keys.add(decoded)
            except Exception:
                errors.append(f"{label}.wireguard.public_key must be a unique WireGuard public key")
        web = node.get("web") or {}
        pg = node.get("postgres") or {}
        minio = node.get("minio") or {}
        valid_port(web.get("http_port"), f"{label}.web.http_port", errors)
        valid_port(web.get("https_port"), f"{label}.web.https_port", errors)
        valid_port(pg.get("local_port"), f"{label}.postgres.local_port", errors)
        valid_port(pg.get("cluster_port", 5432), f"{label}.postgres.cluster_port", errors)
        valid_port(minio.get("local_port"), f"{label}.minio.local_port", errors)
        valid_port(minio.get("console_port"), f"{label}.minio.console_port", errors)
        valid_port(minio.get("cluster_port", 9000), f"{label}.minio.cluster_port", errors)
        valid_port(node.get("health_port", 9187), f"{label}.health_port", errors)
        local_tcp = [web.get("http_port"), web.get("https_port"), pg.get("local_port"), minio.get("local_port"), minio.get("console_port"), node.get("health_port", 9187)]
        local_ints = [int(v) for v in local_tcp if str(v).isdigit()]
        dups = sorted({p for p in local_ints if local_ints.count(p) > 1})
        if dups:
            errors.append(f"{label} reuses local TCP port(s): {', '.join(map(str, dups))}")
    preferred = str(raw.get("preferred_primary", ""))
    if preferred not in ids:
        errors.append("preferred_primary must reference an existing node")
    voters = [n for n in nodes if n.get("quorum_voter", False)]
    if mode == "quorum" and (len(voters) < 3 or len(voters) % 2 == 0):
        errors.append("quorum mode requires an odd number of at least three quorum_voter nodes")
    if errors:
        fail("\n".join(errors))
    return raw


def node_by_id(inv: dict[str, Any], node_id: str) -> dict[str, Any]:
    for node in inv["nodes"]:
        if node["id"] == node_id:
            return node
    fail(f"node {node_id} is not present in inventory")


def current_node(explicit: str | None) -> str:
    if explicit:
        return explicit
    if NODE_ID_FILE.is_file():
        return NODE_ID_FILE.read_text(encoding="utf-8").strip()
    fail("local node id is not set; run bash ./cluster.sh apply NODE_ID")


def chown_deployment_owner(path: Path) -> None:
    if os.geteuid() != 0:
        return
    owner = ROOT.stat()
    os.chown(path, owner.st_uid, owner.st_gid)


def render_local(inv: dict[str, Any], node: dict[str, Any]) -> Path:
    RUNTIME.mkdir(parents=True, exist_ok=True)
    os.chmod(RUNTIME, 0o700)
    chown_deployment_owner(RUNTIME)
    pg = node["postgres"]
    minio = node["minio"]
    web = node["web"]
    lines = [
        "TASKFORGE_EASY_CLUSTER=true",
        f"TASKFORGE_NODE_ID={node['id']}",
        f"PUBLIC_HTTP_PORT={int(web['http_port'])}",
        f"PUBLIC_HTTPS_PORT={int(web['https_port'])}",
        "POSTGRES_BIND=127.0.0.1",
        f"POSTGRES_PORT={int(pg['local_port'])}",
        "MINIO_BIND=127.0.0.1",
        f"MINIO_PORT={int(minio['local_port'])}",
        "MINIO_CONSOLE_BIND=127.0.0.1",
        f"MINIO_CONSOLE_PORT={int(minio['console_port'])}",
    ]
    path = RUNTIME / "local.env"
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    os.chmod(path, 0o600)
    chown_deployment_owner(path)
    summary = {
        "mode": inv.get("mode", "replica"),
        "node": node["id"],
        "preferred_primary": inv["preferred_primary"],
        "wireguard": node["wireguard"],
        "web": node["web"],
        "postgres": node["postgres"],
        "minio": node["minio"],
    }
    summary_path = RUNTIME / "easy-node-summary.json"
    save_json(summary_path, summary)
    os.chmod(summary_path, 0o644)
    chown_deployment_owner(summary_path)
    return path


def wireguard_config(inv: dict[str, Any], node: dict[str, Any], private_key: str) -> str:
    network = ipaddress.ip_network(inv["wireguard_cidr"], strict=False)
    interface_prefix = network.prefixlen
    peer_prefix = network.max_prefixlen
    lines = [
        "[Interface]",
        f"Address = {node['wireguard']['ip']}/{interface_prefix}",
        f"ListenPort = {int(node['wireguard']['listen_port'])}",
        f"PrivateKey = {private_key.strip()}",
        "SaveConfig = false",
        "",
    ]
    for peer in inv["nodes"]:
        if peer["id"] == node["id"]:
            continue
        lines.extend([
            f"# TaskForge node {peer['id']}",
            "[Peer]",
            f"PublicKey = {peer['wireguard']['public_key']}",
            f"AllowedIPs = {peer['wireguard']['ip']}/{peer_prefix}",
            f"Endpoint = {peer['public_host']}:{int(peer['wireguard']['listen_port'])}",
            "PersistentKeepalive = 25",
            "",
        ])
    return "\n".join(lines).rstrip() + "\n"


def descriptor_from_args(args: argparse.Namespace, public_key: str) -> dict[str, Any]:
    return {
        "id": args.node,
        "priority": args.priority,
        "public_host": args.public_ip,
        "quorum_voter": bool(args.quorum_voter),
        "app": {
            "profile": args.app_profile,
            "can_be_primary": bool(args.can_be_primary),
            "assist_on_failover": bool(args.assist_on_failover),
            "exclude_services": [x for x in args.exclude_service if x],
            "assist_exclude_services": [x for x in args.assist_exclude_service if x],
        },
        "wireguard": {"ip": args.wg_ip, "listen_port": args.wg_port, "public_key": public_key},
        "web": {"http_port": args.http_port, "https_port": args.https_port},
        "postgres": {"local_port": args.postgres_port, "cluster_port": 5432, "patroni_rest_port": args.patroni_port},
        "minio": {"local_port": args.minio_port, "console_port": args.minio_console_port, "cluster_port": 9000},
        "health_port": args.health_port,
    }


def render_quorum(inv: dict[str, Any], output: Path) -> None:
    voters = [n for n in inv["nodes"] if n.get("quorum_voter", False)]
    if len(voters) < 3 or len(voters) % 2 == 0:
        fail("need an odd number of at least three quorum_voter nodes")
    nodes = []
    ranked = sorted(inv["nodes"], key=lambda item: int(item.get("priority", 0)), reverse=True)
    rank_by_id = {str(item["id"]): idx for idx, item in enumerate(ranked)}
    for n in inv["nodes"]:
        pg = n["postgres"]
        mi = n["minio"]
        app = n.get("app") if isinstance(n.get("app"), dict) else None
        if app is None:
            # Backward-compatible v39 -> v40 default: two strongest nodes are
            # full failover candidates, the remaining voters keep a lite copy
            # and assist only after a real failover.
            if rank_by_id[str(n["id"])] < 2:
                app = {
                    "profile": "full",
                    "can_be_primary": True,
                    "assist_on_failover": False,
                    "exclude_services": [],
                    "assist_exclude_services": [],
                }
            else:
                app = {
                    "profile": "lite",
                    "can_be_primary": False,
                    "assist_on_failover": True,
                    "exclude_services": ["browser-api", "image-analyzer"],
                    "assist_exclude_services": ["support-bot", "telegram-quiz-bot", "rating-worker"],
                }
        nodes.append({
            "id": n["id"],
            "priority": n["priority"],
            "platform": "linux",
            "public_host": n["public_host"],
            "dcs_voter": bool(n.get("quorum_voter", False)),
            "app": app,
            "wireguard": n["wireguard"],
            "web": {**n["web"], "tls_mode": "origin-ca"},
            "postgres": {"host_port": int(pg.get("cluster_port", 5432)), "patroni_rest_port": int(pg.get("patroni_rest_port", 8008))},
            "etcd": {"client_port": int(n.get("etcd", {}).get("client_port", 2379)), "peer_port": int(n.get("etcd", {}).get("peer_port", 2380))},
            "minio": {"api_port": int(mi.get("cluster_port", 9000)), "console_port": int(mi["console_port"])},
            "health_port": int(n.get("health_port", 9187)),
        })
    raw = {
        "schema_version": 1,
        "cluster_name": inv["cluster_name"],
        "wireguard_cidr": inv["wireguard_cidr"],
        "preferred_primary": inv["preferred_primary"],
        "automatic_failback": True,
        "failback_delay_seconds": 300,
        "failback_stable_seconds": 60,
        "maximum_lag_on_failover_bytes": 67108864,
        "maximum_lag_on_failback_bytes": 16777216,
        "shared_state_interval_seconds": 60,
        "etcd_image": "gcr.io/etcd-development/etcd:v3.6.14",
        "nodes": nodes,
    }
    save_json(output, raw)


def main() -> int:
    parser = argparse.ArgumentParser(description="TaskForge easy cluster inventory")
    parser.add_argument("--inventory", default=str(DEFAULT_INVENTORY))
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("validate"); p.add_argument("--allow-placeholders", action="store_true")
    p = sub.add_parser("set-node"); p.add_argument("node")
    p = sub.add_parser("node-info"); p.add_argument("--node")
    p = sub.add_parser("render-local"); p.add_argument("--node")
    p = sub.add_parser("render-wireguard"); p.add_argument("--node"); p.add_argument("--private-key-file", required=True); p.add_argument("--output", required=True)
    p = sub.add_parser("prepare-descriptor")
    p.add_argument("--node", required=True); p.add_argument("--public-ip", required=True); p.add_argument("--wg-ip", required=True); p.add_argument("--public-key-file", required=True)
    p.add_argument("--priority", type=int, default=50); p.add_argument("--wg-port", type=int, default=51820)
    p.add_argument("--http-port", type=int, default=80); p.add_argument("--https-port", type=int, default=443)
    p.add_argument("--postgres-port", type=int, default=5432); p.add_argument("--patroni-port", type=int, default=8008)
    p.add_argument("--minio-port", type=int, default=9000); p.add_argument("--minio-console-port", type=int, default=9001)
    p.add_argument("--health-port", type=int, default=9187); p.add_argument("--quorum-voter", action="store_true")
    p.add_argument("--app-profile", choices=("full", "lite", "none"), default="full")
    p.add_argument("--can-be-primary", action=argparse.BooleanOptionalAction, default=True)
    p.add_argument("--assist-on-failover", action=argparse.BooleanOptionalAction, default=False)
    p.add_argument("--exclude-service", action="append", default=[])
    p.add_argument("--assist-exclude-service", action="append", default=[])
    p.add_argument("--output", required=True)
    p = sub.add_parser("add-node"); p.add_argument("descriptor")
    p = sub.add_parser("render-quorum"); p.add_argument("--output", default=str(HERE / "cluster.json"))
    sub.add_parser("cloudflare")
    args = parser.parse_args()
    path = Path(args.inventory)
    if args.command == "validate":
        inv = load_inventory(path, allow_placeholders=args.allow_placeholders)
        print(f"inventory ok: mode={inv.get('mode','replica')} nodes={len(inv['nodes'])}")
        return 0
    if args.command == "prepare-descriptor":
        pub = Path(args.public_key_file).read_text(encoding="utf-8").strip()
        save_json(Path(args.output), descriptor_from_args(args, pub))
        print(args.output)
        return 0
    inv = load_inventory(path)
    if args.command == "set-node":
        node_by_id(inv, args.node)
        RUNTIME.mkdir(parents=True, exist_ok=True)
        atomic_write_text(NODE_ID_FILE, args.node + "\n", 0o600)
        os.chmod(NODE_ID_FILE, 0o600); chown_deployment_owner(NODE_ID_FILE)
        print(args.node)
    elif args.command == "node-info":
        print(json.dumps(node_by_id(inv, current_node(args.node)), ensure_ascii=False, indent=2))
    elif args.command == "render-local":
        print(render_local(inv, node_by_id(inv, current_node(args.node))))
    elif args.command == "render-wireguard":
        node = node_by_id(inv, current_node(args.node))
        private = Path(args.private_key_file).read_text(encoding="utf-8").strip()
        atomic_write_text(Path(args.output), wireguard_config(inv, node, private), 0o600)
        os.chmod(args.output, 0o600)
        print(args.output)
    elif args.command == "add-node":
        desc = load_json(Path(args.descriptor))
        if any(n["id"] == desc.get("id") for n in inv["nodes"]):
            inv["nodes"] = [desc if n["id"] == desc.get("id") else n for n in inv["nodes"]]
        else:
            inv["nodes"].append(desc)
        save_json(path, inv)
        load_inventory(path)
        print(f"node {desc['id']} saved in {path}")
    elif args.command == "render-quorum":
        render_quorum(inv, Path(args.output)); print(args.output)
    elif args.command == "cloudflare":
        print(json.dumps({
            "traffic_steering": "off",
            "origins": [
                {
                    "node": n["id"],
                    "address": n["public_host"],
                    "http_port": n["web"]["http_port"],
                    "https_port": n["web"]["https_port"],
                    # External load balancers must probe the public web origin.
                    # The dedicated 9187 controller endpoint binds to WireGuard
                    # and is intentionally not exposed through the host firewall.
                    "health_path": "/ha/traffic-ready",
                    "health_port": n["web"]["https_port"],
                    "health_protocol": "https",
                    "internal_cluster_health_port": n.get("health_port", 9187),
                }
                for n in sorted(inv["nodes"], key=lambda x: int(x["priority"]), reverse=True)
            ],
        }, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
