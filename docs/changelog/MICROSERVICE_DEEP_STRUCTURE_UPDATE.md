# Microservice deep structure update

Продолжение чистки микросервисов после первичного выноса логики из `Program.cs`.

## Что изменено

- Endpoint-файлы больше не лежат одной плоской кучей в `Endpoints/`.
  Теперь они разложены по папкам доменных групп:
  - `Endpoints/Assignments/`
  - `Endpoints/TaskTests/`
  - `Endpoints/MathTasks/`
  - `Endpoints/ImageTests/`
  - `Endpoints/Auth/`
  - `Endpoints/Profile/`
  - `Endpoints/Internal/`
  - `Endpoints/ServiceInfo/`
  - `Endpoints/Composition/`

- `Contracts/` тоже перестал быть плоской папкой.
  Базовые contract-файлы перенесены в:
  - `Contracts/Requests/`
  - `Contracts/Responses/`
  - `Contracts/Specs/`
  - `Contracts/SupportTypes/`

- Файлы в `Services/` больше не являются кусками `namespace ...Endpoints`.
  Было плохо:

```text
Services/Common/AssignmentApiEndpoints.Helpers.cs
namespace TaskForge.Tasks.Api.Endpoints;
internal static partial class AssignmentApiEndpoints
```

  Стало нормальнее:

```text
Services/Common/AssignmentApiCommonService.cs
namespace TaskForge.Tasks.Api.Services.Common;
internal static class AssignmentApiCommonService
```

- Endpoint-файлы теперь используют service/helper-классы через `using static`, а не через один огромный partial-класс.

## Что это даёт

- `Endpoints/` теперь отвечает за HTTP-маршруты.
- `Services/` теперь хотя бы физически и по namespace отделён от endpoint-слоя.
- При открытии сервиса видна нормальная структура, близкая к легаси-монолиту: маршруты отдельно, сервисная логика отдельно, контракты отдельно.
- Старые helper-файлы с именами `*Endpoints.*Helpers.cs` удалены/переименованы.

## Что ещё можно улучшать дальше

- Самые жирные service-файлы всё ещё можно дополнительно делить на полноценные DI-сервисы.
- В части сервисов `Program.cs` ещё содержит startup-логику миграций/регистраций. Это уже не критический мусор, но следующим шагом можно вынести в `Extensions/` или `Infrastructure/Startup/`.
- Minimal API оставлен. Классические MVC Controllers специально не добавлялись, чтобы не ломать существующие маршруты и контракты.

## Проверка структуры

После правки в API-сервисах больше нет:

- `.cs` файлов прямо в корне `Endpoints/`;
- `.cs` файлов прямо в корне `Contracts/` для API-сервисов;
- service-файлов с namespace `*.Endpoints`;
- service-файлов с классами `*Endpoints.*Helpers.cs`.
