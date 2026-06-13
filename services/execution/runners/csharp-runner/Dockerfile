FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props global.json ./
COPY services/shared services/shared
COPY services/execution/runners/csharp-runner/csharp-runner.csproj services/execution/runners/csharp-runner/
RUN dotnet restore services/execution/runners/csharp-runner/csharp-runner.csproj
COPY services/execution/runners/csharp-runner services/execution/runners/csharp-runner
RUN dotnet publish services/execution/runners/csharp-runner/csharp-runner.csproj -c Release -o /out /p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update     && apt-get install -y --no-install-recommends curl ca-certificates     && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER 65534
ARG TASKFORGE_DEBUG_LOGS=0
ENV TASKFORGE_DEBUG_LOGS=${TASKFORGE_DEBUG_LOGS}
ENTRYPOINT if [ "${TASKFORGE_DEBUG_LOGS:-0}" = "1" ]; then echo "[taskforge-debug] logs=on service=csharp-runner"; exec env Logging__LogLevel__Default="${Logging__LogLevel__Default:-Debug}" Logging__LogLevel__Microsoft="${Logging__LogLevel__Microsoft:-Information}" Logging__LogLevel__Microsoft.AspNetCore="${Logging__LogLevel__Microsoft_AspNetCore:-Information}" Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command="${Logging__LogLevel__Microsoft_EntityFrameworkCore_Database_Command:-Information}" Logging__LogLevel__System.Net.Http.HttpClient="${Logging__LogLevel__System_Net_Http_HttpClient:-Information}" dotnet csharp-runner.dll; else echo "[taskforge-debug] logs=off service=csharp-runner"; exec env Logging__LogLevel__Default="${Logging__LogLevel__Default:-Information}" Logging__LogLevel__Microsoft="${Logging__LogLevel__Microsoft:-Warning}" Logging__LogLevel__Microsoft.AspNetCore="${Logging__LogLevel__Microsoft_AspNetCore:-Warning}" Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command="${Logging__LogLevel__Microsoft_EntityFrameworkCore_Database_Command:-Warning}" Logging__LogLevel__System.Net.Http.HttpClient="${Logging__LogLevel__System_Net_Http_HttpClient:-Warning}" dotnet csharp-runner.dll; fi
