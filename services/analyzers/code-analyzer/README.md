# taskforge-code-analyzer

Небольшой сервис для **быстрой статической проверки** решений перед запуском (stage 1).

## Что делает
- Получает `language` + `source`.
- Удаляет комментарии и строковые литералы (чтобы не ловить запреты внутри строк/комментов).
- Ищет запрещённые конструкции (inline-asm, eval, запуск процессов, файловые/сетевые API и т.д.).

> MVP: поиск по подстрокам (быстро). Дальше можно улучшать до токенов/AST.

## API
- `GET /health` → `ok`
- `POST /analyze`

### Request
```json
{
  "language": "cpp",
  "source": "...",
  "extra_forbidden": [
    {"id":"no_sort","needle":"std::sort","description":"Запрещён std::sort"}
  ]
}
```

### Response
```json
{
  "ok": false,
  "errors": [
    {"code":"forbidden","message":"Запрещён inline-assembler (asm)","pattern_id":"c.asm"}
  ],
  "hits": [
    {"pattern_id":"c.asm","needle":"asm","position":123,"preview":"..."}
  ]
}
```
