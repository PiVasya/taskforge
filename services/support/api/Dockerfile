FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY services/support/api/TaskForge.Support.Api.csproj services/support/api/
RUN dotnet restore services/support/api/TaskForge.Support.Api.csproj
COPY services/support/api services/support/api
RUN dotnet publish services/support/api/TaskForge.Support.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "TaskForge.Support.Api.dll"]
