# Development logging switch

Debug logging is now a **workflow build switch**, not a deploy `.env` switch.

The reason is simple: these probes are intentionally noisy and are baked into the image at build time. Production compose files should not decide whether a container is a debug container; GitHub Actions builds either the noisy image or the quiet image.

## Current GitHub policy

`.github/workflows/develop-build.yml` currently contains:

```yaml
env:
  TASKFORGE_BUILD_DEBUG_LOGS: "1"
```

So the current `:develop` GHCR images are built with ultra development logs enabled.

To build quiet images later, change that workflow value to:

```yaml
env:
  TASKFORGE_BUILD_DEBUG_LOGS: "0"
```

and push. A workflow-file change forces a full rebuild because the value is baked into every image through Docker build args.

Manual `workflow_dispatch` also has a `debug_logs` choice:

```text
1 = noisy debug images
0 = quiet images
```

Manual runs rebuild all images by default unless you explicitly pass an `images` list.

## How it gets into containers

Every Dockerfile accepts:

```dockerfile
ARG TASKFORGE_DEBUG_LOGS=0
ENV TASKFORGE_DEBUG_LOGS=${TASKFORGE_DEBUG_LOGS}
```

The workflow passes:

```yaml
build-args: |
  TASKFORGE_DEBUG_LOGS=${{ github.event.inputs.debug_logs || env.TASKFORGE_BUILD_DEBUG_LOGS }}
```

Compose does **not** inject `TASKFORGE_DEBUG_LOGS` anymore. The running container uses the value baked into the image.

## What is logged when enabled

The logs are intentionally very detailed.

### Browser/front

The React API client logs each request and response to the browser console with the tag `TFDBG-FRONT`:

- request id;
- method and URL;
- params and request body, with obvious secrets redacted;
- response status and duration;
- extracted `id`, `displayName`, `fullName`, `firstName`, `lastName`, `email` values;
- semantic warning when ids arrive but names do not.

### Gateway

Nginx uses a debug access format with the prefix `TFDBG GATEWAY`:

- `$request_id`;
- host, method, URI and status;
- request time;
- upstream address, upstream status and upstream response time;
- user-agent and referer.

The gateway also forwards trace headers:

```text
X-Request-ID
X-TaskForge-Gateway-Request-Id
X-TaskForge-Gateway-Host
```

### ASP.NET APIs

Every ASP.NET API has `TaskForgeDebugDiagnostics` middleware:

- `TFDBG IN START` for inbound request metadata;
- `TFDBG IN BODY` for request payload snippets;
- `TFDBG IN DATA` for ids/names/emails/titles extracted from JSON;
- `TFDBG IN END` for status, duration and resolved user after auth middleware has run;
- `TFDBG IN RESPONSE` and `TFDBG IN RESPONSE-DATA` for response snippets and extracted business identifiers;
- `TFDBG IN EXCEPTION` for exception paths.

Outgoing `HttpClient` calls are wrapped too:

- `TFDBG OUT START` logs caller service, target host, URL and request body;
- `TFDBG OUT DATA` extracts ids/names/emails/titles from the outgoing payload;
- `TFDBG OUT END` logs status, duration and response body;
- `TFDBG OUT RESPONSE-DATA` extracts returned business values;
- `TFDBG OUT SEMANTIC-MISMATCH` fires when a user-summary call asked identity for user ids but got no names, missing ids or placeholders.

### User-name enrichment chain

The services that enrich user ids through `identity-api` now emit explicit semantic probes:

```text
[TFDBG USERS ASK] service=solutions-api target=identity-api count=3 ids=...
[TFDBG USERS SERVE] service=identity-api requested=3 returned=2 names=...
[TFDBG USERS GOT] service=solutions-api target=identity-api requested=3 found=2 missing=1 names=...
[TFDBG USERS WRONG] service=solutions-api target=identity-api message=asked_identity_for_real_user_names_but_mapping_is_incomplete_or_placeholder
```

This is the exact class of bug where one service asks for “Торопа Валерия”, but the UI eventually receives “Пользователь” or an id.

### Runners and analyzers

Go runners log:

- `TFDBG RUNNER IN START` with trace id, method, path, query and request body snippet;
- `TFDBG RUNNER IN END` with status, duration and response snippet.

The image analyzer logs:

- request start/end;
- file names and byte sizes;
- threshold;
- CLIP/pHash/combined similarity;
- pass/fail result.

The Rust code analyzer keeps its verbose rule scan logs only when `TASKFORGE_DEBUG_LOGS=1`.

## How to read it

Start from one request id or trace id and follow it through:

```text
TFDBG-FRONT request:start id=front-...
TFDBG GATEWAY request_id=...
TFDBG IN START trace=...
TFDBG OUT START trace=... caller=solutions-api target=identity-api
TFDBG USERS ASK service=solutions-api target=identity-api
TFDBG USERS SERVE service=identity-api
TFDBG USERS GOT service=solutions-api
TFDBG IN RESPONSE-DATA trace=...
```

For the current leaderboard/profile/name bugs, the most important lines are:

```text
TFDBG USERS WRONG
TFDBG OUT SEMANTIC-MISMATCH
TFDBG IN RESPONSE-DATA
```
