# files-api

MinIO/S3-backed file service.

## Security model

- `POST /api/files/images` — authenticated image upload. Normal users may upload only to `editor-images/`; editor/admin can upload private image-test references.
- `POST /api/internal/files/images` — internal upload for services/workers, protected by `X-Internal-Key`.
- `GET /api/files/{key}` — public read endpoint **only** for public prefixes (`editor-images/`, `public/`). It intentionally returns 404 for judge/private prefixes such as `image-tests/`.
- `GET /api/private-files/{key}` — authenticated private read endpoint. Editors/admins can read all; normal users can read their own `image-tests/submissions/{assignmentId}/{userId}/...` artifacts. `image-tests/reference/` is editor/admin only.
- `GET /api/internal/files/{key}` — internal read endpoint for services/workers, protected by `X-Internal-Key`.
- `GET /api/files` — editor/admin metadata list only.

## Upload validation

User-facing image uploads are limited by `Files__MaxUploadBytes` / `FILES_MAX_UPLOAD_BYTES` and default to 10 MB. The service validates raster image magic bytes and intentionally rejects SVG for user uploads.

This is still a hotfix-level ACL model. Full object-level ownership checks for `agent-conversations/` should later be delegated to `ai-api` or backed by shared metadata/ACL records.
