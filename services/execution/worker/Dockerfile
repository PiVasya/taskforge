FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY services/execution/worker/TaskForge.Execution.Worker.csproj services/execution/worker/
RUN dotnet restore services/execution/worker/TaskForge.Execution.Worker.csproj
COPY services/execution/worker services/execution/worker
RUN dotnet publish services/execution/worker/TaskForge.Execution.Worker.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "TaskForge.Execution.Worker.dll"]
