# MinIO and monolith-parity hardening

## Files API

`files-api` no longer uses local container disk storage. It now uses MinIO/S3-compatible storage through `AWSSDK.S3`.

Implemented endpoints:

- `POST /api/files/images` uploads image bytes to MinIO bucket `S3__Bucket`.
- `GET /api/files/{key}` downloads public editor images from MinIO.
- `GET /api/private-files/{key}` downloads private files from MinIO after JWT auth.
- `/health/ready` verifies PostgreSQL and MinIO bucket readiness.

The upload response contains:

- `key`
- `url=/api/files/{key}`
- `privateUrl=/api/private-files/{key}`
- `storage=minio`

The service creates the bucket automatically when possible.

## Compose settings

Dev/prod compose now passes:

- `S3__Endpoint`
- `S3__AccessKey`
- `S3__SecretKey`
- `S3__Bucket`
- `S3__Region`
- `S3__UsePathStyle`

`Storage__LocalPath` and local `files-data` volume are removed.

## Error semantics

Removed user-facing `501 Not Implemented` responses from active API services.

Unfinished operations now return structured JSON with:

- `status`
- `code`
- `stage`
- `message`
- optional `detail`

Examples of stages:

- `request.validation`
- `request.form`
- `storage.connection`
- `storage.bucket`
- `storage.put_object`
- `database.metadata_save`
- `tasks.test_attempt_start`
- `solutions.badges.award`
- `ai.provider`

This lets frontend show an understandable message and lets backend logs point to the exact failed phase.

## Security

Public editor images under `/api/files/{key}` follow the monolith behavior and are public-read through the application endpoint.
Private files under `/api/private-files/{key}` require JWT.
Uploads still require JWT.
