# AI migration notes

Под этот набросок **нужно сгенерировать и применить миграцию** или перенести изменения вручную SQL-скриптом.

## Что меняется в модели

Новые таблицы:

- `AiJobs`
- `AiJobFiles`
- `AiGeneratedAssignmentDrafts`
- `AiSubmissionReviews`
- `AiUserRiskReports`
- `AiAssignmentInsights`

## EF-команды

```bash
cd taskforge
dotnet restore
dotnet ef migrations add AddAiScaffolding --project taskforge.csproj --startup-project taskforge.csproj
dotnet ef database update --project taskforge.csproj --startup-project taskforge.csproj
```

Если ты предпочитаешь писать миграцию сам, ориентируйся на `ApplicationDbContext` и папку `Data/Models/Entities/AI`.
