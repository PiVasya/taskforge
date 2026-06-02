# MinIO and staged error update

This update fixes files-api to use MinIO/S3-compatible storage instead of local container disk.

## Files API

- Uses `AWSSDK.S3` and `IAmazonS3`.
- Upload endpoint writes image bytes to MinIO bucket `S3__Bucket`.
- Public editor images are returned through `/api/files/{key}`.
- Private links are returned through `/api/private-files/{key}`.
- Healthcheck verifies PostgreSQL and MinIO bucket readiness.
- Bucket is created automatically when possible.

## Error responses

User-facing unfinished operations no longer return `501 Not Implemented`.
They now return structured JSON with:

- `status`
- `code`
- `stage`
- `message`
- `detail` when useful

This is intended to make it clear which stage failed: request validation, MinIO connection, bucket check, object upload, DB metadata save, etc.

## Important

This does not magically complete every monolith business feature. It makes the failure mode honest and debuggable, while making file storage real through MinIO.
