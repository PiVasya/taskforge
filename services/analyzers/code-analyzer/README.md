# taskforge-code-analyzer

Небольшой сервис для **быстрой статической проверки** решений перед запуском (stage 1).

## Что делает
- Получает `language` + `source`.
- Удаляет комментарии и строковые литералы (чтобы не ловить запреты внутри строк/комментов).
- Ищет запрещённые конструкции (inline-asm, eval, запуск процессов, файловые/сетевые API и т.д.).
- Блокирует кириллицу в исполняемом коде, но разрешает её в комментариях и строковых литералах. Это особенно важно для Pascal/C++: русские имена переменных дают неочевидные ошибки компилятора.

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
    {"code":"forbidden","message":"Запрещён inline-assembler (asm)","pattern_id":"c.asm"},
    {"code":"cyrillic_in_code","message":"Кириллица разрешена в строках и комментариях, но запрещена в исполняемом коде: используйте латинские имена переменных, функций и классов.","pattern_id":"unicode.cyrillic_in_code"}
  ],
  "hits": [
    {"pattern_id":"c.asm","needle":"asm","position":123,"preview":"..."}
  ]
}
```
