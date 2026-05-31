# csharp-runner — ASP.NET Core раннер C# (Roslyn in‑proc)

Принимает C#‑код, компилирует в памяти (Roslyn) и запускает в изолированном контексте с подменой `Console`.

## Запуск (локально)
```bash
cd csharp-runner
dotnet restore
dotnet run --project csharp-runner.csproj
# слушает :8080 (или как задано)
```

## Эндпоинты
Базовый маршрут: `/Run` (контроллер `RunController`), доступны оба варианта с и без ведущего слеша.
- `POST /run` и `POST /Run/run`  
  Тело:
  ```json
  { "code": "using System; class P{ static void Main(){ Console.WriteLine(Console.ReadLine()); } }", "input": "hey" }
  ```
  Ответ (`RunResponse`):
  ```json
  { "stdout": "hey\n", "stderr": "", "exitCode": 0, "error": null }
  ```
- `POST /run/tests` (`/Run/tests`)  
  Тело:
  ```json
  { "code": "...", "tests": [{ "input": "a", "expectedOutput": "a\n" }] }
  ```
  Ответ:
  ```json
  { "results": [ { "input": "a", "expectedOutput": "a\n", "actualOutput": "a\n", "passed": true } ] }
  ```

## Особенности исполнения
- Пустой ввод заменяется на хотя бы `"\n"`, чтобы `Console.ReadLine()` не вернул `null`.
- Подмена `Console.SetIn/SetOut`, сбор stdout/stderr, тайм‑аут выполнения.
- Компиляция с полным набором TPA‑сборок для устранения CS0012/System.Runtime.
