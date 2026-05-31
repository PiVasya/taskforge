# User-facing error handling update

Goal: the UI must never show transport/runtime strings such as `Request failed with status code 401`, `Network Error`, raw nginx/html responses, or backend troubleshooting text.

## Changed

- `apps/web/src/api/http.js`
  - Added central API error normalization.
  - Maps HTTP statuses to user-facing Russian messages.
  - Detects and hides Axios/transport/HTML/nginx-looking messages.
  - Keeps technical details out of page-level errors.

- `apps/web-ct/src/api/http.js`
  - Same normalization as the main frontend.

- Login and registration pages
  - Use `getApiErrorMessage(...)` instead of reading `e.message`/raw response fields.
  - Wrong credentials now show a normal product message: `Неверный e-mail или пароль. Проверьте данные или зарегистрируйтесь.`

- `apps/web/src/utils/handleApiError.js`
  - Now uses normalized API messages.
  - Supports `handleApiError(e, { notify: false })` safely.
  - Does not leak Axios default messages.

- `apps/web/src/components/AppErrorPanel.jsx`
  - Sanitizes string errors before rendering.

- `services/identity/api/Program.cs`
  - `/api/auth/login` now returns structured 401 JSON for invalid credentials instead of an empty 401 body.
  - Auth-required endpoints return structured 401 JSON with a human-readable message.

- `scripts/verify-structure.sh`
  - Added check that frontend source does not contain technical transport messages exposed to users.

## Removed

- Old `MIGRATIONS_REQUIRED.md` markers from service folders.

## Verification

- `./scripts/verify-structure.sh`: ok
- `node --check apps/web/src/api/http.js`: ok
- `node --check apps/web-ct/src/api/http.js`: ok
- `node --check apps/web/src/utils/handleApiError.js`: ok
