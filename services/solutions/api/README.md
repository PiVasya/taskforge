# taskforge-solutions-api

Настоящая граница микросервиса для домена `solutions`.

Миграционные копии старого монолита из рабочего дерева убраны. Активный код сервиса разложен по `Endpoints/`, `Services/`, `Contracts/`, `Domain/`, `Data/`, `Infrastructure/`, `Security/` и `Diagnostics/`.

БД сервиса: `taskforge_solutions`.

EF Core migrations хранятся в этой папке и применяются владельцем сервиса.
