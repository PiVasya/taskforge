# cpp-runner — FastAPI раннер C++ (g++)

Компилирует и запускает C++‑код пользователя в контейнере, ограничивает ресурсы и нормализует вывод.

## Запуск (локально)
```bash
cd cpp-runner
pip install -r requirements.txt  # если есть
uvicorn server:app --host 0.0.0.0 --port 8080
```
Либо через Docker (`Dockerfile`), порт `:8080`.

## Эндпоинты
- `POST /run`  
  ```json
  { "code": "#include <iostream>\nint main(){std::string s;std::getline(std::cin,s);std::cout<<s;}", "input": "hi" }
  ```
- `POST /run/tests` (синоним: `/run-tests`)  
  ```json
  { "code": "...", "tests": [{ "input": "x", "expectedOutput": "x\n" }] }
  ```
Ответы аналогичны python‑runner.

## Лимиты
- Время CPU: по умолчанию ~3 сек, RAM: 256 MB (через `resource`).
- Ограничение вывода: 1 МБ.
- Пустой ввод заменяется на `"\n"`, чтобы `getline` не возвращал `EOF`.

## Примечания
- Нормализация `\r\n` → `\n`.
- Компиляция из временных файлов, запуск через `subprocess` с тайм‑аутом.
