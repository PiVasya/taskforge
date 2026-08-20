#!/usr/bin/env python3
from __future__ import annotations

import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "deploy" / "ha"))
import agent  # noqa: E402


def config(tmp: Path) -> agent.Config:
    return agent.Config(
        env_file=tmp / ".env",
        enabled=True,
        node_id="A",
        preferred_node="A",
        wg_ip="10.80.0.1",
        peer_wg_ip="10.80.0.2",
        node_a_public_ip="",
        node_b_public_ip="",
        wg_cidr="10.80.0.0/24",
        agent_bind="127.0.0.1",
        agent_port=9187,
        shared_key="x" * 48,
        postgres_user="taskforge",
        postgres_password="p" * 32,
        postgres_port=5432,
        postgres_image="postgres:18-alpine",
        repl_user="taskforge_repl",
        repl_password="r" * 32,
        auto_failover=False,
        auto_failback=True,
        allow_unfenced=False,
        fence_script="",
        recover_script="",
        failover_after=30,
        service_failover_after=90,
        failback_delay=180,
        failback_stable=30,
        failback_max_lag_bytes=1024 * 1024,
        rejoin_after=120,
        loop_seconds=5,
        operation_timeout=1800,
        gateway_ready_timeout=240,
        minio_drain_timeout=300,
    )


with tempfile.TemporaryDirectory(prefix="taskforge-ha-ci-") as td:
    tmp = Path(td)
    old_root = agent.ROOT
    try:
        agent.ROOT = tmp
        ready = tmp / ".runtime" / "ha" / "traffic-ready"
        ready.parent.mkdir(parents=True)
        ready.write_text("A test\n", encoding="utf-8")

        ctl = agent.Controller(config(tmp))
        assert ready.is_file(), "constructing a Controller must not drop readiness"

        # A detected dual-primary condition is never auto-resolved by preferred
        # node order. Readiness is removed and no activation path is called.
        ctl.peer_status = lambda: {"postgres_role": "primary"}  # type: ignore[method-assign]
        ctl.pg_role = lambda: "primary"  # type: ignore[method-assign]
        ctl.ensure_active = lambda: (_ for _ in ()).throw(AssertionError("must not activate split brain"))  # type: ignore[method-assign]
        ctl.reconcile_once()
        assert not ready.exists(), "split brain must remove public readiness"
        assert ctl.state.get("last_event") == "split-brain-detected"

        # A recovery hook is best effort. A failure after successful promotion
        # must not throw and accidentally remove readiness from the new primary.
        ready.write_text("A test\n", encoding="utf-8")
        ctl.cfg = agent.dataclasses.replace(ctl.cfg, recover_script="missing-recover-hook")
        ctl.recover_peer_after_fence()
        assert ready.is_file(), "recovery hook failure must not revoke promoted primary readiness"
    finally:
        agent.ROOT = old_root

print("TaskForge HA state-machine smoke tests OK")
