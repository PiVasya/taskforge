FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY services/notifications/api/TaskForge.Notifications.Api.csproj services/notifications/api/
RUN dotnet restore services/notifications/api/TaskForge.Notifications.Api.csproj
COPY services/notifications/api services/notifications/api
RUN dotnet publish services/notifications/api/TaskForge.Notifications.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "TaskForge.Notifications.Api.dll"]
