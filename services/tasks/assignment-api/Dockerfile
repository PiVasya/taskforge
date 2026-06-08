FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY services/tasks/assignment-api/TaskForge.Tasks.Api.csproj services/tasks/assignment-api/
RUN dotnet restore services/tasks/assignment-api/TaskForge.Tasks.Api.csproj
COPY services/tasks/assignment-api services/tasks/assignment-api
RUN dotnet publish services/tasks/assignment-api/TaskForge.Tasks.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "TaskForge.Tasks.Api.dll"]
