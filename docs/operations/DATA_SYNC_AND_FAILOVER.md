# Data sync and failover notes

For multi-server deployment, API failover is not enough. The data layer must be ready before a server dies.

## Starting point

For the first production check, use one primary PostgreSQL instance and one production compose deployment. After the microservices are verified, add replication.

## Recommended first HA layout

```text
Poland:
  PostgreSQL primary
  RabbitMQ primary
  MinIO primary
  all APIs/workers/runners

Israel:
  PostgreSQL standby
  APIs/workers/runners

RB:
  APIs/workers/runners
  optional PostgreSQL standby
```

If RB dies, traffic can move to Poland/Israel and the data layer still exists.

## If primary DB is in RB

Then Israel/Poland must already have PostgreSQL standby replicas before failover is useful.

```text
RB PostgreSQL primary
  -> Israel standby
  -> Poland standby
```

If RB dies:

1. Remove RB from external load balancer/DNS.
2. Promote Israel or Poland standby to primary.
3. Switch service connection strings or internal DB DNS to the new primary.
4. Restart API/worker services.
5. Do not let old RB primary rejoin as primary. Rebuild it as a replica from the new primary.

## Files and artifacts

Do not store uploads, generated images, AI artifacts, or solution attachments only on a local server disk.

Use MinIO/S3 through `files-api`. Later add MinIO replication or move to an external S3-compatible provider.

## Queues

Execution and AI jobs should go through RabbitMQ. Workers and runners can exist on multiple servers/regions.

For first production, one RabbitMQ is acceptable. For HA, plan RabbitMQ replication/federation or regional queues with a dispatcher.
