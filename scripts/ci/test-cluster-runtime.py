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
    assert [n.id for n in cluster.voters] == ["A", "B", "C"]
    assert cluster.node("A").app_profile == "full" and cluster.node("A").can_be_primary
    assert cluster.node("B").app_profile == "full" and cluster.node("B").can_be_primary
    assert cluster.node("C").app_profile == "lite" and not cluster.node("C").can_be_primary
    assert {"browser-api", "image-analyzer"}.issubset(set(cluster.node("C").app_exclude_services))
    # Same service ports are valid because each host has its own network namespace.
    assert cluster.node("C").postgres_port == 5432
    assert cluster.node("C").https_port == 443

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

    clusterctl.RUNTIME_DIR = tmp / "runtime"
    written_a = clusterctl.render(cluster, cluster.node("A"), env_path)
    patroni_a = json.loads(Path(written_a["patroni"]).read_text(encoding="utf-8"))
    assert patroni_a["bootstrap"]["dcs"]["postgresql"]["parameters"]["synchronous_standby_names"] == ""
    assert patroni_a["bootstrap"]["dcs"]["failsafe_mode"] is True
    assert patroni_a["tags"]["nofailover"] is False
    assert patroni_a["tags"]["failover_priority"] == 100
    assert patroni_a["etcd3"]["hosts"] == ["10.80.0.1:2379", "10.80.0.2:2379", "10.80.0.3:2379"]

    clusterctl.RUNTIME_DIR = tmp / "runtime-c"
    written_c = clusterctl.render(cluster, cluster.node("C"), env_path)
    patroni_c = json.loads(Path(written_c["patroni"]).read_text(encoding="utf-8"))
    assert patroni_c["tags"]["nofailover"] is True
    assert patroni_c["tags"]["noloadbalance"] is True
    assert patroni_c["tags"]["failover_priority"] == 80
    assert "10.80.0.0/24" in json.dumps(patroni_c)

    wg = clusterctl.wireguard_config(cluster, cluster.node("C"), "private-key")
    assert "ListenPort = 51820" in wg
    assert "Endpoint = 203.0.113.1:51820" in wg
    assert "Endpoint = 203.0.113.2:51820" in wg

    # Adding non-voter full nodes must not grow the three-member DCS quorum.
    expanded = copy.deepcopy(raw)
    for index, node_id in enumerate(("D", "E"), start=4):
        node = copy.deepcopy(expanded["nodes"][0])
        node.update({
            "id": node_id,
            "priority": 110 - index * 10,
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
        node["postgres"] = {"host_port": 5432, "patroni_rest_port": 8008}
        node["minio"] = {"api_port": 9000, "console_port": 9001}
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
    assert patroni_d["etcd3"]["hosts"] == ["10.80.0.1:2379", "10.80.0.2:2379", "10.80.0.3:2379"]
    assert patroni_d["tags"]["failover_priority"] == 70
    wg_d = clusterctl.wireguard_config(cluster5, cluster5.node("D"), "private-key")
    assert wg_d.count("[Peer]") == 4

print("TaskForge v40 cluster renderer smoke tests OK")
