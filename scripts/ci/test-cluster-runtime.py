#!/usr/bin/env python3
from __future__ import annotations

import base64
import copy
import importlib.util
import json
import sys
from pathlib import Path
import tempfile

ROOT = Path(__file__).resolve().parents[2]
CLUSTER_DIR = ROOT / "deploy" / "cluster"


def load_module(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


clusterctl = load_module("taskforge_clusterctl_test", CLUSTER_DIR / "clusterctl.py")

with tempfile.TemporaryDirectory(prefix="taskforge-cluster-ci-") as td:
    tmp = Path(td)
    raw = json.loads((CLUSTER_DIR / "cluster.example.json").read_text(encoding="utf-8"))
    for index, node in enumerate(raw["nodes"], start=1):
        node["public_host"] = f"203.0.113.{index}"
        node["wireguard"]["public_key"] = base64.b64encode(bytes([index]) * 32).decode()
    config_path = tmp / "cluster.json"
    config_path.write_text(json.dumps(raw), encoding="utf-8")
    env_path = tmp / ".env"
    env_path.write_text(
        "TASKFORGE_INTERNAL_KEY=" + "i" * 64 + "\n"
        "POSTGRES_USER=taskforge\n"
        "POSTGRES_PASSWORD=" + "p" * 48 + "\n"
        "MINIO_ROOT_USER=taskforge\n"
        "MINIO_ROOT_PASSWORD=" + "m" * 48 + "\n"
        "S3_BUCKET=taskforge-files\n",
        encoding="utf-8",
    )
    cluster = clusterctl.Cluster.load(config_path)
    assert len(cluster.nodes) == 3
    assert len(cluster.voters) == 3
    assert cluster.node("C").postgres_port == 55432
    assert cluster.node("C").https_port == 8443

    # WireGuard keys identify nodes: duplicates and the all-zero key must fail.
    duplicate_key = copy.deepcopy(raw)
    duplicate_key["nodes"][1]["wireguard"]["public_key"] = duplicate_key["nodes"][0]["wireguard"]["public_key"]
    duplicate_path = tmp / "cluster-duplicate-wg-key.json"
    duplicate_path.write_text(json.dumps(duplicate_key), encoding="utf-8")
    try:
        clusterctl.Cluster.load(duplicate_path)
        raise AssertionError("duplicate WireGuard public key was accepted")
    except ValueError as exc:
        assert "duplicate WireGuard public key" in str(exc)

    zero_key = copy.deepcopy(raw)
    zero_key["nodes"][2]["wireguard"]["public_key"] = base64.b64encode(bytes(32)).decode()
    zero_path = tmp / "cluster-zero-wg-key.json"
    zero_path.write_text(json.dumps(zero_key), encoding="utf-8")
    try:
        clusterctl.Cluster.load(zero_path)
        raise AssertionError("all-zero WireGuard public key was accepted")
    except ValueError as exc:
        assert "all-zero key" in str(exc)

    # Numeric port reuse is valid across protocols (WireGuard UDP vs HTTPS TCP).
    mixed_protocol = copy.deepcopy(raw)
    mixed_protocol["nodes"][2]["wireguard"]["listen_port"] = mixed_protocol["nodes"][2]["web"]["https_port"]
    mixed_path = tmp / "cluster-mixed-protocol-port.json"
    mixed_path.write_text(json.dumps(mixed_protocol), encoding="utf-8")
    clusterctl.Cluster.load(mixed_path)

    clusterctl.RUNTIME_DIR = tmp / "runtime"
    written = clusterctl.render(cluster, cluster.node("A"), env_path)
    patroni = json.loads(Path(written["patroni"]).read_text(encoding="utf-8"))
    assert patroni["bootstrap"]["dcs"]["postgresql"]["parameters"]["synchronous_standby_names"] == ""
    assert patroni["bootstrap"]["dcs"]["failsafe_mode"] is True
    assert patroni["tags"]["failover_priority"] == 100
    assert patroni["etcd3"]["hosts"] == ["10.80.0.1:2379", "10.80.0.2:2379", "10.80.0.3:12379"]
    assert "::0/0" not in json.dumps(patroni)
    assert "10.80.0.0/24" in json.dumps(patroni)

    wg = clusterctl.wireguard_config(cluster, cluster.node("C"), "private-key")
    assert "ListenPort = 51937" in wg
    assert "Endpoint = 203.0.113.1:51820" in wg
    assert "Endpoint = 203.0.113.2:51820" in wg

    # Adding full TaskForge nodes D/E must not require adding more etcd voters.
    expanded = copy.deepcopy(raw)
    for index, node_id in enumerate(("D", "E"), start=4):
        node = copy.deepcopy(expanded["nodes"][0])
        node.update({
            "id": node_id,
            "priority": 110 - index * 10,
            "platform": "linux",
            "public_host": f"203.0.113.{index}",
            "dcs_voter": False,
            "health_port": 9187 + index,
        })
        node["wireguard"] = {
            "ip": f"10.80.0.{index}",
            "listen_port": 51820 + index,
            "public_key": base64.b64encode(bytes([index]) * 32).decode(),
        }
        node["web"] = {"http_port": 8080 + index, "https_port": 8443 + index, "tls_mode": "origin-ca"}
        node["postgres"] = {"host_port": 55432 + index, "patroni_rest_port": 18008 + index}
        node["minio"] = {"api_port": 19000 + index * 2, "console_port": 19001 + index * 2}
        node.pop("etcd", None)
        expanded["nodes"].append(node)
    expanded_path = tmp / "cluster-5.json"
    expanded_path.write_text(json.dumps(expanded), encoding="utf-8")
    cluster5 = clusterctl.Cluster.load(expanded_path)
    assert len(cluster5.nodes) == 5
    assert [n.id for n in cluster5.voters] == ["A", "B", "C"]
    clusterctl.RUNTIME_DIR = tmp / "runtime-5"
    rendered_d = clusterctl.render(cluster5, cluster5.node("D"), env_path)
    patroni_d = json.loads(Path(rendered_d["patroni"]).read_text(encoding="utf-8"))
    assert patroni_d["etcd3"]["hosts"] == ["10.80.0.1:2379", "10.80.0.2:2379", "10.80.0.3:12379"]
    assert patroni_d["tags"]["failover_priority"] == 70
    wg_d = clusterctl.wireguard_config(cluster5, cluster5.node("D"), "private-key")
    assert wg_d.count("[Peer]") == 4

print("TaskForge cluster renderer smoke tests OK")
