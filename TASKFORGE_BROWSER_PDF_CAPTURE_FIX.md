# TaskForge Browser PDF / crawler capture fix

## Problem

The first self-discovery runtime test on the 390x844 viewport failed with `BROWSER_RENDER_FAILED` because `Page.PdfAsync` received `292.5pt` as the paper width. Playwright documents PDF dimension units as `px`, `in`, `cm`, or `mm`.

The crawler capture path also treated the optional PDF compatibility wrapper as mandatory, so a PDF-only failure prevented otherwise valid snapshot JSON and Chromium PNG artifacts from being published.

## Fix

- PDF wrapper dimensions now use the captured PNG dimensions directly in `px`.
- CSS `@page`, wrapper HTML, and `PagePdfOptions` use the same width/height and `Scale = 1`.
- Public crawler captures treat PDF as optional. Snapshot JSON and authoritative PNG are still published if PDF generation fails.
- Artifact manifests expose `HasPdf`; `render.pdf` exists only when the wrapper was generated.
- The crawler smoke test requires snapshot + PNG and validates PDF when it is advertised.
- CI/security checks reject `pt` PDF dimensions and protect the optional-PDF fallback.

No database model or migration was changed.
