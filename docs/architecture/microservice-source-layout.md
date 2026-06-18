# Microservice source layout

Микросервисы не должны хранить маршруты, DTO, вспомогательную бизнес-логику и инфраструктурные расширения в одном `Program.cs`.

Базовая структура API-сервиса:

```text
Program.cs                    # только композиция приложения: DI, middleware, миграции, Swagger, security, запуск
Endpoints/                    # минимальные API-маршруты, сгруппированные как контроллеры
  Composition/                # единая точка подключения всех route-групп сервиса
  ServiceInfo/                # health/service-info ручки
  <Area>/                     # доменная группа маршрутов: Auth, Profile, Assignments, TaskTests и т.п.
    <Area>Endpoints.cs
Contracts/                    # request/response/spec DTO, используемые HTTP-контрактами
  Requests/
  Responses/
  Specs/
  SupportTypes/
Domain/                       # доменные сущности сервиса
Data/                         # DbContext и persistence-конфигурация сервиса
Services/                     # прикладная логика и endpoint-support, вынесенные из маршрутов
  Access/                     # проверки доступа и обращения к соседним сервисам
  Common/                     # общие helper-методы конкретного сервиса
  Mapping/                    # сборка DTO/представлений
  Serialization/              # JSON-нормализация/импорт/экспорт
  Results/                    # единообразные HTTP-ответы/ошибки
  Image/, Math/, Testing/     # предметные helper-сервисы, если нужны сервису
Infrastructure/               # кэширование, внешние клиенты, системные расширения
  Caching/                    # TaskForgeCache и cache helpers
Security/                     # request security/JWT helpers
Diagnostics/                  # debug diagnostics и middleware
Hubs/                         # SignalR hubs, если сервис их использует
Migrations/                   # EF Core migrations
```

Правила для будущих правок:

1. `Program.cs` не раздувать. Он только собирает приложение.
2. Новые HTTP-ручки добавлять в подходящую папку `Endpoints/<Area>/`.
3. DTO не держать в endpoint-файлах — только `Contracts/Requests`, `Contracts/Responses`, `Contracts/Specs`.
4. Повторяемую и предметную логику выносить из `Endpoints/` в `Services/` с отдельным namespace `*.Services.<Area>`.
5. Инфраструктурные расширения не держать в корне сервиса — использовать `Infrastructure/`.
6. Старые монолитные снапшоты `extracted/`, `docs/original/`, `scripts/original/` в рабочем дереве не хранятся. Если понадобится история — брать из git/архива, а не таскать мусор внутри сервиса.

## Long file policy

После cleanup-а активная бизнес-логика микросервисов не должна возвращаться к файлам на 1000+ строк. Если файл начинает разрастаться, его нужно дробить по сценарию или ответственности:

- hosted-service lifecycle отдельно от message/callback flow;
- endpoint routing отдельно от services;
- parsing/serialization отдельно от business decisions;
- policy/analyzer helpers отдельно от runner/http pipeline;
- DTO/records отдельно от сценарных классов, если они разрастаются.

Текущий проход дополнительно разделил AI worker, execution worker и Telegram quiz bot, где после первичного разнесения ещё оставались файлы по 700-1400 строк.
