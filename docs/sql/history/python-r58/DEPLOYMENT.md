# SQL source and r58 deployment gates

This is the runtime implementation candidate based on develop(212). It is NOT a
production-validated release. Preserve the user's existing migrations; do not run
migration generation or `dotnet ef database update` just to test this candidate.

## 1. Build/test on the local development machine

Extract the complete source into a clean folder, not over an older tree. From its
root, with .NET 10, Node/npm, Python 3, libseccomp2 and PyYAML available:

```bash
bash ./scripts/check-sql-update.sh
bash ./scripts/sql/test-engines.sh
```

The first script verifies the user migration/model hashes, builds the changed .NET
APIs/domain check and Education, runs Python/SQLite and frontend tests, and builds
the web application. It does not launch the web APIs or apply database migrations.

The second needs Docker and registry access. It builds the worker and launches
ONLY isolated PostgreSQL/MySQL test engines with fresh credentials, private networks,
no host DB ports and dedicated disposable tmpfs. It runs real adapter tests and
removes only its own uniquely named test objects. It is not an instruction to point
the test adapter at a production database. Both commands must succeed before rollout.

GitHub image workflows run these gates before their build/push jobs. A failed gate
must block image publication, not be bypassed to get a green deployment. New images
have not been built or pushed by this archive-producing session.

## 2. Disposable dev-stack acceptance

On a separate development environment, use the existing `bash ./build.sh`. The dev
wrapper prepares SQL-only local secrets/pins and enables the SQL profile. Starting
APIs can apply their normal startup migrations to the DEV business database; do not
supply production connection strings. Existing source `deploy/prod` scripts do not
replace the authoritative standalone r58 cluster package.

In the UI create one shared dataset, two sql-test assignments, and validate each
selected engine. Publish only after all targets are valid. Exercise result/state/
schema, per-engine reference overrides, a failed script with partial preview, two
independent Run clicks and a successful Check. Confirm Run has no submission/energy/
progress effect; Check has exactly one history entry and normal progression.
Confirm a learner cannot fetch private reference or expected content.

Export/import a version-5 graph with both assignments sharing one dataset. Repeat
without checks and verify private content is absent. Import an existing version-3/4
code/test/math graph. Test existing code-test/image-test/test/math tasks, rating,
analytics and course progression. Restart worker/engines with jobs pending and
confirm bounded recovery, no duplicate grade/refund and no dirty sandbox reuse.
These UI/API/crash acceptance steps have NOT been executed here.

## 3. Publish a single image set

After gates and dev acceptance pass, build/push through the repository workflow to
the intended existing image channel. Confirm that tasks-api, execution-api,
solutions-api, education-api, front, gateway and sql-worker come from this source
revision. Rebuilding or installing only sql-worker is not the complete update.

Use the same worker image and PostgreSQL/MySQL RepoDigest pins on A/B. Profiles are
exact fingerprints: different worker code/engine pins do not transparently serve
old published specs. Revalidate/republish explicitly for a new profile.

## 4. Standalone production cluster r58

The full r58 archive is independent of an r57 folder. It requires Docker, existing
live cluster state/configuration and published registry images, not a neighboring
old release. Container images are registry dependencies, not embedded tar layers.

Take/verify the normal business-data backup before allowing new API images to run
startup migrations. Do not use disposable SQL cache as a backup. Do not remove or
replace any production PostgreSQL/MinIO/RabbitMQ/Redis/etcd volume.

Use the SAME r58 package on A/B/C. Update standbys first and the actual current
Primary last; do not assume A is Primary. From each extracted r58 root:

```bash
sudo bash ./cluster.sh migrate
bash ./cluster.sh status
bash ./cluster.sh doctor
```

`migrate` discovers the actual node/state, preserves live storage bindings and
secrets, prepares optional SQL on full nodes, and installs the stable control plane.
C remains lite and does not need heavy SQL images. Existing Cloudflare/Patroni/
certificate ordering is retained. SQL auxiliary startup does not block route/HTTPS
proof. A successful cluster migrate is NOT proof that all API/frontend images have
updated or that SQL validation has passed.

Check the existing image rollout/watchtower results before testing SQL. A stable
control-plane-only migration deliberately does not force-recreate every existing
application container. After the image rollout, inspect actual running image IDs:

```bash
sudo bash ./cluster.sh compose images
sudo bash ./cluster.sh compose ps
sudo bash ./cluster.sh compose logs --tail 100 sql-worker
```

Do not blindly run `compose up` for all services on a standby: application role is
managed by the agent. If the image rollout is incomplete or any gate failed, stop
and diagnose rather than forcing Primary or regenerating migrations.

A/B can run SQL when full Primary. C can still be Primary for the ordinary site,
but SQL execution is then unavailable. SQL is not active-active in this revision.
On a staging cluster verify A->B and B->A with SQL jobs pending, public HTTPS,
worker recovery and profile equality. That live failover test is still required.

## 5. No new migration round

The three user migrations in develop(212) are retained exactly. The runtime changes
do not need another Entity/DbContext migration. Migration presence/hash checks do
not replace a real upgrade rehearsal on a disposable restored business database.
Production APIs keep their existing migration/startup behavior; do not mistake
`cluster.sh migrate` (control-plane upgrade) for the EF migration generator.

## Failure policy

Keep a failed candidate out of the automatic image channel until fixed. Do not
hand-edit EF snapshots, suppress build warnings as proof of correctness, bypass
seccomp/marker checks, attach SQL engines to the production network, or turn C into
a heavy node to make a test pass. Clearing disposable caches is separate from
business data and must remain scoped to this SQL runtime.
