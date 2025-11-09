# python-runner — FastAPI раннер Python

Мини‑API для запуска пользовательского Python‑кода с ограничениями времени/памяти и нормализацией вывода.

## Запуск (локально)
```bash
cd python-runner
pip install -r requirements.txt  # если есть
uvicorn server:app --host 0.0.0.0 --port 8080
```
В Docker используется `Dockerfile`; по умолчанию сервис слушает `:8080`.

## Эндпоинты
- `POST /run`  
  ```json
  { "code": "print(input())", "input": "hello" }
  ```
  Ответ:
  ```json
  { "stdout": "hello\n", "stderr": "", "exitCode": 0, "error": null }
  ```
- `POST /run/tests` (синоним: `/run-tests`)  
  ```json
  { "code": "print(input())", "tests": [{ "input": "a", "expectedOutput": "a\n" }] }
  ```
  Ответ:
  ```json
  { "results": [ { "input": "a", "expectedOutput": "a\n", "actualOutput": "a\n", "passed": true } ] }
  ```

## Лимиты
- CPU: ~3 сек, RAM: 256 MB (через `resource`).
- Пустой ввод автоматически заменяется на `"\n"`, чтобы `input()` не падал с `EOFError`.
- Ограничение на размер вывода: 1 МБ.

## Нормализация
- Переводы строк `\r\n` → `\n`, усечение слишком длинного вывода.
