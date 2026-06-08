# Local run and smoke test

Run from the repository root.

## Clean generated files

```bash
./build.sh clean
```

## Build and start the dev stack

```bash
./build.sh
```

`build.sh` builds services one by one, stores logs in `deploy/dev/build-logs/<timestamp>/`, starts the stack without rebuilding again, and prints a startup log snapshot.

If a build fails, fix the error and continue from the failed service:

```bash
./build.sh resume
```

## Start an already built stack

```bash
./build.sh up
```

## Check containers

```bash
./build.sh ps
./build.sh logs judge
```

## Run judge E2E

```bash
./build.sh e2e
```

The E2E script validates the regular solution pipeline for six languages:

- C++
- C#
- Java
- JavaScript
- Pascal
- Python

It also checks the code analyzer path for Cyrillic identifiers and forbidden calls.

## PostgreSQL 18 note

The PostgreSQL 18 Docker image must mount the named data volume to:

```yaml
postgres-data:/var/lib/postgresql
```

Do not mount it to `/var/lib/postgresql/data`. PostgreSQL 18 images use a major-version-aware data directory layout under `/var/lib/postgresql`.

For disposable dev data, use:

```bash
./build.sh down -v
./build.sh up
```
