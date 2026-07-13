# Development logging policy

TaskForge is currently in active development. All project images and Compose services must run with detailed TaskForge diagnostics enabled unless the user explicitly asks to switch to quiet logging.

## Mandatory current defaults

Both Docker workflows contain:

```yaml
env:
  TASKFORGE_BUILD_DEBUG_LOGS: "1"
```

Every project Dockerfile also defaults to:

```dockerfile
ARG TASKFORGE_DEBUG_LOGS=1
ENV TASKFORGE_DEBUG_LOGS=${TASKFORGE_DEBUG_LOGS}
```

Development and production Compose templates currently pass:

```yaml
TASKFORGE_DEBUG_LOGS: ${TASKFORGE_DEBUG_LOGS:-1}
```

The corresponding `.env.example` files contain:

```dotenv
TASKFORGE_DEBUG_LOGS=1
```

This means GitHub Actions builds noisy development images, local Docker builds are noisy by default, and Compose does not accidentally override the image with quiet logging.

## Hard change rule

Do not change any of these values to `0`, remove development probes, or quiet service log levels unless the user explicitly asks to change the logging policy. An optimization, cleanup, production deployment update, or unrelated refactor is not permission to disable logs.

When the user eventually requests quiet images, update the workflow build switch, Dockerfile defaults, Compose defaults, environment templates, this document, and the project AI rules together so the policy remains consistent.

Manual `workflow_dispatch` keeps a `debug_logs` choice:

```text
1 = development diagnostics enabled
0 = quiet images, only when explicitly requested
```

Manual runs rebuild all images by default unless an explicit image list is supplied.

## What is logged

### Gateway

The gateway emits request and upstream timing details, trace identifiers, host, method, URI, status, user agent, and referer. It forwards TaskForge trace headers to backend services.

### ASP.NET APIs

`TaskForgeDebugDiagnostics` records inbound and outbound requests, response status and duration, relevant business identifiers, user-name enrichment chains, semantic mismatches, and exceptions. Obvious secrets are redacted.

### Runners and analyzers

Runners record request boundaries, execution timing, and result summaries. The image analyzer records file metadata, thresholds, similarity values, and pass/fail results. The code analyzer keeps verbose rule-scan diagnostics while the switch is enabled.

## Reading a trace

Start with one request or trace id and follow it through the browser, gateway, API, downstream HTTP calls, workers, and analyzers. Useful prefixes include:

```text
TFDBG-FRONT
TFDBG GATEWAY
TFDBG IN START
TFDBG OUT START
TFDBG USERS ASK
TFDBG USERS GOT
TFDBG USERS WRONG
TFDBG OUT SEMANTIC-MISMATCH
```
