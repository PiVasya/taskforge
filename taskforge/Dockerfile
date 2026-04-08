# Simple, reliable build (no native OpenCV dependencies required).

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy csproj and restore as distinct layers
COPY ["taskforge.csproj", "./"]
RUN dotnet restore "./taskforge.csproj"

# Copy everything else and publish
COPY . .
RUN dotnet publish "./taskforge.csproj" -c Release -o /app/out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/out .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

HEALTHCHECK --interval=10s --timeout=5s --start-period=35s --retries=18 CMD curl -fsS http://localhost:8080/health >/dev/null || exit 1

ENTRYPOINT ["dotnet", "taskforge.dll"]
