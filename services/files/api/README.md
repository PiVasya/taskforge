# TaskForge Files API

Owns `taskforge_files` and stores file bytes in MinIO/S3-compatible object storage.

## Endpoints

- `POST /api/files/images` — upload editor image; JWT required.
- `GET /api/files/{key}` — public read endpoint for editor images, matching legacy monolith behavior.
- `GET /api/private-files/{key}` — private read endpoint; JWT required.
- `GET /api/files` — list recent file metadata; JWT required.

## Storage

Required configuration:

```text
S3__Endpoint=http://minio:9000
S3__AccessKey=taskforge
S3__SecretKey=...
S3__Bucket=taskforge-files
S3__Region=us-east-1
S3__UsePathStyle=true
```

The service creates the bucket automatically when possible.

## Error response format

Errors include a stage field to show where the failure happened:

```json
{
  "status": 503,
  "code": "MINIO_PUT_OBJECT_FAILED",
  "stage": "storage.put_object",
  "message": "MinIO принял подключение, но не смог сохранить объект.",
  "detail": "..."
}
```
