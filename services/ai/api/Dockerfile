FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY services/ai/api/TaskForge.Ai.Api.csproj services/ai/api/
RUN dotnet restore services/ai/api/TaskForge.Ai.Api.csproj
COPY services/ai/api services/ai/api
RUN dotnet publish services/ai/api/TaskForge.Ai.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "TaskForge.Ai.Api.dll"]
