# TaskForge agent self-discovery update

This update makes the existing Browser API discoverable and usable by restricted automated clients that know only the public TaskForge domain.

## Root-domain discovery

The main frontend `index.html` now advertises, without JavaScript:

- `/ai-access`
- `/.well-known/taskforge-ai.json`
- `/llms.txt`
- `/api/site/info`

A normal browser still loads React as before. The additional content is in the `noscript` fallback and `<link rel="alternate">` metadata.

## `/ai-access`

`GET /ai-access` is a plain HTML, no-JavaScript crawler entry point served by `browser-api` through the gateway.

It contains direct links to public TaskForge route captures for common mobile and desktop viewports. It deliberately lists only routes that are anonymous, non-dynamic and non-redirecting.

## Crawler-friendly capture chain

Example:

`GET /api/site/agent/capture/main/390/844/full/`

The Browser API opens the selected TaskForge page in the real Chromium runtime, creates:

- semantic snapshot JSON;
- authoritative PNG screenshot;
- PDF compatibility wrapper;

and stores them in Redis under a content-addressed SHA-256 artifact id for a short TTL.

The response is ordinary HTML containing stable path-only links such as:

- `/ai-artifacts/<id>/snapshot.json`
- `/ai-artifacts/<id>/render.png`
- `/ai-artifacts/<id>/render.pdf`

This avoids requiring a user to paste a query-string render URL to a restricted fetcher.

The capture HTML also lists safe same-origin links discovered in the rendered page as new capture links, so a GET-only crawler can continue navigating public TaskForge pages without constructing Browser API query strings itself.

## Security boundary

Public crawler artifacts can only be created anonymously. Explicitly authenticated requests are rejected by the public artifact endpoint so private/authenticated page content cannot accidentally be persisted behind a public artifact URL.

The capture still uses the existing Browser URL policy, configured TaskForge origins, read-only request filtering, Redis rate limits, Nginx rate limits, Chromium capacity limits, and artifact size limits.

Artifacts are content-addressed, temporary, Redis-backed and contain only anonymous/read-only page state. No database migration is required.

## PDF parity

PNG remains the pixel-authoritative output.

The PDF compatibility wrapper now converts Chromium screenshot pixels to explicit PDF points using the CSS 96 dpi to PDF 72 dpi ratio (`72 / 96`) before creating the page. This removes ambiguity from using `px` directly as PDF page dimensions and should reduce subtle viewer-dependent stretching.

## Smoke test

`scripts/prod/check-browser-api.sh` now additionally verifies:

- `/ai-access`;
- crawler capture generation;
- artifact link discovery;
- stable snapshot/PNG/PDF artifact retrieval.

The pre-existing direct snapshot/render/session checks remain intact.
