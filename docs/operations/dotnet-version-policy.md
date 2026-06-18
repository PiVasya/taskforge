# .NET and C# version policy

TaskForge service projects target the current stable .NET LTS line:

- Target framework: `net10.0`
- C# language version: `14.0`
- Default SDK pin: `global.json` -> `10.0.300` with `rollForward=latestFeature`
- Container base images: `mcr.microsoft.com/dotnet/*:10.0`

The project intentionally avoids preview SDKs and preview NuGet packages for core runtime services. Patch updates inside the .NET 10 line are expected and should be taken through normal dependency updates.

## Upgrade rules

1. All active service `.csproj` files under `services/` must target `net10.0`.
2. Active .NET Dockerfiles must use `sdk:10.0`, `aspnet:10.0`, or `runtime:10.0`.
3. C# runner compiles user submissions with the stable C# 14 parser, not `LanguageVersion.Preview`.
4. EF Core packages must stay on the .NET 10-compatible EF Core 10 line.
5. PostgreSQL EF provider must stay on the Npgsql EF Core 10 line while services target .NET 10.

Архивные материалы старого монолита не хранятся в активном дереве проекта. Эти правила применяются ко всем runtime `.NET`-проектам внутри `services/`.
