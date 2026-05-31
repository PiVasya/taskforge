# HTTP ownership map

| Route group | Owner service |
|---|---|
| `/api/auth/*`, `/api/users/*`, `/api/profile/*`, `/api/me/ui-settings/*` | identity |
| `/api/courses/*`, `/api/groups/*` | education |
| `/api/learning/*`, `/api/conspects/*` | content |
| `/api/assignments/*`, `/api/task-tests/*`, `/api/math-tasks/*`, `/api/quiz/*` | tasks |
| `/api/solutions/*`, `/api/judge/*`, `/api/leaderboard/*`, `/api/badges/*`, `/api/quotas/*` | solutions |
| `/api/compiler/*`, `/api/image-runners/*`, `/api/execution/*` | execution |
| `/api/agent/*`, `/api/internal-agent/*` | ai |
| `/api/support/*` | support |
| `/api/minecraft/*` | minecraft |
| `/api/files/*` | files |
| `/api/notifications/*`, telegram notification adapter | notifications |
| `/api/activity/*`, `/api/admin/analytics/*`, `/api/system-status/*` | observability |
