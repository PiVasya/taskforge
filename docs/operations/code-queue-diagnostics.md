# Code-execution queue diagnostics

Only the legacy code-submission pipeline uses these events. SQL check/materialization uses a separate queue and is not included in these snapshots.

## Capture on both cluster nodes

For more useful labels, set `TASKFORGE_NODE_LABEL=A` on node A and `TASKFORGE_NODE_LABEL=B` on node B in their respective Compose deployment environments, then redeploy. Otherwise the diagnostic identity still includes Docker container hostname and a unique startup ID. The label is optional and does not alter how the queue is claimed.

Keep `TASKFORGE_DEBUG_LOGS=1`. Queue health snapshots are emitted every 60 seconds by default; `EXECUTION_QUEUE_DIAGNOSTICS_INTERVAL_SECONDS` can be set between 15 and 300 seconds. This setting affects only the new snapshots, not the worker polling, watchdog timeout or execution limits.

After reproducing a stall, collect diagnostics **on each node** using the installed cluster controller:

```bash
sudo bash ./cluster.sh diagnostics --full-logs --out .
```

The diagnostic bundle should contain `containers/*execution-api*.log`, `containers/*execution-worker*.log`, and `containers/*solutions-api*.log`. A bundle collected only on node B does not establish whether worker A lost its first attempt. Collect while the event is still recent: the Docker log capture can retain only a bounded tail.

## Correlate the queue

The repository includes a read-only parser, which accepts two `.tar.gz` archives directly; it does not unpack, edit, or execute their contents:

```bash
python3 scripts/diagnostics/analyze-code-queue.py node-a-diagnostics.tar.gz node-b-diagnostics.tar.gz
python3 scripts/diagnostics/analyze-code-queue.py node-a-diagnostics.tar.gz node-b-diagnostics.tar.gz --id 1433fa32
```

Supply either a full job/submission GUID or a prefix of at least eight characters to `--id`. The parser reports the last visible events per job and an ordered timeline. A missing terminal event in the captured logs is a **lead, not proof of a stuck job**: the log window may be truncated or the event may be recorded only on the other node.

The stable prefix is `[TFQUEUE]`. Useful fields: `job`, `submission`, `worker`, `api`, `attempt`, `stage`, `createdAt`, `ageSeconds`, `heartbeatAgeSeconds`, and `elapsedMs`. Timestamps in Docker logs are UTC. Every process restart changes its instance ID; the physical A/B label does not.

| Event | Interpretation |
|---|---|
| `SUBMISSION_CREATED`, `ENQUEUE_START`, `CREATED`, `SUBMISSION_QUEUED` | Submission persisted; execution job created and queued |
| `CLAIMED` | `execution-api` assigned a particular attempt to a particular worker instance |
| `CLAIM_ACK`, `WORK_START` | The worker received the claim and entered processing |
| `ANALYZER_START`, `ANALYZER_DONE` | Security analyzer call and its result (no code or attestation logged) |
| `RUNNER_HTTP`, `RUNNER_EXCEPTION` | Language runner call response, timeout, or transport exception |
| `HEARTBEAT_OK`, `HEARTBEAT_CONFLICT` | The attempt is still valid or has been replaced |
| `VERDICT_SEND`, `VERDICT_ACK`, `VERDICT_COMMITTED` | Worker publication and durable solution verdict |
| `COMPLETE_SEND`, `COMPLETE_ACK`, `COMPLETED` | Durable execution-job completion |
| `BULK_STALE_REQUEUE`, `WATCHDOG_REQUEUED` | A stalled attempt became eligible for another worker |
| `WATCHDOG_EXPIRED`, `SUBMISSION_EXPIRED` | The respective watchdog finally closed an orphaned job/submission |
| `DELIVERY_ACK`, `DELIVERY_FINALIZED` | Reconciliation pushed a durable verdict and marked it delivered |
| `SNAPSHOT_ALERT`, `SUBMISSION_SNAPSHOT_ALERT` | Age, attempts and bounded identifiers for the oldest outstanding jobs |

### How to interpret typical gaps

- `CLAIMED` without `CLAIM_ACK` on the selected worker: claim HTTP response loss or process failure before start. Check that worker's container restart, CPU, and network events.
- `WORK_START` without `ANALYZER_DONE`: analyzer stage timed out, process was killed, or code execution stalled. Check `HEARTBEAT_*` events to distinguish a live worker from a lost claim.
- `ANALYZER_DONE` without `RUNNER_HTTP`: runner never responded or processing was cancelled; inspect `RUNNER_EXCEPTION`, runner logs and Docker/OOM events.
- `VERDICT_ACK` without `COMPLETE_ACK`/`COMPLETED`: verdict was accepted by `solutions-api`, but execution job may still require reconciliation or have to be reclaimed.
- `COMPLETED` without `VERDICT_COMMITTED`: reconciliation may have been refused or unable to reach `solutions-api`. Check `DELIVERY_*` and `DELIVERY_PAUSED`.
- Regular `WORKER_IDLE` with growing `SNAPSHOT_ALERT queued`: investigate claim failures, attempts exhausted, unreachable execution-api, or dispatch backlog.

Dedicated `[TFQUEUE]` records contain identifiers, statuses, durations and counts, **not source code, tests, credentials, or raw HTTP bodies**. Full development diagnostics can contain unrelated sensitive payloads from existing request logging; treat complete archives accordingly.

No EF migrations are needed for these diagnostic additions.
