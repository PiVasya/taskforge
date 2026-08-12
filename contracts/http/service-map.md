# HTTP ownership map

This file describes the current public/internal route ownership. Some prefixes are intentionally split; do not route by prefix alone when a more specific gateway rule exists.

| Route group | Owner service |
|---|---|
| `/api/auth/*`, `/api/users/*`, `/api/profile/*`, `/api/me/ui-settings/*` | identity |
| `/api/courses/*`, `/api/groups/*` | education |
| `/api/learning/*`, `/api/conspects/*` | content |
| assignment metadata/edit/solve-shell routes under `/api/assignments/*`, plus `/api/task-tests/*`, `/api/math-tasks/*`, task activity | tasks / assignment-api |
| `POST /api/assignments/{id}/submit`, `/api/me/solutions*`, top solutions, `/api/solutions/*`, `/api/judge/*`, `/api/leaderboard/*`, `/api/badges/*`, `/api/quotas/*`, `/api/me/quotas` | solutions |
| `/api/compiler/*`, `/api/image-runners/*`, `/api/execution/*` | execution |
| `/api/agent/*`, `/api/admin/ai/account-manager/*`, `/api/internal/agent/*` | ai-api |
| `/.well-known/taskforge-ai.json`, `/.well-known/taskforge-ai-browser.json`, `/llms.txt`, `/ai-access`, `/ai-browser`, `/api/site/*`, `/api/browser/*`, `/api/ai/browser/*`, `/ai-artifacts/*` | browser-api |
| `/api/support/*` | support |
| `/api/minecraft/*` | minecraft |
| `/api/files/*` | files |
| `/api/notifications/*`, telegram notification adapter | notifications |
| `/api/activity/*`, `/api/admin/analytics/*`, `/api/system-status/*` | observability |

The old `/api/internal-agent/*` prefix is obsolete. Worker coordination uses `/api/internal/agent/*`.
