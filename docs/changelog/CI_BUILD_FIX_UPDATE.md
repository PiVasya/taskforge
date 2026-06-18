# CI build fix after microservice restructuring

Fixed compile issues introduced during the source-layout refactor.

## Fixed

- Qualified ASP.NET Minimal API result helpers as `Microsoft.AspNetCore.Http.Results` where service namespaces named `Results` shadowed the framework `Results` type.
- Qualified BCL math calls as `System.Math` where `TaskForge.Tasks.Api.Services.Math` shadowed `System.Math`.
- Restored the missing `NormalizeLanguage` switch body in `services/execution/worker/Worker.cs`.
- Restored the missing `ReadLong` helper body in `services/bots/telegram-quiz-bot/Bot/Teacher/TeacherBotHostedService.Quizzes.cs`.
- Removed remaining legacy folders from the submitted archive:
  - `docs/original/`
  - `scripts/original/`
  - `docs/source-map/`
  - `services/**/extracted/`
- Updated the optional Docker build-context audit so service-local cache helpers are not reported as missing shared copies.

## Verified locally without Docker/.NET SDK

The current environment does not provide `dotnet` or `docker`, so full image builds were not possible here. The available repository checks pass:

```text
Workflow integrity OK: 32 matrix images, 32 Dockerfiles, 32 prod images, full-rebuild matrix aligned.
Workflow matrix integrity OK: 32 images, 32 Dockerfiles.
Docker build-context audit OK: 32 workflow image entries checked.
```

Additional static checks:

```text
Unqualified Results/Math shadowing patterns: 0
Working source files >= 600 lines: 0
Stripped C# brace-balance mismatches: 0
```
