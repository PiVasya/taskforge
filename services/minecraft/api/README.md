# taskforge-minecraft-api

Настоящая граница микросервиса для домена `minecraft`.

Миграционные копии старого монолита из рабочего дерева убраны. Активный код сервиса разложен по `Endpoints/`, `Services/`, `Contracts/`, `Domain/`, `Data/`, `Infrastructure/`, `Security/` и `Diagnostics/`.

БД сервиса: `taskforge_minecraft`.

EF Core migrations хранятся в этой папке и применяются владельцем сервиса.
