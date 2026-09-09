# Go SQL / r59: factual verification report

Date: 2026-09-09. This is a release candidate, not a certified production rollout.
Input: complete prior SQL candidate plus the user's develop(212) migrations.
Python SQL execution has been replaced by Go; see GO_RUNTIME.md for exact scope.

## Preservation

- All 92 migrations/designers/snapshots and 104 Domain/Data files match the user
  develop(212) hash list. No migrations were generated, edited or applied.
- All 1483 files from develop(212) remain present.
- All 664 C# files and all 316 web-tree files in the prior complete Python SQL
  candidate are byte-identical. This port does not alter the C# domain/API or UI.
- cluster/agent.py, cluster/primary.py and cluster/cloudflare_dns.py are byte-identical
  to r58. Packaging/control revision strings, Go Compose and SQL checks are updated.
- The only root Markdown file in either package is 00_AI_READ_THIS_FIRST.md.
- The SQL worker has 24 production Go files (7189 physical lines including
  the CGO bridge) and 7 Go test files (1784 lines). It has zero Python files.
  Physical line counts include blanks/comments and are not a performance metric.

## Executed here

The available compiler is Go 1.23.2 linux/amd64, not the Docker/CI pin 1.26.8.
Native clients: libpq 170010, MariaDB Connector/C 3.4.9, SQLite 3.46.1. The Go 1.26.8
archive could not be downloaded in this environment. No target-compiler execution
is claimed. Docker uses Debian bookworm native libraries; those exact binaries
still need the real image gate even though the same API bridge built locally.

| Gate | Actual result |
| --- | --- |
| Go formatting/build/vet | Passed, including a stripped release-helper build |
| Go race tests | Passed; 52 nonempty top-level checks including the fuzz seed set |
| Isolated SQLite | Real child processes: result/state/schema, preview after DDL/DML, partial errors, constraints, caps, timeout, authorizer denial and unchanged GOLDEN |
| Pool and lifecycle | Singleflight, bounded refill, no dirty reuse, quarantine, partial create failure, cancellation, idle/LRU retirement and active-lease reset fence |
| Lease/completion | Lost completion ACK does not rerun SQL; lease watchdog terminates a running helper and fences completion |
| Native security probe | Real Linux/amd64 all-thread seccomp test; new files and sockets denied on existing threads |
| HTTP/profile controls | Redirect/error secrecy, profile identity, lease conflicts, admin-password redaction, namespace lock, API-outage cache preservation |
| Rabbit unit tests | Fake TCP broker: handshake, fragmented deliveries, heartbeat, malformed/oversized frames |
| AMQP cursor fuzz | 3 seconds, two workers, 56,544 executions, no failure; not an exhaustive security proof |
| Load exercise | 100 Run + 100 Check through four local SQLite execution slots, in race-enabled tests |
| Existing OJ check script | All available checks passed: existing Python/JS policy smoke, C sandbox compilation, Go runner tests/vet; Rust analyzer skipped because cargo is absent |
| Web tests | 6 SQL tests and 14 existing cluster tests passed; architecture check passed |
| Source guards | Protected hashes, C# source invariants, workflow/image inventory and Docker build-context validation passed; 35 image entries |

Go's JSON output reports 82 pass events because it includes parents and subtests.
Eight TestRealEngines parent groups contain only skipped PostgreSQL/MySQL subcases
locally and are NOT counted among the 52 executed top-level checks above.
There are 16 explicitly skipped database subcases and one skipped real Rabbit test.
No-test-file packages are not failures or real-server tests. Raw receipts are in
qa/go/go-race.jsonl and qa/go/*.log. The Go race detector does not instrument the
external native C libraries or the separately built production helper process.

The load test is not 100 simultaneously executing database queries, not a 100-client
HTTP/browser benchmark and not a performance comparison with the Python candidate.
No throughput/RAM improvement ratio is claimed. Actual capacity needs production-
image tests, realistic data, co-located services and hostile-query measurements.

## Mandatory gates not executed here

.NET 10 build and the 44 domain checks for the complete runtime candidate; full
frontend npm/production build; Docker image build; real PostgreSQL and MySQL;
real RabbitMQ; browser and HTTP end-to-end behavior; native ARM64 isolation;
production restored-DB migration rehearsal; image publication; rollout on A/B/C;
live Patroni/Cloudflare/HTTPS failover with SQL jobs. No production writes occurred.
The earlier user .NET domain result is not evidence that the later API runtime built.

The real server gate now builds a dedicated test target from the same Go runtime
base, runs restricted PostgreSQL/MySQL and an isolated test-only RabbitMQ, then
runs adapter/security/recovery and broker tests using the production helper. It
removes only its own uniquely named test resources. Its existence is not a pass.

From the source root, with the documented prerequisites:

```bash
bash ./scripts/check-sql-update.sh && bash ./scripts/sql/test-engines.sh
```

Both workflows require these gates before image build/push. Preserve failures;
do not weaken the security policy or silently skip Docker tests to publish images.

## Cluster and artifact verification

The r59 offline release suite passed: baseline storage/topology assertions,
46 lifecycle tests, 16 integration/mocked-process tests and 9 SQL/Go topology tests.
The log is qa/go/r59-offline.log. It exercises mocked/state-based cluster behavior;
it is not live Docker, Cloudflare or database proof. The final archives
are complete source/control bundles, not patchers. Registry images/toolchains are
external build/deployment dependencies; this is not an air-gapped image distribution.

Historical Python r58 QA is retained only in history/python-r58. Do not use those
Python tests as evidence for this Go implementation.
